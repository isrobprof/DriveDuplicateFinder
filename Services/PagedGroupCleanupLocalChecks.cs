using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DriveDuplicateFinder.Data.Sqlite;
using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Services;

public sealed record PagedGroupCleanupLocalCheckResult(
    bool AccountChangeRejectedBeforePatch,
    bool ScopeChangeRejectedBeforePatch,
    bool ScanChangeRejectedBeforePatch,
    bool InvalidSessionRejectedBeforePatch,
    bool KeepToCandidateRejectedBeforePatch,
    bool CandidateToKeepRejectedBeforePatch,
    bool CandidateToUndecidedRejectedBeforePatch,
    bool ConfirmationInvalidatedAfterDecisionChange,
    bool StalePreflightRejectedBeforePatch,
    bool ApprovedPreflightStaleBeforeExecutionRejectedBeforePatch,
    bool SingleCandidatePatchRecordedConfirmed,
    bool SuccessfulPatchRecordedConfirmed,
    bool PartialFailureRecordedDistinctly,
    bool LostPatchResponseRecordedUncertain,
    bool CancellationAfterConfirmedWriteStopsNextPatch,
    bool KeepsAndUndecidedNeverPatched,
    bool OldConfirmationRejectedAfterPartial,
    bool NoRetriesAfterDefinitiveFailure,
    bool NoRetriesAfterUncertainResponse,
    bool NoRetriesAfterCancellation,
    bool NoAutomaticWriteRetries,
    bool NoWritesFromPagedUi);

/// <summary>Deterministic coordinator and write-outcome checks using temporary SQLite and a local HTTP handler.</summary>
public static class PagedGroupCleanupLocalChecks
{
    private const string Email = "paged-cleanup@example.invalid";
    private const string Hash = "abcdefabcdefabcdefabcdefabcdefab";
    private const long Size = 512;

    public static async Task<PagedGroupCleanupLocalCheckResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        bool accountChange = await RejectedAfterMutationAsync(FixtureKind.Standard,
            fixture => fixture.ChangeSessionFieldAsync("AccountKey", "another-account-key", cancellationToken), cancellationToken);
        bool scopeChange = await RejectedAfterMutationAsync(FixtureKind.Standard,
            fixture => fixture.ChangeSessionFieldAsync("ScopeKey", "another-scope-key", cancellationToken), cancellationToken);
        bool scanChange = await RejectedAfterMutationAsync(FixtureKind.Standard,
            fixture => fixture.AddNewerCompletedSessionAsync(cancellationToken), cancellationToken);
        bool invalidSession = await RejectedAfterMutationAsync(FixtureKind.Standard,
            fixture => fixture.Sessions.UpdateStatusAsync(fixture.Inventory.ScanId, ScanStatus.Paused,
                DateTimeOffset.UtcNow, "offline invalidation fixture", cancellationToken), cancellationToken);
        bool keepToCandidate = await RejectedAfterMutationAsync(FixtureKind.TwoKeeps,
            fixture => fixture.Reviews.SetExplicitDecisionAsync(fixture.Identity, "keep-file", DuplicateFileDecision.CandidateForTrash, cancellationToken), cancellationToken);
        bool candidateToKeep = await RejectedAfterMutationAsync(FixtureKind.Standard,
            fixture => fixture.Reviews.SetExplicitDecisionAsync(fixture.Identity, "candidate-1", DuplicateFileDecision.Keep, cancellationToken), cancellationToken);
        bool candidateToUndecided = await RejectedAfterMutationAsync(FixtureKind.Standard,
            fixture => fixture.Reviews.SetExplicitDecisionAsync(fixture.Identity, "candidate-1", DuplicateFileDecision.Undecided, cancellationToken), cancellationToken);

