using DriveDuplicateFinder.Data.Sqlite;
using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Builds the existing full-group review contract from one confirmed SQLite group.
/// This adapter is local-only: it has no DriveService and cannot execute cleanup.
/// </summary>
public sealed class PersistedCleanupGroupReviewAdapter
{
    public const int MaximumMembersPerGroup = 2_000;
    private const int ReadPageSize = 500;
    private const int MaximumAncestorFoldersPerGroup = 20_000;
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    private readonly DriveFileCacheRepository _files;
    private readonly ReviewStateRepository _reviews;
    private readonly ScanSessionRepository _sessions;
    private readonly GoogleDriveFileService _fileMapper;
    private readonly SqliteConnectionFactory _connectionFactory;

    public PersistedCleanupGroupReviewAdapter(
        SqliteConnectionFactory connectionFactory,
        GoogleDriveFileService fileMapper)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _fileMapper = fileMapper ?? throw new ArgumentNullException(nameof(fileMapper));
        _files = new DriveFileCacheRepository(connectionFactory);
        _reviews = new ReviewStateRepository(connectionFactory);
        _sessions = new ScanSessionRepository(connectionFactory);
    }

    public async Task<PersistedCleanupGroupReview> BuildAsync(
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();

        ReviewGroupPersistenceState initialState = await _reviews.GetGroupStateAsync(identity, cancellationToken)
            ?? throw new InvalidOperationException("El grupo no está registrado en el inventario Full vigente.");
        if (!initialState.IsReviewConfirmed || initialState.ReviewedWithoutCleanup)
            throw new InvalidOperationException("El grupo no tiene una confirmación operativa vigente.");

        // Check the cap before querying or materializing any member rows.
        if (initialState.MemberCount > MaximumMembersPerGroup)
            throw new CleanupGroupMemberLimitExceededException(initialState.MemberCount, MaximumMembersPerGroup);
        if (initialState.MemberCount < 2)
            throw new InvalidOperationException("La identidad no corresponde a un grupo de duplicados completo.");

        ScanSessionRecord session = await _sessions.GetByIdAsync(identity.Inventory.ScanId, cancellationToken)
            ?? throw new InvalidOperationException("No se encontró la sesión del inventario.");
        if (session.ScanType != ScanType.Full || session.Status != ScanStatus.Completed ||
            !string.Equals(session.AccountKey, identity.Inventory.AccountKey, StringComparison.Ordinal) ||
            !string.Equals(session.ScopeKey, identity.Inventory.ScopeKey, StringComparison.Ordinal))
            throw new InvalidOperationException("La sesión no es Full Completed para la cuenta y alcance solicitados.");

        ReviewDecisionCounts initialCounts = await _reviews.GetGroupDecisionCountsAsync(identity, cancellationToken);
        ValidateDecisionCounts(initialState.MemberCount, initialCounts);
        if (initialCounts.KeepCount == 0 || initialCounts.CandidateCount == 0)
            throw new InvalidOperationException("Se requiere al menos un Keep y un candidato explícito para adaptar el grupo.");

        var memberRecords = new List<DriveFileCacheRecord>(checked((int)initialState.MemberCount));
        var decisionsByFileId = new Dictionary<string, DuplicateFileDecision>(checked((int)initialState.MemberCount), StringComparer.Ordinal);
        for (int offset = 0; offset < initialState.MemberCount; offset += ReadPageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DuplicateGroupMembersPage members = await _files.GetDuplicateGroupMembersPageAsync(
                identity, offset, ReadPageSize, cancellationToken);
            PersistedFileDecisionPage decisions = await _reviews.GetDecisionsPageAsync(
                identity, offset, ReadPageSize, cancellationToken);
            if (members.TotalCount != initialState.MemberCount || decisions.TotalCount != initialState.MemberCount ||
                members.Items.Count != decisions.Items.Count || members.Offset != offset || decisions.Offset != offset)
                throw new InvalidOperationException("Los miembros y las decisiones no forman páginas completas coincidentes; no se creó la revisión.");

            for (int index = 0; index < members.Items.Count; index++)
            {
                DriveFileCacheRecord file = members.Items[index];
                PersistedFileDecision decision = decisions.Items[index];
                if (!string.Equals(file.FileId, decision.FileId, StringComparison.Ordinal) ||
                    !string.Equals(file.AccountKey, identity.Inventory.AccountKey, StringComparison.Ordinal) ||
                    !string.Equals(file.ScopeKey, identity.Inventory.ScopeKey, StringComparison.Ordinal) ||
                    !string.Equals(file.LastSeenScanId, identity.Inventory.ScanId, StringComparison.Ordinal) ||
                    file.IsTrashed || file.IsRemoved || file.SizeBytes != identity.SizeBytes ||
                    !string.Equals(file.Md5Checksum, identity.NormalizedChecksum, StringComparison.OrdinalIgnoreCase) ||
                    !decisionsByFileId.TryAdd(file.FileId, decision.Decision))
                    throw new InvalidOperationException("Se detectó un miembro o FileId ajeno/inconsistente con el grupo; no se creó la revisión.");

                memberRecords.Add(file);
            }
        }

        if (memberRecords.Count != initialState.MemberCount || decisionsByFileId.Count != initialState.MemberCount)
            throw new InvalidOperationException("La lectura no recuperó todos los miembros y decisiones del grupo.");

        ReviewDecisionCounts loadedCounts = CountDecisions(decisionsByFileId);
        ValidateDecisionCounts(initialState.MemberCount, loadedCounts);
        if (loadedCounts != initialCounts || loadedCounts.KeepCount == 0 || loadedCounts.CandidateCount == 0)
            throw new InvalidOperationException("Las decisiones cambiaron durante la carga o ya no contienen Keep y candidato explícitos.");

        ReviewGroupPersistenceState currentState = await _reviews.GetGroupStateAsync(identity, cancellationToken)
            ?? throw new InvalidOperationException("El grupo dejó de pertenecer al inventario vigente durante la carga.");
        ReviewDecisionCounts currentCounts = await _reviews.GetGroupDecisionCountsAsync(identity, cancellationToken);
        if (!currentState.IsReviewConfirmed || currentState.ReviewedWithoutCleanup ||
            currentState.LastModifiedAtUtc != initialState.LastModifiedAtUtc || currentCounts != initialCounts)
            throw new InvalidOperationException("La confirmación o las decisiones cambiaron durante la carga; vuelve a revisar el grupo.");

        var recordsForMapping = new List<DriveFileCacheRecord>(memberRecords);
        IReadOnlyList<DriveFileCacheRecord> ancestors = await LoadAncestorFoldersAsync(
            session, memberRecords, cancellationToken);
        recordsForMapping.AddRange(ancestors);
        IReadOnlyList<DriveFileInfo> mapped = _fileMapper.BuildResultFromCache(
            recordsForMapping, session.RootFolderId, cancellationToken).ComparableFiles;
        if (mapped.Count != memberRecords.Count ||
            mapped.Select(file => file.Id).Distinct(StringComparer.Ordinal).Count() != memberRecords.Count)
            throw new InvalidOperationException("No se pudieron reconstruir exactamente los metadatos de todos los miembros del grupo.");

        var mappedById = mapped.ToDictionary(file => file.Id, StringComparer.Ordinal);
        var orderedIds = memberRecords.Select(file => file.FileId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var groupFiles = new DriveFileInfo[orderedIds.Length];
        for (int index = 0; index < orderedIds.Length; index++)
        {
            string fileId = orderedIds[index];
            if (!mappedById.TryGetValue(fileId, out DriveFileInfo? file) || !decisionsByFileId.ContainsKey(fileId))
                throw new InvalidOperationException("Un FileId dejó de coincidir entre la caché, el grupo y las decisiones.");
            groupFiles[index] = file;
        }

        var group = new DuplicateGroup
        {
            // GroupNumber is display-only; the stable identity remains size + normalized checksum + inventory.
            GroupNumber = 0,
            FileSize = identity.SizeBytes,
            Md5Checksum = identity.NormalizedChecksum,
            Files = Array.AsReadOnly(groupFiles)
        };
        var review = new DuplicateGroupReview
        {
            StableId = DuplicateReviewService.CreateStableGroupId(group.FileSize, group.Md5Checksum),
            Group = group,
            ReviewedWithoutCleanup = currentState.ReviewedWithoutCleanup,
            Notes = currentState.Notes,
            LastModifiedUtc = currentState.LastModifiedAtUtc,
            Status = currentState.Status
        };
        foreach (string fileId in orderedIds)
            review.DecisionsByFileId.Add(fileId, decisionsByFileId[fileId]);

        // Recheck inventory/group confirmation after potentially expensive path reconstruction.
        ReviewGroupPersistenceState finalState = await _reviews.GetGroupStateAsync(identity, cancellationToken)
            ?? throw new InvalidOperationException("El inventario dejó de estar vigente al finalizar la adaptación.");
        if (!finalState.IsReviewConfirmed || finalState.ReviewedWithoutCleanup ||
            finalState.LastModifiedAtUtc != initialState.LastModifiedAtUtc)
            throw new InvalidOperationException("La confirmación dejó de ser vigente al finalizar la adaptación.");

        return new PersistedCleanupGroupReview(identity, review, finalState.LastModifiedAtUtc,
            initialState.MemberCount, loadedCounts.KeepCount, loadedCounts.CandidateCount, loadedCounts.UndecidedCount);
    }

    private async Task<IReadOnlyList<DriveFileCacheRecord>> LoadAncestorFoldersAsync(
        ScanSessionRecord session,
        IReadOnlyCollection<DriveFileCacheRecord> members,
        CancellationToken cancellationToken)
    {
        var queried = new HashSet<string>(StringComparer.Ordinal);
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        var folders = new List<DriveFileCacheRecord>();
        var pending = new Queue<string>(members.SelectMany(file => file.ParentIds)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<string>(ReadPageSize);
            while (pending.Count > 0 && batch.Count < ReadPageSize)
            {
                string id = pending.Dequeue();
                if (queried.Add(id)) batch.Add(id);
            }
            if (batch.Count == 0) continue;

            IReadOnlyList<DriveFileCacheRecord> fetched = await DriveFileCacheRepository.GetCompletedFoldersByIdsAsync(
                connection, session.ScanId, session.AccountKey, session.ScopeKey, batch, cancellationToken);
            if (folders.Count + fetched.Count > MaximumAncestorFoldersPerGroup)
                throw new InvalidOperationException($"El grupo requiere más de {MaximumAncestorFoldersPerGroup:N0} carpetas antecesoras; no se creó una revisión parcial.");

            foreach (DriveFileCacheRecord folder in fetched)
            {
                if (!loaded.Add(folder.FileId)) continue;
                folders.Add(folder);
                foreach (string parentId in folder.ParentIds)
                    if (!string.IsNullOrWhiteSpace(parentId) && !queried.Contains(parentId)) pending.Enqueue(parentId);
            }
        }

        return folders;
    }

    private static ReviewDecisionCounts CountDecisions(IReadOnlyDictionary<string, DuplicateFileDecision> decisions) =>
        new(decisions.Count,
            decisions.Values.LongCount(value => value == DuplicateFileDecision.Keep),
            decisions.Values.LongCount(value => value == DuplicateFileDecision.CandidateForTrash),
            decisions.Values.LongCount(value => value == DuplicateFileDecision.Undecided));

    private static void ValidateDecisionCounts(long expectedMembers, ReviewDecisionCounts counts)
    {
        if (counts.MemberCount != expectedMembers ||
            counts.KeepCount + counts.CandidateCount + counts.UndecidedCount != expectedMembers)
            throw new InvalidOperationException("Las decisiones SQLite no cubren exactamente la membresía del grupo.");
    }
}

