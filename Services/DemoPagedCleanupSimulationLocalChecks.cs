using DriveDuplicateFinder.Data.Sqlite;
using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using Google.Apis.Drive.v3;

namespace DriveDuplicateFinder.Services;

public sealed record DemoPagedCleanupSimulationCheckResult(
    bool SyntheticCompletedInventoryAccepted,
    bool ExactConfirmationRequired,
    bool DeclinedConfirmationSentNoPatch,
    bool StaleConfirmationSentNoPatch,
    bool ForeignGroupRejectedBeforeTransport,
    bool BlockedPreflightDidNotSimulatePatch,
    bool CrossPageSimulationProtectedKeepAndUndecided,
    bool SuccessfulSimulationPresentedWithoutClaimingRealWrite,
    bool OutcomeUncertainPresentedAsNotRetryable,
    bool SimulatorUsesLocalTransportWithoutOAuth,
    bool PagedUiSimulationGuardedAndDoubleClickBlocked,
    bool ConfirmationDialogRequiresExplicitReview,
    bool DemoHistoryContainedInTemporaryDirectory);

/// <summary>Offline end-to-end checks for the demo-only WinForms cleanup simulation.</summary>
public static class DemoPagedCleanupSimulationLocalChecks
{
    public static async Task<DemoPagedCleanupSimulationCheckResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        DemoReviewSeed seed = await DemoReviewData.CreateAsync(cancellationToken: cancellationToken);
        try
        {
            var mapper = new GoogleDriveFileService();
            var scan = new RecoverableFullScanService(mapper, seed.DataDirectory);
            RecoverableFullScanResult opened = await scan.OpenCompletedInventoryAsync(seed.Inventory, cancellationToken);
            var connectionFactory = new SqliteConnectionFactory(new LocalDataPathService(seed.DataDirectory));
            var fileRepository = new DriveFileCacheRepository(connectionFactory);
            var reviewRepository = new ReviewStateRepository(connectionFactory);
            DuplicateGroupSummaryPage groups = await scan.GetDuplicateGroupSummariesPageAsync(seed.Inventory, 0, 10, cancellationToken);
            DuplicateGroupIdentity identity = groups.Items.Single(group => group.MemberCount == 205).Identity;
            await reviewRepository.RegisterGroupAsync(identity, cancellationToken);

            DuplicateGroupMembersPage first = await fileRepository.GetDuplicateGroupMembersPageAsync(identity, 0, 100, cancellationToken);
            DuplicateGroupMembersPage middle = await fileRepository.GetDuplicateGroupMembersPageAsync(identity, 100, 100, cancellationToken);
            DuplicateGroupMembersPage last = await fileRepository.GetDuplicateGroupMembersPageAsync(identity, 200, 100, cancellationToken);
            string firstKeepId = first.Items[0].FileId;
            string candidateId = middle.Items[0].FileId;
            string secondKeepId = last.Items[0].FileId;
            await reviewRepository.SetExplicitDecisionAsync(identity, firstKeepId, DuplicateFileDecision.Keep, cancellationToken);
            await reviewRepository.SetExplicitDecisionAsync(identity, candidateId, DuplicateFileDecision.CandidateForTrash, cancellationToken);
            await reviewRepository.SetExplicitDecisionAsync(identity, secondKeepId, DuplicateFileDecision.Keep, cancellationToken);
            await reviewRepository.ConfirmGroupReviewAsync(identity, cancellationToken);

            var adapter = new PersistedCleanupGroupReviewAdapter(connectionFactory, mapper);
            var trash = new GoogleDriveTrashService();
            string historyDirectory = Path.Combine(seed.DataDirectory, "cleanup-history");
            var cleanup = new PagedGroupCleanupService(scan, adapter, reviewRepository, trash,
                new CleanupHistoryService(historyDirectory));
            var preflight = new PagedGroupPreflightService(scan, adapter, trash);
            var simulator = new DemoPagedCleanupSimulationService(seed, preflight, cleanup);

            PagedGroupCleanupLocalPreview transportPreview = await cleanup.PrepareLocalPreviewAsync(identity, cancellationToken);
            await VerifySyntheticDriveMetadataPayloadAsync(transportPreview, candidateId, cancellationToken);

            PagedGroupCleanupPlan? declinedPlan = null;
            DemoPagedCleanupSimulationResult declined = await simulator.RunAsync(identity, (plan, _) =>
            {
                declinedPlan = plan;
                return Task.FromResult(false);
            }, () => true, cancellationToken);
            string[] expectedKeepIds = [firstKeepId, secondKeepId];
            string[] expectedCandidateIds = [candidateId];
            string[] actualKeepIds = declinedPlan?.KeepFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray() ?? [];
            string[] actualCandidateIds = declinedPlan?.CandidateFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray() ?? [];
            bool exactConfirmationRequired = declinedPlan is not null && declinedPlan.LocalReview.MemberCount == 205 &&
                declinedPlan.LocalReview.UndecidedCount == 202 &&
                actualKeepIds.SequenceEqual(expectedKeepIds.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal) &&
                actualCandidateIds.SequenceEqual(expectedCandidateIds, StringComparer.Ordinal);
            bool declinedConfirmationSentNoPatch = declined.ConfirmationDeclined && declined.Operation is null &&
                declined.SimulatedPatchFileIds.Count == 0 && declined.LocalTransportRequestCount > 0;

            bool requestCurrent = true;
            DemoPagedCleanupSimulationResult staleConfirmation = await simulator.RunAsync(identity, (_, _) =>
            {
                requestCurrent = false;
                return Task.FromResult(true);
            }, () => requestCurrent, cancellationToken);
            bool staleConfirmationSentNoPatch = staleConfirmation.Operation is null &&
                staleConfirmation.SimulatedPatchFileIds.Count == 0 &&
                staleConfirmation.BlockingReason?.Contains("obsoleta", StringComparison.OrdinalIgnoreCase) == true;

            bool foreignGroupRejectedBeforeTransport = false;
            ScanInventoryIdentity[] foreignInventories =
            {
                new("another-account", identity.Inventory.ScopeKey, identity.Inventory.ScanId),
                new(identity.Inventory.AccountKey, "another-scope", identity.Inventory.ScanId),
                new(identity.Inventory.AccountKey, identity.Inventory.ScopeKey, "another-demo-scan")
            };
            foreignGroupRejectedBeforeTransport = true;
            foreach (ScanInventoryIdentity foreignInventory in foreignInventories)
            {
                try
                {
                    var foreignIdentity = new DuplicateGroupIdentity(foreignInventory,
                        identity.SizeBytes, identity.NormalizedChecksum);
                    _ = await simulator.RunAsync(foreignIdentity, (_, _) => Task.FromResult(true), () => true, cancellationToken);
                    foreignGroupRejectedBeforeTransport = false;
                    break;
                }
                catch (InvalidOperationException) { }
            }

            await SetCanTrashAsync(connectionFactory, candidateId, canTrash: false, cancellationToken);
            DemoPagedCleanupSimulationResult blockedPreflight = await simulator.RunAsync(identity,
                (_, _) => Task.FromResult(true), () => true, cancellationToken);
            bool blockedPreflightDidNotSimulatePatch = blockedPreflight.Preflight is { Preflight: { IsSuccessful: false }, RemoteFileReadsStarted: false } &&
                blockedPreflight.Plan is null && blockedPreflight.Operation is null &&
                blockedPreflight.SimulatedPatchFileIds.Count == 0 &&
                !string.IsNullOrWhiteSpace(blockedPreflight.BlockingReason) &&
                PagedGroupCleanupSimulationResultForm.BuildDetails(blockedPreflight).Contains("BLOQUEADO", StringComparison.Ordinal);
            await SetCanTrashAsync(connectionFactory, candidateId, canTrash: true, cancellationToken);

            PagedGroupCleanupPlan? confirmedPlan = null;
            DemoPagedCleanupSimulationResult simulated = await simulator.RunAsync(identity, (plan, _) =>
            {
                confirmedPlan = plan;
                bool exactIds = plan.KeepFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal)
                        .SequenceEqual(new[] { firstKeepId, secondKeepId }.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal) &&
                    plan.CandidateFiles.Select(file => file.Id).SequenceEqual(new[] { candidateId }, StringComparer.Ordinal);
                return Task.FromResult(exactIds);
            }, () => true, cancellationToken);