        bool stalePreflightRejected = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.Standard, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.None);
            try
            {
                _ = await fixture.Coordinator.PreparePlanAsync(cleanup, fixture.Approval with { IsStale = true },
                    cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException) { stalePreflightRejected = true; }
            stalePreflightRejected &= fixture.TransportFor(cleanup).Requests.All(request => request.Method != HttpMethod.Patch);
        }

        bool approvedPreflightStaleBeforeExecution = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.Standard, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.Success);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken);
            PagedGroupCleanupConfirmation confirmation = fixture.Confirm(plan);
            fixture.TransportFor(cleanup).ChangeRemoteMetadata();
            bool rejected = false;
            try
            {
                _ = await fixture.Coordinator.ExecuteConfirmedAsync(cleanup, confirmation, cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException) { rejected = true; }
            approvedPreflightStaleBeforeExecution = rejected &&
                fixture.TransportFor(cleanup).Requests.All(request => request.Method != HttpMethod.Patch);
        }

        bool singleCandidateSuccess = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.Standard, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.Success);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken);
            CleanupOperationResult result = await fixture.Coordinator.ExecuteConfirmedAsync(cleanup, fixture.Confirm(plan),
                cancellationToken: cancellationToken);
            string[] patchIds = fixture.TransportFor(cleanup).Requests.Where(request => request.Method == HttpMethod.Patch)
                .Select(request => request.FileId!).ToArray();
            singleCandidateSuccess = patchIds.SequenceEqual(new[] { "candidate-1" }, StringComparer.Ordinal) &&
                result.Record.Status == CleanupOperationStatus.Completed &&
                result.Record.FileResults.Count == 1 && result.Record.FileResults[0].Status == CleanupFileStatus.Trashed &&
                result.Record.FileResults[0].VerificationSucceeded;
        }

        bool successRecorded = false;
        bool keepsAndUndecidedProtected = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.MultipleCandidates, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.Success);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken);
            PagedGroupCleanupConfirmation confirmation = fixture.Confirm(plan);
            CleanupOperationResult result = await fixture.Coordinator.ExecuteConfirmedAsync(cleanup, confirmation,
                cancellationToken: cancellationToken);
            string[] patchIds = fixture.TransportFor(cleanup).Requests.Where(request => request.Method == HttpMethod.Patch)
                .Select(request => request.FileId!).ToArray();
            successRecorded = result.Record.Status == CleanupOperationStatus.Completed &&
                result.Record.FileResults.Count(file => file.Status == CleanupFileStatus.Trashed && file.VerificationSucceeded) == 2 &&
                result.Record.FileResults.All(file => file.Status == CleanupFileStatus.Trashed);
            keepsAndUndecidedProtected = patchIds.OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(new[] { "candidate-1", "candidate-2" }, StringComparer.Ordinal) &&
                !patchIds.Any(id => id.StartsWith("keep-", StringComparison.Ordinal) || id == "pending-file");
        }

        bool partialFailure = false;
        bool noRetriesForFailure = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.MultipleCandidates, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.FailSecondBeforeApply);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken);
            CleanupOperationResult result = await fixture.Coordinator.ExecuteConfirmedAsync(cleanup, fixture.Confirm(plan),
                cancellationToken: cancellationToken);
            int patchCount = fixture.TransportFor(cleanup).Requests.Count(request => request.Method == HttpMethod.Patch);
            partialFailure = result.Record.Status == CleanupOperationStatus.Partial &&
                result.Record.FileResults.Count(file => file.Status == CleanupFileStatus.Trashed) == 1 &&
                result.Record.FileResults.Count(file => file.Status == CleanupFileStatus.Failed) == 1 &&
                result.Record.FileResults.Count(file => file.Status == CleanupFileStatus.NotProcessed) == 0;
            noRetriesForFailure = patchCount == 2 && fixture.TransportFor(cleanup).WriteAttempts == 2 &&
                cleanup.DriveService.HttpClient.MessageHandler.NumTries == 1;
        }

        bool uncertainRecorded = false;
        bool oldConfirmationRejected = false;
        bool noRetryAfterUncertain = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.MultipleCandidates, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.LoseSecondResponseAfterApply);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken);
            PagedGroupCleanupConfirmation confirmation = fixture.Confirm(plan);
            CleanupOperationResult result = await fixture.Coordinator.ExecuteConfirmedAsync(cleanup, confirmation,
                cancellationToken: cancellationToken);
            int patchesAfterResult = fixture.TransportFor(cleanup).Requests.Count(request => request.Method == HttpMethod.Patch);
            uncertainRecorded = result.Record.Status == CleanupOperationStatus.Partial &&
                result.Record.FileResults.Count(file => file.Status == CleanupFileStatus.Trashed && file.VerificationSucceeded) == 1 &&
                result.Record.FileResults.Count(file => file.Status == CleanupFileStatus.Failed &&
                    file.FailureStage == "Files.UpdateOutcomeUncertain" && file.FailureReason == "OutcomeUncertain" &&
                    file.Message?.Contains("No se pudo verificar", StringComparison.Ordinal) == true) == 1;
            try
            {
                _ = await fixture.Coordinator.ExecuteConfirmedAsync(cleanup, confirmation, cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException) { oldConfirmationRejected = true; }
            int patchesAfterReuse = fixture.TransportFor(cleanup).Requests.Count(request => request.Method == HttpMethod.Patch);
            noRetryAfterUncertain = patchesAfterResult == 2 && patchesAfterReuse == patchesAfterResult &&
                fixture.TransportFor(cleanup).WriteAttempts == 2 && cleanup.DriveService.HttpClient.MessageHandler.NumTries == 1;
        }

        bool cancelledSafely = false;
        bool noRetriesForCancellation = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.MultipleCandidates, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.Success);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var progress = new InlineProgress<(int Current, int Total, string Name)>(item =>
            {
                if (item.Current == 2) cancel.Cancel();
            });
            CleanupOperationResult result = await fixture.Coordinator.ExecuteConfirmedAsync(cleanup, fixture.Confirm(plan),
                progress, cancel.Token);
            int patches = fixture.TransportFor(cleanup).Requests.Count(request => request.Method == HttpMethod.Patch);
            cancelledSafely = result.Record.Status == CleanupOperationStatus.Partial &&
                result.Record.FileResults.Count(file => file.Status == CleanupFileStatus.Trashed) == 1 &&
                result.Record.FileResults.Count(file => file.Status == CleanupFileStatus.Cancelled) == 1 && patches == 1;
            noRetriesForCancellation = fixture.TransportFor(cleanup).WriteAttempts == 1 &&
                cleanup.DriveService.HttpClient.MessageHandler.NumTries == 1;
        }

        // Account/scope/session mutations and decision edits happen after a confirmed plan;
        // each invalidation assertion requires rejection before any PATCH is recorded.
        var checks = new PagedGroupCleanupLocalCheckResult(
            accountChange, scopeChange, scanChange, invalidSession, keepToCandidate,
            candidateToKeep, candidateToUndecided, keepToCandidate || candidateToKeep || candidateToUndecided,
            stalePreflightRejected, approvedPreflightStaleBeforeExecution, singleCandidateSuccess,
            successRecorded, partialFailure, uncertainRecorded,
            cancelledSafely, keepsAndUndecidedProtected, oldConfirmationRejected,
            noRetriesForFailure, noRetryAfterUncertain, noRetriesForCancellation,
            noRetriesForFailure && noRetryAfterUncertain && noRetriesForCancellation,
            NoPagedCleanupUiReferences());

        Assert(accountChange, "Cambiar AccountKey no rechazó la ejecución antes de PATCH.");
        Assert(scopeChange, "Cambiar ScopeKey no rechazó la ejecución antes de PATCH.");
        Assert(scanChange, "Un ScanId sustituido por un Full Completed más reciente no rechazó la ejecución antes de PATCH.");
        Assert(invalidSession, "Una sesión que dejó de estar Completed no rechazó la ejecución antes de PATCH.");
        Assert(keepToCandidate, "Cambiar Keep a Candidate no rechazó la ejecución antes de PATCH.");
        Assert(candidateToKeep, "Cambiar Candidate a Keep no rechazó la ejecución antes de PATCH.");
        Assert(candidateToUndecided, "Cambiar Candidate a Undecided no rechazó la ejecución antes de PATCH.");
        Assert(checks.ConfirmationInvalidatedAfterDecisionChange, "Una decisión editada conservó una confirmación operativa reutilizable.");
        Assert(checks.StalePreflightRejectedBeforePatch, "Se aceptó como vigente un preflight previamente obsoleto.");
        Assert(checks.ApprovedPreflightStaleBeforeExecutionRejectedBeforePatch, "Un cambio remoto posterior al preflight aprobado no fue bloqueado antes de PATCH.");
        Assert(checks.SingleCandidatePatchRecordedConfirmed, "El PATCH simulado de un único candidato no quedó confirmado y verificado.");
        Assert(checks.SuccessfulPatchRecordedConfirmed, "El PATCH simulado exitoso no quedó registrado como confirmado.");
        Assert(checks.PartialFailureRecordedDistinctly, "El éxito parcial y el fallo siguiente no quedaron diferenciados en el historial.");
        Assert(checks.LostPatchResponseRecordedUncertain, "La respuesta perdida tras posible aplicación no quedó identificada como incierta.");
        Assert(checks.CancellationAfterConfirmedWriteStopsNextPatch, "La cancelación posterior a un archivo confirmado no detuvo el siguiente PATCH.");
        Assert(checks.KeepsAndUndecidedNeverPatched, "La escritura simulada incluyó Keep o Undecided, o excluyó candidatos explícitos.");
        Assert(checks.OldConfirmationRejectedAfterPartial, "Se pudo reutilizar una confirmación después de una operación parcial.");
        Assert(checks.NoRetriesAfterDefinitiveFailure, "Se reintentó la solicitud PATCH fallida antes de aplicarse.");
        Assert(checks.NoRetriesAfterUncertainResponse, "Se reintentó automáticamente una solicitud PATCH cuyo resultado era incierto.");
        Assert(checks.NoRetriesAfterCancellation, "Se emitieron escrituras adicionales después de cancelar entre archivos.");
        Assert(checks.NoAutomaticWriteRetries, "Se reintentó automáticamente una escritura fallida, incierta o cancelada.");
        Assert(checks.NoWritesFromPagedUi, "Form1 contiene una referencia a la ruta paginada de ejecución de limpieza.");
        return checks;
    }

    private static async Task<bool> RejectedAfterMutationAsync(
        FixtureKind kind,
        Func<TestFixture, Task> mutate,
        CancellationToken cancellationToken)
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(kind, cancellationToken);
        using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.None);
        PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken);
        PagedGroupCleanupConfirmation confirmation = fixture.Confirm(plan);
        await mutate(fixture);
        bool rejected = false;
        try { _ = await fixture.Coordinator.ExecuteConfirmedAsync(cleanup, confirmation, cancellationToken: cancellationToken); }
        catch (InvalidOperationException) { rejected = true; }
        return rejected && fixture.TransportFor(cleanup).Requests.All(request => request.Method != HttpMethod.Patch);
    }

    private static bool NoPagedCleanupUiReferences()
    {
        // PagedGroupCleanupService has no call site in Form1; paged actions are guarded separately.
        string sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Form1.cs"));
        if (!File.Exists(sourcePath) || PagedReadOnlyViewSafety.LegacyActionsEnabled(pagedReadOnlyMode: true)) return false;
        string source = File.ReadAllText(sourcePath);
        return !source.Contains("PagedGroupCleanupService", StringComparison.Ordinal) &&
            HandlerHasPagedGuardBeforeExecution(source, "btnEnviarGruposPapelera_Click", "_cleanupBatchService.ExecuteAsync") &&
            HandlerHasPagedGuardBeforeExecution(source, "btnEnviarGrupoPapelera_Click", "_trashService.ExecuteAsync");
    }

    private static bool HandlerHasPagedGuardBeforeExecution(string source, string handlerName, string executionCall)
    {
        int start = source.IndexOf("private async void " + handlerName, StringComparison.Ordinal);
        if (start < 0) return false;
        int end = source.IndexOf("\n    private ", start + handlerName.Length, StringComparison.Ordinal);
        if (end < 0) end = source.Length;
        string handler = source[start..end];
        int guard = handler.IndexOf("if (!IsLegacyActionsAllowed) return;", StringComparison.Ordinal);
        int execute = handler.IndexOf(executionCall, StringComparison.Ordinal);
        return guard >= 0 && execute > guard;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private enum FixtureKind { Standard, TwoKeeps, MultipleCandidates }
    private enum WriteBehavior { None, Success, FailSecondBeforeApply, LoseSecondResponseAfterApply }
    private sealed record CapturedRequest(HttpMethod Method, string? FileId);

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly Dictionary<CleanupDriveSession, LocalTransport> _transports = new();
        private TestFixture(string root, SqliteConnectionFactory factory, ScanSessionRepository sessions,
            ReviewStateRepository reviews, ScanInventoryIdentity inventory, DuplicateGroupIdentity identity,
            GoogleDriveFileService mapper)
        {
            _root = root; Factory = factory; Sessions = sessions; Reviews = reviews; Inventory = inventory;
            Identity = identity; Mapper = mapper;
            Scan = new RecoverableFullScanService(mapper, root);
            Adapter = new PersistedCleanupGroupReviewAdapter(factory, mapper);
            Trash = new GoogleDriveTrashService();
            Coordinator = new PagedGroupCleanupService(Scan, Adapter, reviews, Trash,
                new CleanupHistoryService(Path.Combine(root, "history")));
        }

        public SqliteConnectionFactory Factory { get; }
        public ScanSessionRepository Sessions { get; }
        public ReviewStateRepository Reviews { get; }
        public ScanInventoryIdentity Inventory { get; }
        public DuplicateGroupIdentity Identity { get; }
        public GoogleDriveFileService Mapper { get; }
        public RecoverableFullScanService Scan { get; }
        public PersistedCleanupGroupReviewAdapter Adapter { get; }
        public GoogleDriveTrashService Trash { get; }
        public PagedGroupCleanupService Coordinator { get; }
        public PagedGroupPreflightResult Approval { get; private set; } = null!;

        public static async Task<TestFixture> CreateAsync(FixtureKind kind, CancellationToken token)
        {
            string root = Path.Combine(Path.GetTempPath(), "DriveDuplicateFinder", $"paged-cleanup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var factory = new SqliteConnectionFactory(new LocalDataPathService(root));
            try
            {
                await new SqliteDatabaseInitializer(factory).InitializeAsync(token);
                var sessions = new ScanSessionRepository(factory);
                var reviews = new ReviewStateRepository(factory);
                string scanId = "offline-" + Guid.NewGuid().ToString("N");
                string accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Email)));
                var inventory = new ScanInventoryIdentity(accountKey, RecoverableFullScanService.ScopeKeyValue, scanId);
                var identity = new DuplicateGroupIdentity(inventory, Size, Hash);
                DriveFileInfo[] files = FilesFor(kind);
                await CreateInventoryAsync(factory, sessions, reviews, inventory, identity, files, token);
                var fixture = new TestFixture(root, factory, sessions, reviews, inventory, identity, new GoogleDriveFileService());
                using DriveService readDrive = new LocalTransport(Email, WriteBehavior.None).CreateDrive();
                fixture.Approval = await new PagedGroupPreflightService(fixture.Scan, fixture.Adapter, fixture.Trash)
                    .CheckOneGroupAsync(readDrive, identity, cancellationToken: token);
                if (!fixture.Approval.Preflight.IsSuccessful || fixture.Approval.IsStale)
                    throw new InvalidOperationException("La aprobación local de preflight no se pudo construir en el fixture.");
                return fixture;
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public CleanupDriveSession CreateCleanupSession(string email, WriteBehavior behavior)
        {
            var transport = new LocalTransport(email, behavior);
            var session = new CleanupDriveSession(transport.CreateDrive());
            _transports.Add(session, transport);
            return session;
        }

        public LocalTransport TransportFor(CleanupDriveSession session) => _transports[session];

        public async Task<PagedGroupCleanupPlan> PreparePlanAsync(CleanupDriveSession session, CancellationToken token)
        {
            return await Coordinator.PreparePlanAsync(session, Approval, cancellationToken: token);
        }

        public PagedGroupCleanupConfirmation Confirm(PagedGroupCleanupPlan plan) => Coordinator.ConfirmPlan(
            plan, plan.KeepFiles.Select(file => file.Id), plan.CandidateFiles.Select(file => file.Id));

        public async Task ChangeSessionFieldAsync(string field, string value, CancellationToken token)
        {
            if (field is not "AccountKey" and not "ScopeKey") throw new ArgumentOutOfRangeException(nameof(field));
            await using SqliteConnection connection = await Factory.OpenAsync(token);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE ScanSessions SET {field}=$value WHERE ScanId=$scanId;";
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$scanId", Inventory.ScanId);
            await command.ExecuteNonQueryAsync(token);
        }

        public async Task AddNewerCompletedSessionAsync(CancellationToken token)
        {
            await Task.Delay(20, token);
            string scanId = "newer-" + Guid.NewGuid().ToString("N");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await Sessions.CreateAsync(new ScanSessionRecord
            {
                ScanId = scanId, AccountKey = Inventory.AccountKey, ScopeKey = Inventory.ScopeKey,
                RootFolderId = "root", ScanType = ScanType.Full, Status = ScanStatus.Running,
                StartedAtUtc = now, UpdatedAtUtc = now
            }, token);
            await Sessions.UpdateStatusAsync(scanId, ScanStatus.Completed, DateTimeOffset.UtcNow, cancellationToken: token);
        }

        public async ValueTask DisposeAsync()
        {
            _transports.Clear();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            await ValueTask.CompletedTask;
        }

        private static DriveFileInfo[] FilesFor(FixtureKind kind)
        {
            string[] ids = kind switch
            {
                FixtureKind.TwoKeeps => ["keep-file", "keep-file-2", "candidate-1"],
                FixtureKind.MultipleCandidates => ["keep-file", "keep-file-2", "candidate-1", "candidate-2", "pending-file"],
                _ => ["keep-file", "candidate-1"]
            };
            return ids.Select(id => new DriveFileInfo
            {
                Id = id, Name = id + ".pdf", MimeType = "application/pdf", Size = Size, Md5Checksum = Hash,
                ParentIds = ["root"], Path = "Mi unidad/Test/" + id + ".pdf", OwnedByMe = true,
                IsShared = false, IsStarred = false, CanTrash = true
            }).ToArray();
        }

        private static async Task CreateInventoryAsync(SqliteConnectionFactory factory, ScanSessionRepository sessions,
            ReviewStateRepository reviews, ScanInventoryIdentity inventory, DuplicateGroupIdentity identity,
            IReadOnlyList<DriveFileInfo> files, CancellationToken token)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await sessions.CreateAsync(new ScanSessionRecord
            {
                ScanId = inventory.ScanId, AccountKey = inventory.AccountKey, ScopeKey = inventory.ScopeKey,
                RootFolderId = "root", ScanType = ScanType.Full, Status = ScanStatus.Running,
                StartedAtUtc = now, UpdatedAtUtc = now
            }, token);
            await using (SqliteConnection connection = await factory.OpenAsync(token))
            await using (SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token))
            {
                foreach (DriveFileInfo file in files)
                    await DriveFileCacheRepository.UpsertAsync(connection, transaction, new DriveFileCacheRecord
                    {
                        FileId = file.Id, Name = file.Name, AccountKey = inventory.AccountKey,
                        ScopeKey = inventory.ScopeKey, MimeType = file.MimeType, SizeBytes = Size,
                        Md5Checksum = Hash, ParentIds = ["root"], OwnedByMe = true, CanTrash = true,
                        IsShared = false, IsStarred = false, LastSeenScanId = inventory.ScanId, CachedAtUtc = now
                    }, token);
                await transaction.CommitAsync(token);
            }
            await sessions.UpdateStatusAsync(inventory.ScanId, ScanStatus.Completed, DateTimeOffset.UtcNow, cancellationToken: token);
            await reviews.RegisterCompletedInventoryAsync(inventory, token);
            await reviews.RegisterGroupAsync(identity, token);
            await reviews.SetExplicitDecisionAsync(identity, "keep-file", DuplicateFileDecision.Keep, token);
            if (files.Any(file => file.Id == "keep-file-2"))
                await reviews.SetExplicitDecisionAsync(identity, "keep-file-2", DuplicateFileDecision.Keep, token);
            foreach (DriveFileInfo file in files.Where(file => file.Id.StartsWith("candidate-", StringComparison.Ordinal)))
                await reviews.SetExplicitDecisionAsync(identity, file.Id, DuplicateFileDecision.CandidateForTrash, token);
            await reviews.ConfirmGroupReviewAsync(identity, token);
        }
    }

    private sealed class LocalTransport
    {
        private readonly string _email;
        private readonly WriteBehavior _behavior;
        private readonly HashSet<string> _trashed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _uncertain = new(StringComparer.Ordinal);
        private bool _remoteMetadataChanged;
        public List<CapturedRequest> Requests { get; } = [];
        public int WriteAttempts { get; private set; }
        public LocalTransport(string email, WriteBehavior behavior) { _email = email; _behavior = behavior; }
        public void ChangeRemoteMetadata() => _remoteMetadataChanged = true;
        public DriveService CreateDrive() => new(new BaseClientService.Initializer
        {
            ApplicationName = "DriveDuplicateFinder offline paged cleanup check",
            HttpClientFactory = new LocalHttpClientFactory(() => new LocalHttpMessageHandler(RespondAsync))
        });

        private Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string? id = path.Contains("/files/", StringComparison.Ordinal)
                ? Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]) : null;
            Requests.Add(new CapturedRequest(request.Method, id));
            if (request.Method == HttpMethod.Patch)
            {
                WriteAttempts++;
                if (_behavior == WriteBehavior.None)
                    return Task.FromResult(Json(HttpStatusCode.MethodNotAllowed, Error("Unexpected write.")));
                if (_behavior == WriteBehavior.FailSecondBeforeApply && id == "candidate-2")
                    return Task.FromResult(Json(HttpStatusCode.InternalServerError, Error("Synthetic failure before apply.")));
                _trashed.Add(id!);
                if (_behavior == WriteBehavior.LoseSecondResponseAfterApply && id == "candidate-2")
                {
                    _uncertain.Add(id!);
                    throw new HttpRequestException("Synthetic response lost after the remote write may have applied.");
                }
                return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id, name = id + ".pdf", trashed = true, modifiedTime = "2026-10-10T00:00:00Z" })));
            }
            if (request.Method != HttpMethod.Get)
                return Task.FromResult(Json(HttpStatusCode.MethodNotAllowed, Error("Unexpected method.")));
            if (path.EndsWith("/about", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { user = new { emailAddress = _email } })));
            if (id == "root") return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id = "root" })));
            if (id is null || !id.StartsWith("keep-", StringComparison.Ordinal) &&
                !id.StartsWith("candidate-", StringComparison.Ordinal) && id != "pending-file")
                return Task.FromResult(Json(HttpStatusCode.NotFound, Error("Unknown fixture member.")));
            if (_uncertain.Contains(id)) throw new HttpRequestException("Synthetic verification unavailable after lost PATCH response.");
            bool trashed = _trashed.Contains(id);
            return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                id, name = id + (_remoteMetadataChanged && id.StartsWith("candidate-", StringComparison.Ordinal) ? "-renamed.pdf" : ".pdf"),
                mimeType = "application/pdf", size = Size.ToString(), md5Checksum = Hash,
                parents = new[] { "root" }, trashed, explicitlyTrashed = trashed, ownedByMe = true,
                shared = false, starred = false, version = "1", capabilities = new { canTrash = true }
            })));
        }

        private static string Error(string message) => JsonSerializer.Serialize(new { error = new { code = 500, message, errors = new[] { new { domain = "global", reason = "synthetic", message } } } });
        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class LocalHttpClientFactory(Func<HttpMessageHandler> handlerFactory) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handlerFactory();
    }
    private sealed class LocalHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