/// <summary>A local-only result; it is never accepted by the execution service.</summary>
public sealed record PersistedCleanupGroupReview(
    DuplicateGroupIdentity Identity,
    DuplicateGroupReview Review,
    DateTimeOffset ConfirmedAtUtc,
    long MemberCount,
    long KeepCount,
    long CandidateCount,
    long UndecidedCount)
{
    public bool RemotePreflightPending => true;
    public bool ExecutionEnabled => false;

    /// <summary>
    /// Creates a read-only probe containing one retained anchor and only explicit candidates.
    /// Undecided and additional Keep files are never added to the candidate set.
    /// This projection must not be used as an execution plan.
    /// </summary>
    public DuplicateGroupReview CreateReadOnlyPreflightReview()
    {
        string[] keepIds = Review.DecisionsByFileId
            .Where(pair => pair.Value == DuplicateFileDecision.Keep)
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        string[] candidateIds = Review.DecisionsByFileId
            .Where(pair => pair.Value == DuplicateFileDecision.CandidateForTrash)
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (keepIds.Length == 0 || candidateIds.Length == 0)
            throw new InvalidOperationException("El preflight requiere al menos un Keep y un candidato explícitos.");

        var filesById = Review.Group.Files.ToDictionary(file => file.Id, StringComparer.Ordinal);
        string anchorKeepId = keepIds[0];
        string[] probeIds = [anchorKeepId, .. candidateIds];
        if (probeIds.Any(id => !filesById.ContainsKey(id)))
            throw new InvalidOperationException("Una decisión explícita no pertenece a los miembros del grupo adaptado.");

        var group = new DuplicateGroup
        {
            GroupNumber = Review.Group.GroupNumber,
            FileSize = Review.Group.FileSize,
            Md5Checksum = Review.Group.Md5Checksum,
            Files = Array.AsReadOnly(probeIds.Select(id => filesById[id]).ToArray())
        };
        var probe = new DuplicateGroupReview
        {
            StableId = Review.StableId,
            Group = group,
            Status = DuplicateGroupReviewStatus.ReadyForCleanup,
            ReviewedWithoutCleanup = false,
            Notes = Review.Notes,
            LastModifiedUtc = ConfirmedAtUtc
        };
        probe.DecisionsByFileId.Add(anchorKeepId, DuplicateFileDecision.Keep);
        foreach (string candidateId in candidateIds)
            probe.DecisionsByFileId.Add(candidateId, DuplicateFileDecision.CandidateForTrash);
        return probe;
    }

    public bool HasSameDecisionSnapshot(PersistedCleanupGroupReview other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Identity != other.Identity || ConfirmedAtUtc != other.ConfirmedAtUtc ||
            MemberCount != other.MemberCount || KeepCount != other.KeepCount ||
            CandidateCount != other.CandidateCount || UndecidedCount != other.UndecidedCount ||
            Review.DecisionsByFileId.Count != other.Review.DecisionsByFileId.Count)
            return false;

        return Review.DecisionsByFileId.All(pair =>
            other.Review.DecisionsByFileId.TryGetValue(pair.Key, out DuplicateFileDecision decision) && decision == pair.Value);
    }
}

public sealed class CleanupGroupMemberLimitExceededException(long memberCount, int maximumMembers)
    : InvalidOperationException($"El grupo contiene {memberCount:N0} miembros; el máximo local para adaptar una revisión completa es {maximumMembers:N0}. No se cargó ningún miembro.")
{
    public long MemberCount { get; } = memberCount;
    public int MaximumMembers { get; } = maximumMembers;
}