            string[] allKeepIds = confirmedPlan?.KeepFiles.Select(file => file.Id).ToArray() ?? [];
            string[] actualPatchIds = simulated.SimulatedPatchFileIds.ToArray();
            string[] actualFileOutcomes = simulated.Operation?.Record.FileResults.Select(result =>
                $"{result.File.Id}:{result.Status}:{result.FailureStage ?? "-"}:{result.FailureReason ?? "-"}").ToArray() ?? [];
            bool crossPageSimulationProtectedKeepAndUndecided = simulated.SimulatedPatchFileIds.SequenceEqual(new[] { candidateId }, StringComparer.Ordinal) &&
                !simulated.SimulatedPatchFileIds.Intersect(allKeepIds, StringComparer.Ordinal).Any() &&
                !simulated.SimulatedPatchFileIds.Contains(first.Items.Skip(1).First().FileId, StringComparer.Ordinal) &&
                simulated.Operation?.Record.FileResults.Count(result => result.Status == CleanupFileStatus.Trashed && result.VerificationSucceeded) == 1;
            string resultText = PagedGroupCleanupSimulationResultForm.BuildDetails(simulated);
            bool successfulSimulationPresentedWithoutClaimingRealWrite = simulated.IsOfflineSimulation && !simulated.RealExecutionAvailable &&
                simulated.Operation?.Record.Status == CleanupOperationStatus.Completed &&
                resultText.Contains("SIMULACIÓN OFFLINE", StringComparison.OrdinalIgnoreCase) &&
                resultText.Contains("no se modificó ningún archivo real", StringComparison.OrdinalIgnoreCase) &&
                resultText.Contains("PATCH simulados capturados: 1", StringComparison.Ordinal) &&
                resultText.Contains("Keep protegidos: 2", StringComparison.Ordinal) &&
                resultText.Contains("candidatos explícitos: 1", StringComparison.Ordinal) &&
                resultText.Contains("Undecided excluidos: 202", StringComparison.Ordinal) &&
                simulated.SimulatedPatchFileIds.SequenceEqual(new[] { candidateId }, StringComparer.Ordinal) &&
                simulated.Operation.Record.FileResults.All(file => file.Status == CleanupFileStatus.Trashed && file.VerificationSucceeded);

