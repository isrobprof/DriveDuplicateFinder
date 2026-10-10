using System.Net;
using System.Net.Http;
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

public sealed record GoogleDriveTrashPreflightCheckResult(
    bool ValidGroupApprovedWithGetOnly,
    bool MissingKeepOrCandidateRejectedLocally,
    bool UndecidedRejectedBeforeMemberGets,
    bool MissingFileBlocked,
    bool TrashedFileBlocked,
    bool ChangedSizeAndChecksumBlocked,
    bool PermissionAndNetworkErrorsClassified,
    bool CancellationHandledWithoutWrites,
    bool AccountMismatchRejectedBeforeGroupReads,
    bool DecisionChangeDuringPreflightDiscarded,
    bool NoWriteRequestsSent,
    bool TemporaryDirectoryDeleted);

/// <summary>Exercises the existing preflight with synthetic HTTP responses and temporary SQLite only.</summary>
public static class GoogleDriveTrashPreflightLocalChecks
{
    private const string Email = "offline-preflight@example.invalid";
    private const string Hash = "abcdefabcdefabcdefabcdefabcdefab";
    private const long Size = 512;

    public static async Task<GoogleDriveTrashPreflightCheckResult> VerifyAsync(
        CancellationToken cancellationToken = default)
    {
        bool noWriteRequestsSent = true;
        DuplicateGroupReview validReview = CreateReview(includeUndecided: false);

        var validTransport = new LocalDriveTransport(Email, ResponseScenario.Valid);
        CleanupPreflightResult valid = await RunPreflightAsync(validReview, validTransport, cancellationToken);
        bool validGroupApprovedWithGetOnly = valid.IsSuccessful && valid.KeepFile?.Id == "keep-file" &&
            valid.CandidateFiles.Select(file => file.Id).SequenceEqual(["candidate-file"], StringComparer.Ordinal) &&
            validTransport.Requests.Count == 2 && validTransport.Requests.All(request => request.Method == HttpMethod.Get);

        DuplicateGroupReview withoutKeep = CreateReview(includeUndecided: false);
        withoutKeep.DecisionsByFileId["keep-file"] = DuplicateFileDecision.CandidateForTrash;
        var withoutKeepTransport = new LocalDriveTransport(Email, ResponseScenario.Valid);
        CleanupPreflightResult noKeep = await RunPreflightAsync(withoutKeep, withoutKeepTransport, cancellationToken);
        DuplicateGroupReview withoutCandidate = CreateReview(includeUndecided: false);
        withoutCandidate.DecisionsByFileId["candidate-file"] = DuplicateFileDecision.Keep;
        var withoutCandidateTransport = new LocalDriveTransport(Email, ResponseScenario.Valid);
        CleanupPreflightResult noCandidate = await RunPreflightAsync(withoutCandidate, withoutCandidateTransport, cancellationToken);
        bool missingKeepOrCandidateRejectedLocally = !noKeep.IsSuccessful && !noCandidate.IsSuccessful &&
            withoutKeepTransport.Requests.Count == 0 && withoutCandidateTransport.Requests.Count == 0;

        DuplicateGroupReview withUndecided = CreateReview(includeUndecided: true);
        var undecidedTransport = new LocalDriveTransport(Email, ResponseScenario.Valid);
        CleanupPreflightResult undecided = await RunPreflightAsync(withUndecided, undecidedTransport, cancellationToken);
        bool undecidedRejectedBeforeMemberGets = !undecided.IsSuccessful && undecidedTransport.Requests.Count == 0 &&
            undecided.ValidationMessages.Any(message => message.Contains("sin decidir", StringComparison.OrdinalIgnoreCase));

        var missingTransport = new LocalDriveTransport(Email, ResponseScenario.MissingCandidate);
        CleanupPreflightResult missing = await RunPreflightAsync(validReview, missingTransport, cancellationToken);
        bool missingFileBlocked = !missing.IsSuccessful && missing.FailureKind == CleanupPreflightFailureKind.FileUnavailable;

        var trashedTransport = new LocalDriveTransport(Email, ResponseScenario.TrashedCandidate);
        CleanupPreflightResult trashed = await RunPreflightAsync(validReview, trashedTransport, cancellationToken);
        bool trashedFileBlocked = !trashed.IsSuccessful && trashed.ValidationMessages.Any(message =>
            message.Contains("ya está en la papelera", StringComparison.OrdinalIgnoreCase));

        var changedSizeTransport = new LocalDriveTransport(Email, ResponseScenario.ChangedSize);
        CleanupPreflightResult changedSize = await RunPreflightAsync(validReview, changedSizeTransport, cancellationToken);
        var changedHashTransport = new LocalDriveTransport(Email, ResponseScenario.ChangedChecksum);
        CleanupPreflightResult changedHash = await RunPreflightAsync(validReview, changedHashTransport, cancellationToken);
        bool changedSizeAndChecksumBlocked = !changedSize.IsSuccessful && !changedHash.IsSuccessful &&
            changedSize.ValidationMessages.Any(message => message.Contains("tamaño", StringComparison.OrdinalIgnoreCase)) &&
            changedHash.ValidationMessages.Any(message => message.Contains("MD5", StringComparison.OrdinalIgnoreCase));

        var permissionTransport = new LocalDriveTransport(Email, ResponseScenario.ForbiddenCandidate);
        CleanupPreflightResult permission = await RunPreflightAsync(validReview, permissionTransport, cancellationToken);
        var networkTransport = new LocalDriveTransport(Email, ResponseScenario.NetworkFailureCandidate);
        CleanupPreflightResult network = await RunPreflightAsync(validReview, networkTransport, cancellationToken);
        bool permissionAndNetworkErrorsClassified = permission.FailureKind == CleanupPreflightFailureKind.PermissionDenied &&
            network.FailureKind == CleanupPreflightFailureKind.NetworkUnavailable;

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancellationTransport = new LocalDriveTransport(Email, ResponseScenario.CancelOnFirstFile, request =>
        {
            if (request.FileId == "keep-file") cancelled.Cancel();
            return Task.CompletedTask;
        });
        CleanupPreflightResult cancellation = await RunPreflightAsync(validReview, cancellationTransport, cancelled.Token);
        bool cancellationHandledWithoutWrites = cancellation.WasCancelled &&
            cancellation.FailureKind == CleanupPreflightFailureKind.Cancelled;

        string root = Path.Combine(Path.GetTempPath(), "DriveDuplicateFinder", $"preflight-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        bool accountMismatchRejectedBeforeGroupReads = false;
        bool decisionChangeDuringPreflightDiscarded = false;
        bool staleFlag = false;
        bool stalePreflightUnapproved = false;
        bool staleFailureClassified = false;
        bool staleRequestsWereGetOnly = false;
        string staleRequestSummary = string.Empty;
        bool temporaryDirectoryDeleted = false;
        try
        {
            var factory = new SqliteConnectionFactory(new LocalDataPathService(root));
            await new SqliteDatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var sessions = new ScanSessionRepository(factory);
            var reviewRepository = new ReviewStateRepository(factory);
            var inventory = new ScanInventoryIdentity(AccountKey(Email), RecoverableFullScanService.ScopeKeyValue, "preflight-current");
            await CreateCompletedInventoryAsync(factory, sessions, reviewRepository, inventory, cancellationToken);
            var identity = new DuplicateGroupIdentity(inventory, Size, Hash);
            await reviewRepository.RegisterGroupAsync(identity, cancellationToken);
            await reviewRepository.SetExplicitDecisionAsync(identity, "keep-file", DuplicateFileDecision.Keep, cancellationToken);
            await reviewRepository.SetExplicitDecisionAsync(identity, "candidate-file", DuplicateFileDecision.CandidateForTrash, cancellationToken);
            await reviewRepository.ConfirmGroupReviewAsync(identity, cancellationToken);

            var mapper = new GoogleDriveFileService();
            var scanService = new RecoverableFullScanService(mapper, root);
            var adapter = new PersistedCleanupGroupReviewAdapter(factory, mapper);
            var coordinator = new PagedGroupPreflightService(scanService, adapter, new GoogleDriveTrashService());

            var wrongAccountTransport = new LocalDriveTransport("different-account@example.invalid", ResponseScenario.Valid);
            using (DriveService wrongAccountDrive = wrongAccountTransport.CreateDriveService())
            {
                try { _ = await coordinator.CheckOneGroupAsync(wrongAccountDrive, identity, cancellationToken: cancellationToken); }
                catch (InvalidOperationException) { accountMismatchRejectedBeforeGroupReads = true; }
            }
            accountMismatchRejectedBeforeGroupReads &= wrongAccountTransport.Requests.Count == 2 &&
                wrongAccountTransport.Requests.All(request => request.Method == HttpMethod.Get) &&
                !wrongAccountTransport.Requests.Any(request => request.FileId is "keep-file" or "candidate-file");

            var staleTransport = new LocalDriveTransport(Email, ResponseScenario.Valid, async request =>
            {
                if (request.FileId == "candidate-file")
                    await reviewRepository.SetExplicitDecisionAsync(identity, "candidate-file", DuplicateFileDecision.Undecided, cancellationToken);
            });
            using (DriveService drive = staleTransport.CreateDriveService())
            {
                PagedGroupPreflightResult stale = await coordinator.CheckOneGroupAsync(drive, identity, cancellationToken: cancellationToken);
                staleFlag = stale.IsStale;
                stalePreflightUnapproved = !stale.Preflight.IsSuccessful;
                staleFailureClassified = stale.Preflight.FailureKind == CleanupPreflightFailureKind.RemoteFailure;
                staleRequestsWereGetOnly = staleTransport.Requests.All(request => request.Method == HttpMethod.Get);
                staleRequestSummary = string.Join(",", staleTransport.Requests.Select(request => request.Method.Method + ":" + request.FileId));
                decisionChangeDuringPreflightDiscarded = staleFlag && stalePreflightUnapproved &&
                    staleFailureClassified && staleRequestsWereGetOnly;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            temporaryDirectoryDeleted = !Directory.Exists(root);
        }

        var allTransports = new[]
        {
            validTransport, withoutKeepTransport, withoutCandidateTransport, undecidedTransport, missingTransport, trashedTransport, changedSizeTransport,
            changedHashTransport, permissionTransport, networkTransport, cancellationTransport
        };
        noWriteRequestsSent &= allTransports.SelectMany(transport => transport.Requests)
            .All(request => request.Method == HttpMethod.Get);

        Assert(validGroupApprovedWithGetOnly, "El grupo válido no pasó el preflight simulado con lecturas GET solamente.");
        Assert(missingKeepOrCandidateRejectedLocally, "El plan sin Keep o sin candidato no se bloqueó antes de la lectura remota.");
        Assert(undecidedRejectedBeforeMemberGets, "Un grupo con Undecided llegó a Files.Get o no se bloqueó localmente.");
        Assert(missingFileBlocked && trashedFileBlocked, "Un archivo ausente o enviado a papelera no bloqueó el preflight.");
        Assert(changedSizeAndChecksumBlocked, "No se detectó un cambio remoto de tamaño o MD5.");
        Assert(permissionAndNetworkErrorsClassified, "Los errores de permisos y red no se clasificaron como bloqueos.");
        Assert(cancellationHandledWithoutWrites, "La cancelación no terminó el preflight de solo lectura de forma segura.");
        Assert(accountMismatchRejectedBeforeGroupReads, "Se consultaron miembros usando una cuenta distinta del inventario.");
        Assert(decisionChangeDuringPreflightDiscarded,
            $"La comprobación de cambios concurrentes no pasó: IsStale={staleFlag}, " +
            $"preflightUnapproved={stalePreflightUnapproved}, failureClassified={staleFailureClassified}, " +
            $"getOnly={staleRequestsWereGetOnly}; " +
            $"requests={staleRequestSummary}. " +
            "El resultado, la confirmación o las decisiones pudieron cambiar durante el preflight.");
        Assert(noWriteRequestsSent, "Una prueba de preflight emitió una solicitud distinta de GET.");

        return new GoogleDriveTrashPreflightCheckResult(validGroupApprovedWithGetOnly,
            missingKeepOrCandidateRejectedLocally,
            undecidedRejectedBeforeMemberGets, missingFileBlocked, trashedFileBlocked,
            changedSizeAndChecksumBlocked, permissionAndNetworkErrorsClassified,
            cancellationHandledWithoutWrites, accountMismatchRejectedBeforeGroupReads,
            decisionChangeDuringPreflightDiscarded, noWriteRequestsSent, temporaryDirectoryDeleted);
    }

    private static async Task<CleanupPreflightResult> RunPreflightAsync(
        DuplicateGroupReview review,
        LocalDriveTransport transport,
        CancellationToken cancellationToken)
    {
        using DriveService drive = transport.CreateDriveService();
        return await new GoogleDriveTrashService().PreflightAsync(drive, review, false, cancellationToken: cancellationToken);
    }

    private static DuplicateGroupReview CreateReview(bool includeUndecided)
    {
        DriveFileInfo keep = CreateFile("keep-file", "keep.pdf");
        DriveFileInfo candidate = CreateFile("candidate-file", "candidate.pdf");
        var files = new List<DriveFileInfo> { keep, candidate };
        if (includeUndecided) files.Add(CreateFile("pending-file", "pending.pdf"));
        var group = new DuplicateGroup { GroupNumber = 1, FileSize = Size, Md5Checksum = Hash, Files = files.AsReadOnly() };
        var review = new DuplicateGroupReview
        {
            StableId = DuplicateReviewService.CreateStableGroupId(Size, Hash),
            Group = group,
            Status = includeUndecided ? DuplicateGroupReviewStatus.PartiallyReviewed : DuplicateGroupReviewStatus.ReadyForCleanup
        };
        review.DecisionsByFileId.Add(keep.Id, DuplicateFileDecision.Keep);
        review.DecisionsByFileId.Add(candidate.Id, DuplicateFileDecision.CandidateForTrash);
        if (includeUndecided) review.DecisionsByFileId.Add("pending-file", DuplicateFileDecision.Undecided);
        return review;
    }

    private static DriveFileInfo CreateFile(string id, string name) => new()
    {
        Id = id,
        Name = name,
        MimeType = "application/pdf",
        Size = Size,
        Md5Checksum = Hash,
        ParentIds = ["root"],
        Path = "Mi unidad/Prueba/" + name,
        OwnedByMe = true,
        IsShared = false,
        IsStarred = false,
        CanTrash = true
    };

    private static async Task CreateCompletedInventoryAsync(
        SqliteConnectionFactory factory,
        ScanSessionRepository sessions,
        ReviewStateRepository reviews,
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await sessions.CreateAsync(new ScanSessionRecord
        {
            ScanId = inventory.ScanId,
            AccountKey = inventory.AccountKey,
            ScopeKey = inventory.ScopeKey,
            RootFolderId = "root",
            ScanType = ScanType.Full,
            Status = ScanStatus.Running,
            StartedAtUtc = now,
            UpdatedAtUtc = now
        }, cancellationToken);

        await using (SqliteConnection connection = await factory.OpenAsync(cancellationToken))
        await using (SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach ((string id, string name) in new[] { ("keep-file", "keep.pdf"), ("candidate-file", "candidate.pdf") })
            {
                await DriveFileCacheRepository.UpsertAsync(connection, transaction, new DriveFileCacheRecord
                {
                    FileId = id,
                    Name = name,
                    AccountKey = inventory.AccountKey,
                    ScopeKey = inventory.ScopeKey,
                    MimeType = "application/pdf",
                    SizeBytes = Size,
                    Md5Checksum = Hash,
                    ParentIds = ["root"],
                    OwnedByMe = true,
                    CanTrash = true,
                    IsShared = false,
                    IsStarred = false,
                    LastSeenScanId = inventory.ScanId,
                    CachedAtUtc = now
                }, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }

        await sessions.UpdateStatusAsync(inventory.ScanId, ScanStatus.Completed, DateTimeOffset.UtcNow,
            cancellationToken: cancellationToken);
        await reviews.RegisterCompletedInventoryAsync(inventory, cancellationToken);
    }

    private static string AccountKey(string email) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private enum ResponseScenario
    {
        Valid,
        MissingCandidate,
        TrashedCandidate,
        ChangedSize,
        ChangedChecksum,
        ForbiddenCandidate,
        NetworkFailureCandidate,
        CancelOnFirstFile
    }

    private sealed record CapturedRequest(HttpMethod Method, string? FileId);

    private sealed class LocalDriveTransport
    {
        private readonly string _email;
        private readonly ResponseScenario _scenario;
        private readonly Func<CapturedRequest, Task>? _afterRequest;
        public List<CapturedRequest> Requests { get; } = [];

        public LocalDriveTransport(string email, ResponseScenario scenario, Func<CapturedRequest, Task>? afterRequest = null)
        {
            _email = email;
            _scenario = scenario;
            _afterRequest = afterRequest;
        }

        public DriveService CreateDriveService() => new(new BaseClientService.Initializer
        {
            ApplicationName = "DriveDuplicateFinder offline preflight check",
            HttpClientFactory = new LocalHttpClientFactory(() => new LocalHttpMessageHandler(RespondAsync))
        });

        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string? fileId = path.Contains("/files/", StringComparison.Ordinal)
                ? Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..])
                : null;
            var captured = new CapturedRequest(request.Method, fileId);
            Requests.Add(captured);
            if (_afterRequest is not null) await _afterRequest(captured);
            if (request.Method != HttpMethod.Get)
                return JsonResponse(HttpStatusCode.MethodNotAllowed, ErrorPayload(HttpStatusCode.MethodNotAllowed, "Read-only test transport."));

            if (path.EndsWith("/about", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { user = new { emailAddress = _email } }));
            if (string.Equals(fileId, "root", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { id = "root" }));

            if (fileId is not "keep-file" and not "candidate-file")
                return JsonResponse(HttpStatusCode.NotFound, ErrorPayload(HttpStatusCode.NotFound, "Not found."));
            if (_scenario == ResponseScenario.CancelOnFirstFile)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (_scenario == ResponseScenario.NetworkFailureCandidate && fileId == "candidate-file")
                throw new HttpRequestException("Synthetic offline network failure.");
            if (_scenario == ResponseScenario.ForbiddenCandidate && fileId == "candidate-file")
                return JsonResponse(HttpStatusCode.Forbidden, ErrorPayload(HttpStatusCode.Forbidden, "Synthetic permission failure."));
            if (_scenario == ResponseScenario.MissingCandidate && fileId == "candidate-file")
                return JsonResponse(HttpStatusCode.NotFound, ErrorPayload(HttpStatusCode.NotFound, "Synthetic missing file."));

            bool isCandidate = fileId == "candidate-file";
            long size = isCandidate && _scenario == ResponseScenario.ChangedSize ? Size + 1 : Size;
            string checksum = isCandidate && _scenario == ResponseScenario.ChangedChecksum ? "0123456789abcdef0123456789abcdef" : Hash;
            bool trashed = isCandidate && _scenario == ResponseScenario.TrashedCandidate;
            var payload = new
            {
                id = fileId,
                name = isCandidate ? "candidate.pdf" : "keep.pdf",
                mimeType = "application/pdf",
                size = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                md5Checksum = checksum,
                parents = new[] { "root" },
                trashed,
                explicitlyTrashed = trashed,
                ownedByMe = true,
                shared = false,
                starred = false,
                version = "1",
                capabilities = new { canTrash = true }
            };
            return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(payload));
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string payload) =>
            new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

        private static string ErrorPayload(HttpStatusCode status, string message) =>
            JsonSerializer.Serialize(new { error = new { code = (int)status, message, errors = new[] { new { domain = "global", reason = "synthetic", message } } } });
    }

    private sealed class LocalHttpClientFactory(Func<HttpMessageHandler> handlerFactory) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handlerFactory();
    }

    private sealed class LocalHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