            var uncertainSnapshot = new CleanupFileSnapshot { Id = "synthetic-uncertain", Name = "fixture.pdf", MimeType = "application/pdf", Size = 1 };
            var uncertainRecord = new CleanupOperationRecord
            {
                OperationId = "offline-uncertain-check",
                StartedAtUtc = DateTimeOffset.UtcNow,
                StableGroupId = "offline-group",
                FileSize = 1,
                Md5Checksum = "00000000000000000000000000000000",
                Status = CleanupOperationStatus.Partial
            };
            uncertainRecord.FileResults.Add(new CleanupFileResult
            {
                File = uncertainSnapshot,
                Status = CleanupFileStatus.Failed,
                FailureStage = "Files.UpdateOutcomeUncertain",
                FailureReason = "OutcomeUncertain",
                Message = "Synthetic response lost after possible application."
            });
            var uncertainResult = new DemoPagedCleanupSimulationResult(null, null,
                new CleanupOperationResult { Record = uncertainRecord, WriteAttempted = true, ShouldInvalidateScan = true, NoFilesChanged = false },
                false, null, Array.Empty<string>(), 1);
            string uncertainText = PagedGroupCleanupSimulationResultForm.BuildDetails(uncertainResult);
            bool outcomeUncertainPresentedAsNotRetryable = uncertainText.Contains("INCIERTO", StringComparison.Ordinal) &&
                uncertainText.Contains("no se debe reintentar automáticamente", StringComparison.Ordinal) &&
                uncertainText.Contains("inciertos=1", StringComparison.Ordinal) &&
                !uncertainText.Contains("confirmados=1", StringComparison.Ordinal);

            bool simulatorUsesLocalTransportWithoutOAuth = typeof(DemoPagedCleanupSimulationService).GetConstructors()
                    .SelectMany(constructor => constructor.GetParameters())
                    .All(parameter => parameter.ParameterType != typeof(DriveService) && parameter.ParameterType != typeof(CleanupDriveSession)) &&
                !File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Services", "DemoPagedCleanupSimulationService.cs"))
                    .Contains("GoogleDriveCleanupAuthService", StringComparison.Ordinal) &&
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Services", "DemoPagedCleanupSimulationService.cs"))
                    .Contains("HttpClientFactory = this", StringComparison.Ordinal) &&
                DemoReviewData.IsValidSimulationSeed(seed) && DemoReviewData.SyntheticEmailForSimulation.EndsWith(".invalid", StringComparison.Ordinal);
            bool pagedUiSimulationGuardedAndDoubleClickBlocked = UiSimulationGuardIsEffective();
            bool confirmationDialogRequiresExplicitReview = ConfirmationDialogRequiresExplicitReview();
            string fullHistory = Path.GetFullPath(historyDirectory);
            bool demoHistoryContainedInTemporaryDirectory = fullHistory.StartsWith(
                Path.GetFullPath(seed.DataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            var checks = new DemoPagedCleanupSimulationCheckResult(
                DemoReviewData.IsValidSimulationSeed(seed) && opened.Inventory == seed.Inventory,
                exactConfirmationRequired,
                declinedConfirmationSentNoPatch,
                staleConfirmationSentNoPatch,
                foreignGroupRejectedBeforeTransport,
                blockedPreflightDidNotSimulatePatch,
                crossPageSimulationProtectedKeepAndUndecided,
                successfulSimulationPresentedWithoutClaimingRealWrite,
                outcomeUncertainPresentedAsNotRetryable,
                simulatorUsesLocalTransportWithoutOAuth,
                pagedUiSimulationGuardedAndDoubleClickBlocked,
                confirmationDialogRequiresExplicitReview,
                demoHistoryContainedInTemporaryDirectory);
            Assert(checks.SyntheticCompletedInventoryAccepted, "El simulador no quedó ligado al inventario Full demo aislado.");
            Assert(checks.ExactConfirmationRequired,
                $"La confirmación del fixture sintético difiere: miembros esperados=205/reales={declinedPlan?.LocalReview.MemberCount.ToString() ?? "n/a"}; " +
                $"Undecided esperados=202/reales={declinedPlan?.LocalReview.UndecidedCount.ToString() ?? "n/a"}; " +
                $"Keep esperados=[{string.Join(",", expectedKeepIds)}]/reales=[{string.Join(",", actualKeepIds)}]; " +
                $"Candidate esperados=[{string.Join(",", expectedCandidateIds)}]/reales=[{string.Join(",", actualCandidateIds)}]; " +
                $"bloqueo={declined.BlockingReason ?? "ninguno"}; " +
                $"preflight=[{string.Join(" | ", declined.Preflight?.Preflight.ValidationMessages ?? [])}].");
            Assert(checks.DeclinedConfirmationSentNoPatch, "Cancelar la confirmación produjo una solicitud PATCH simulada.");
            Assert(checks.StaleConfirmationSentNoPatch, "Una confirmación obsoleta inició un PATCH simulado.");
            Assert(checks.ForeignGroupRejectedBeforeTransport, "La simulación aceptó un grupo de otra sesión.");
            Assert(checks.BlockedPreflightDidNotSimulatePatch, "Un preflight local bloqueado produjo un plan o un PATCH simulado.");
            Assert(checks.CrossPageSimulationProtectedKeepAndUndecided,
                $"La simulación cruzada difiere: PATCH=[{string.Join(",", actualPatchIds)}], esperados=[{candidateId}], " +
                $"keep=[{string.Join(",", allKeepIds)}], resultados=[{string.Join(";", actualFileOutcomes)}], " +
                $"solicitudes=[{string.Join(";", simulated.ObservedWriteRequests)}], " +
                $"detalle=[{PagedGroupCleanupSimulationResultForm.BuildDetails(simulated)}].");
            Assert(checks.SuccessfulSimulationPresentedWithoutClaimingRealWrite,
                $"La vista de resultado no distingue simulación de cambio real: offline={simulated.IsOfflineSimulation}, " +
                $"real={simulated.RealExecutionAvailable}, estado={simulated.Operation?.Record.Status}, " +
                $"texto=[{resultText}].");
            Assert(checks.OutcomeUncertainPresentedAsNotRetryable, "OutcomeUncertain no se presenta separadamente ni bloquea el reintento automático.");
            Assert(checks.SimulatorUsesLocalTransportWithoutOAuth, "El simulador acepta una sesión real o no usa el transporte HTTP local.");
            Assert(checks.PagedUiSimulationGuardedAndDoubleClickBlocked, "La acción de simulación no está restringida al demo o no bloquea intentos simultáneos.");
            Assert(checks.ConfirmationDialogRequiresExplicitReview, "La confirmación final no requiere revisión afirmativa de Keep y candidatos exactos.");
            Assert(checks.DemoHistoryContainedInTemporaryDirectory, "El historial de simulación no queda dentro de la carpeta temporal del demo.");
            return checks;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DemoReviewData.TryDelete(seed.DataDirectory);
        }
    }

    private static bool UiSimulationGuardIsEffective()
    {
        string formPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Form1.cs"));
        if (!File.Exists(formPath)) return false;
        string source = File.ReadAllText(formPath);
        string? handler = ExtractHandler(source, "btnPagedSimulateCleanup_Click");
        string? realHandler = ExtractHandler(source, "btnPagedPrepareCleanup_Click");
        string? updateUi = ExtractHandler(source, "UpdatePagedNavigationUi");
        return handler is not null &&
            handler.Contains("if (!_demoMode || _demoSeed is null || _demoPagedCleanupSimulationService is null", StringComparison.Ordinal) &&
            handler.Contains("_pagedCleanupSimulationInProgress", StringComparison.Ordinal) &&
            handler.Contains("_pagedCleanupSimulationCompleted", StringComparison.Ordinal) &&
            handler.Contains("_demoPagedCleanupSimulationService.RunAsync", StringComparison.Ordinal) &&
            !handler.Contains("_driveService", StringComparison.Ordinal) &&
            !handler.Contains("_cleanupDriveService", StringComparison.Ordinal) &&
            !handler.Contains("ConnectSessionAsync", StringComparison.Ordinal) &&
            !handler.Contains("ExecuteConfirmedAsync", StringComparison.Ordinal) &&
            realHandler is not null &&
            realHandler.Contains("if (_demoMode", StringComparison.Ordinal) &&
            realHandler.Contains("_pagedGroupCleanupService.ExecuteConfirmedAsync", StringComparison.Ordinal) &&
            realHandler.Contains("PagedGroupCleanupExecutionDisabledException", StringComparison.Ordinal) &&
            source.Contains("btnPagedSimulateCleanup.Visible = _demoMode", StringComparison.Ordinal) &&
            source.Contains("btnPagedPrepareCleanup.Visible = !_demoMode", StringComparison.Ordinal) &&
            source.Contains("new CleanupHistoryService(_demoDataDirectory is null", StringComparison.Ordinal) &&
            updateUi is not null && updateUi.Contains("!pagedBusy && !pagedCompleted", StringComparison.Ordinal) &&
            updateUi.Contains("_pagedCleanupSimulationInProgress", StringComparison.Ordinal);
    }

    private static bool ConfirmationDialogRequiresExplicitReview()
    {
        string formPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "PagedGroupCleanupConfirmationForm.cs"));
        string servicePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Services", "DemoPagedCleanupSimulationService.cs"));
        if (!File.Exists(formPath) || !File.Exists(servicePath)) return false;
        string form = File.ReadAllText(formPath);
        string service = File.ReadAllText(servicePath);
        return form.Contains("He revisado todos los archivos CONSERVAR", StringComparison.Ordinal) &&
            form.Contains("He revisado todos los candidatos explícitos", StringComparison.Ordinal) &&
            form.Contains("Confirmar simulación offline", StringComparison.Ordinal) &&
            service.Contains("_cleanup.ConfirmPlan", StringComparison.Ordinal) &&
            service.Contains("plan.KeepFiles.Select(file => file.Id)", StringComparison.Ordinal) &&
            service.Contains("plan.CandidateFiles.Select(file => file.Id)", StringComparison.Ordinal);
    }

    private static async Task SetCanTrashAsync(
        SqliteConnectionFactory factory,
        string fileId,
        bool canTrash,
        CancellationToken cancellationToken)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection = await factory.OpenAsync(cancellationToken);
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE DriveFiles SET CanTrash = $canTrash WHERE FileId = $fileId;";
        command.Parameters.AddWithValue("$canTrash", canTrash ? 1 : 0);
        command.Parameters.AddWithValue("$fileId", fileId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("No se pudo preparar el fixture temporal del preflight bloqueado.");
    }

    private static async Task VerifySyntheticDriveMetadataPayloadAsync(
        PagedGroupCleanupLocalPreview preview,
        string fileId,
        CancellationToken cancellationToken)
    {
        var transport = new DemoOfflineDriveTransport(preview.Entries);
        using DriveService drive = transport.CreateDriveService();
        try
        {
            FilesResource.GetRequest request = drive.Files.Get(fileId);
            request.SupportsAllDrives = true;
            request.Fields = "id,name,mimeType,size,md5Checksum,modifiedTime,parents,trashed,explicitlyTrashed,ownedByMe,shared,starred,driveId,version,capabilities(canTrash)";
            Google.Apis.Drive.v3.Data.File file = await request.ExecuteAsync(cancellationToken);
            _ = file.ModifiedTimeDateTimeOffset;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"El transporte offline no pudo deserializar su metadata sintética ({exception.GetType().Name}: {exception.Message}).", exception);
        }
    }

    private static string? ExtractHandler(string source, string handlerName)
    {
        int start = source.IndexOf("private async void " + handlerName, StringComparison.Ordinal);
        if (start < 0)
            start = source.IndexOf("private void " + handlerName, StringComparison.Ordinal);
        if (start < 0) return null;
        int end = source.IndexOf("\n    private ", start + handlerName.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
