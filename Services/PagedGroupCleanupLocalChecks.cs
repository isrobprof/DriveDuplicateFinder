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
    bool NoWritesFromPagedUi,
    bool LocalPreviewCompleteCrossPagePlan,
    bool LocalPreviewRejectsWrongIdentityAndStaleInventory,
    bool LocalPreviewDiscardsObsoleteRequest,
    bool LocalPreviewIsNonExecutableAndRemotePending,
    bool LocalPreviewUiSurfacesBlocks,
    bool RealUiBlocksWriteOAuthBeforeConnect,
    bool ReadOnlyPreflightAndLocalPreparationRemainAvailable,
    bool TrialModeDisabledByDefault,
    bool TrialSingleCandidateAtLimitExecutedOnePatch,
    bool TrialRejectsMultipleCandidates,
    bool TrialRejectsOversizedCandidate,
    bool TrialRejectsUnknownCandidateSize,
    bool TrialKeepsAndUndecidedProtected,
    bool TrialSessionAuthorizationIsSingleUse,
    bool TrialConfirmationRequiresExplicitAcceptance,
    bool TrialAccountScopeScanAndDecisionChangesRejectedBeforePatch,
    bool TrialPreflightStaleRejectedBeforePatch,
    bool TrialUncertainWriteUsesOneAttempt,
    bool TrialRealSessionRejectedWhileDisabled,
    bool RealExecutionBlockedAtCoordinator,
    bool PagedOutcomesAreSeparated);

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
        bool groupIdentityChange = await RejectedAfterMutationAsync(FixtureKind.Standard,
            fixture => fixture.ChangeCandidateChecksumAsync(cancellationToken), cancellationToken);
        bool keepToCandidate = await RejectedAfterMutationAsync(FixtureKind.TwoKeeps,
            fixture => fixture.Reviews.SetExplicitDecisionAsync(fixture.Identity, "keep-file", DuplicateFileDecision.CandidateForTrash, cancellationToken), cancellationToken);
        bool candidateToKeep = await RejectedAfterMutationAsync(FixtureKind.Standard,
            fixture => fixture.Reviews.SetExplicitDecisionAsync(fixture.Identity, "candidate-1", DuplicateFileDecision.Keep, cancellationToken), cancellationToken);
        bool candidateToUndecided = await RejectedAfterMutationAsync(FixtureKind.Standard,
            fixture => fixture.Reviews.SetExplicitDecisionAsync(fixture.Identity, "candidate-1", DuplicateFileDecision.Undecided, cancellationToken), cancellationToken);

        bool localPreviewCompleteCrossPagePlan = false;
        bool localPreviewRejectsWrongIdentityAndStaleInventory = false;
        bool localPreviewDiscardsObsoleteRequest = false;
        bool localPreviewIsNonExecutableAndRemotePending = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.CrossPageDecisions,
                         cancellationToken, prepareApproval: false))
        {
            PagedGroupCleanupLocalPreview preview = await fixture.Coordinator.PrepareLocalPreviewAsync(fixture.Identity, cancellationToken);
            string[] keepIds = preview.Entries.Where(entry => entry.Decision == DuplicateFileDecision.Keep)
                .Select(entry => entry.File.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            string[] candidateIds = preview.Entries.Where(entry => entry.Decision == DuplicateFileDecision.CandidateForTrash)
                .Select(entry => entry.File.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            localPreviewCompleteCrossPagePlan = preview.MemberCount == 205 && preview.KeepCount == 2 &&
                preview.CandidateCount == 100 && preview.UndecidedExcludedCount == 103 && preview.Entries.Count == 102 &&
                keepIds.SequenceEqual(new[] { "z-keep-001", "z-keep-002" }, StringComparer.Ordinal) &&
                candidateIds.SequenceEqual(Enumerable.Range(0, 100).Select(index => $"a-candidate-{index:D3}"), StringComparer.Ordinal) &&
                preview.PotentiallyRecoverableBytes == new System.Numerics.BigInteger(Size) * 100 &&
                fixture.TotalRemoteRequests == 0;
            localPreviewIsNonExecutableAndRemotePending = preview.RemotePreflightPending && !preview.ExecutionEnabled &&
                typeof(PagedGroupCleanupService).GetMethod(nameof(PagedGroupCleanupService.PrepareLocalPreviewAsync)) is { } method &&
                method.GetParameters().All(parameter => parameter.ParameterType != typeof(CleanupDriveSession) &&
                    parameter.ParameterType != typeof(DriveService));

            bool wrongAccountRejected = await LocalPreviewRejectedAsync(fixture,
                new DuplicateGroupIdentity(new ScanInventoryIdentity("other-account", fixture.Inventory.ScopeKey,
                    fixture.Inventory.ScanId), fixture.Identity.SizeBytes, fixture.Identity.NormalizedChecksum), cancellationToken);
            bool wrongScopeRejected = await LocalPreviewRejectedAsync(fixture,
                new DuplicateGroupIdentity(new ScanInventoryIdentity(fixture.Inventory.AccountKey, "other-scope",
                    fixture.Inventory.ScanId), fixture.Identity.SizeBytes, fixture.Identity.NormalizedChecksum), cancellationToken);
            bool wrongScanRejected = await LocalPreviewRejectedAsync(fixture,
                new DuplicateGroupIdentity(new ScanInventoryIdentity(fixture.Inventory.AccountKey, fixture.Inventory.ScopeKey,
                    "other-scan"), fixture.Identity.SizeBytes, fixture.Identity.NormalizedChecksum), cancellationToken);

            var generation = new PagedRequestGeneration();
            PagedRequestLease preparing = generation.Begin();
            await fixture.Reviews.SetExplicitDecisionAsync(fixture.Identity, "a-candidate-000",
                DuplicateFileDecision.Undecided, cancellationToken);
            PagedRequestLease replacement = generation.Begin();
            bool generationInvalidated = preparing.Token.IsCancellationRequested && !generation.IsCurrent(preparing) &&
                generation.IsCurrent(replacement);
            generation.Complete(preparing);
            generation.Complete(replacement);
            bool changedDecisionRejected = await LocalPreviewRejectedAsync(fixture, fixture.Identity, cancellationToken);
            localPreviewDiscardsObsoleteRequest = generationInvalidated && changedDecisionRejected;

            await fixture.Sessions.UpdateStatusAsync(fixture.Inventory.ScanId, ScanStatus.Paused,
                DateTimeOffset.UtcNow, "offline stale inventory fixture", cancellationToken);
            bool staleSessionRejected = await LocalPreviewRejectedAsync(fixture, fixture.Identity, cancellationToken);
            localPreviewRejectsWrongIdentityAndStaleInventory = wrongAccountRejected && wrongScopeRejected &&
                wrongScanRejected && staleSessionRejected;
        }

        bool stalePreflightRejected = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.Standard, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.None);
            try
            {
                _ = await fixture.Coordinator.PreparePlanAsync(cleanup, fixture.Approval with { IsStale = true },
                    cancellationToken: cancellationToken, singleCandidateTrial: true);
            }
            catch (InvalidOperationException) { stalePreflightRejected = true; }
            stalePreflightRejected &= fixture.TransportFor(cleanup).Requests.All(request => request.Method != HttpMethod.Patch);
        }

        bool approvedPreflightStaleBeforeExecution = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.Standard, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.Success);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken, singleCandidateTrial: true);
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

        bool trialModeDisabledByDefault = !PagedGroupCleanupService.IsRealExecutionEnabled &&
            !PagedGroupCleanupService.IsSingleCandidateTrialExecutionEnabled;
        bool trialSingleCandidateAtLimitExecutedOnePatch = false;
        bool trialKeepsAndUndecidedProtected = false;
        bool trialSessionAuthorizationIsSingleUse = false;
        bool trialConfirmationRequiresExplicitAcceptance = TrialConfirmationRequiresExplicitAcceptance();
        await using (TestFixture fixture = await TestFixture.CreateAsync(
                         FixtureKind.SingleCandidateWithUndecided, cancellationToken,
                         groupSize: PagedGroupCleanupService.SingleCandidateTrialMaximumBytes))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.Success);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken, singleCandidateTrial: true);
            PagedGroupCleanupPlan secondPlan = await fixture.PreparePlanAsync(cleanup, cancellationToken, singleCandidateTrial: true);
            bool wrongCandidateRejected = false;
            try
            {
                _ = fixture.Coordinator.ConfirmPlan(plan, plan.KeepFiles.Select(file => file.Id), ["another-file"]);
            }
            catch (InvalidOperationException) { wrongCandidateRejected = true; }
            PagedGroupCleanupConfirmation confirmation = fixture.Confirm(plan);
            PagedGroupCleanupConfirmation secondConfirmation = fixture.Confirm(secondPlan);
            CleanupOperationResult result = await fixture.Coordinator.ExecuteConfirmedAsync(
                cleanup, confirmation, cancellationToken: cancellationToken);
            bool authorizationConsumed = cleanup.HasUsedSingleCandidateTrialAttempt;
            string[] patchIds = fixture.TransportFor(cleanup).Requests.Where(request => request.Method == HttpMethod.Patch)
                .Select(request => request.FileId!).ToArray();
            trialSingleCandidateAtLimitExecutedOnePatch = wrongCandidateRejected && patchIds.SequenceEqual(
                    new[] { "candidate-1" }, StringComparer.Ordinal) &&
                plan.IsSingleCandidateTrial && plan.Identity.SizeBytes == PagedGroupCleanupService.SingleCandidateTrialMaximumBytes &&
                result.Record.Status == CleanupOperationStatus.Completed && result.Record.FileResults.Count == 1 &&
                result.Record.FileResults[0].Status == CleanupFileStatus.Trashed && result.Record.FileResults[0].VerificationSucceeded;
            trialKeepsAndUndecidedProtected = plan.KeepFiles.Select(file => file.Id)
                    .OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(new[] { "keep-file", "keep-file-2" }, StringComparer.Ordinal) &&
                plan.CandidateFiles.Select(file => file.Id).SequenceEqual(new[] { "candidate-1" }, StringComparer.Ordinal) &&
                plan.LocalReview.UndecidedCount == 1 &&
                patchIds.All(id => id != "keep-file" && id != "keep-file-2" && id != "pending-file") &&
                fixture.TransportFor(cleanup).Requests.Where(request => request.Method == HttpMethod.Get)
                    .Select(request => request.FileId).Contains("keep-file", StringComparer.Ordinal) &&
                fixture.TransportFor(cleanup).Requests.Where(request => request.Method == HttpMethod.Get)
                    .Select(request => request.FileId).Contains("keep-file-2", StringComparer.Ordinal) &&
                !fixture.TransportFor(cleanup).Requests.Any(request => request.FileId == "pending-file");
            int patchesBeforeReuse = patchIds.Length;
            bool secondExecutionRejected = false;
            try
            {
                _ = await fixture.Coordinator.ExecuteConfirmedAsync(
                    cleanup, secondConfirmation, cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException) { secondExecutionRejected = true; }
            int patchesAfterReuse = fixture.TransportFor(cleanup).Requests.Count(request => request.Method == HttpMethod.Patch);
            trialSessionAuthorizationIsSingleUse = secondExecutionRejected &&
                authorizationConsumed && patchesAfterReuse == patchesBeforeReuse;
        }

        bool trialRejectsMultipleCandidates = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.MultipleCandidates, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.None);
            try { _ = await fixture.PreparePlanAsync(cleanup, cancellationToken, singleCandidateTrial: true); }
            catch (InvalidOperationException) { trialRejectsMultipleCandidates = true; }
            trialRejectsMultipleCandidates &= fixture.TransportFor(cleanup).Requests.All(request => request.Method != HttpMethod.Patch);
        }

        bool trialRejectsOversizedCandidate = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.Standard, cancellationToken,
                         groupSize: PagedGroupCleanupService.SingleCandidateTrialMaximumBytes + 1))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.None);
            try { _ = await fixture.PreparePlanAsync(cleanup, cancellationToken, singleCandidateTrial: true); }
            catch (InvalidOperationException exception)
            {
                trialRejectsOversizedCandidate = exception.Message.Contains("tamaño", StringComparison.OrdinalIgnoreCase);
            }
            trialRejectsOversizedCandidate &= fixture.TransportFor(cleanup).Requests.All(request => request.Method != HttpMethod.Patch);
        }

        bool trialRejectsUnknownCandidateSize = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.Standard, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.None);
            CleanupPreflightResult unknownSizePreflight = CopyPreflight(fixture.Approval.Preflight,
                fixture.Approval.Preflight.CandidateFiles.Select(file => CopySnapshot(file, size: null)));
            PagedGroupPreflightResult unknownSizeApproval = fixture.Approval with { Preflight = unknownSizePreflight };
            try
            {
                _ = await fixture.Coordinator.PreparePlanAsync(cleanup, unknownSizeApproval,
                    cancellationToken: cancellationToken, singleCandidateTrial: true);
            }
            catch (InvalidOperationException exception)
            {
                trialRejectsUnknownCandidateSize = exception.Message.Contains("tamaño", StringComparison.OrdinalIgnoreCase);
            }
            trialRejectsUnknownCandidateSize &= fixture.TransportFor(cleanup).Requests.All(request => request.Method != HttpMethod.Patch);
        }

        bool trialUncertainWriteUsesOneAttempt = false;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.Standard, cancellationToken))
        {
            using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.LoseResponseAfterApply);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken, singleCandidateTrial: true);
            CleanupOperationResult result = await fixture.Coordinator.ExecuteConfirmedAsync(
                cleanup, fixture.Confirm(plan), cancellationToken: cancellationToken);
            int patches = fixture.TransportFor(cleanup).Requests.Count(request => request.Method == HttpMethod.Patch);
            bool priorAuthorizationRejected = false;
            try
            {
                _ = await fixture.Coordinator.ExecuteConfirmedAsync(
                    cleanup, fixture.Confirm(plan), cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException) { priorAuthorizationRejected = true; }
            int patchesAfterReuse = fixture.TransportFor(cleanup).Requests.Count(request => request.Method == HttpMethod.Patch);
            trialUncertainWriteUsesOneAttempt = patches == 1 && fixture.TransportFor(cleanup).WriteAttempts == 1 &&
                priorAuthorizationRejected && patchesAfterReuse == 1 && cleanup.HasUsedSingleCandidateTrialAttempt &&
                cleanup.DriveService.HttpClient.MessageHandler.NumTries == 1 &&
                result.Record.FileResults.Count == 1 && result.Record.FileResults[0].Status == CleanupFileStatus.Failed &&
                result.Record.FileResults[0].FailureReason == "OutcomeUncertain" &&
                result.Record.FileResults[0].FailureStage == "Files.UpdateOutcomeUncertain";
        }

        bool trialIdentityAndDecisionChangesRejected = accountChange && scopeChange && scanChange && invalidSession && groupIdentityChange &&
            keepToCandidate && candidateToKeep && candidateToUndecided;
        bool trialStalePreflightRejected = stalePreflightRejected && approvedPreflightStaleBeforeExecution;

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

        bool realExecutionBlockedAtCoordinator = !PagedGroupCleanupService.IsRealExecutionEnabled;
        bool trialRealSessionRejectedWhileDisabled = !PagedGroupCleanupService.IsSingleCandidateTrialExecutionEnabled;
        await using (TestFixture fixture = await TestFixture.CreateAsync(FixtureKind.Standard, cancellationToken))
        {
            using CleanupDriveSession authenticatedSession = fixture.CreateCleanupSession(
                Email, WriteBehavior.Success, offlineSimulation: false);
            PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(authenticatedSession, cancellationToken);
            PagedGroupCleanupConfirmation confirmation = fixture.Confirm(plan);
            int requestsBeforeExecute = fixture.TransportFor(authenticatedSession).Requests.Count;
            bool blocked = false;
            try
            {
                _ = await fixture.Coordinator.ExecuteConfirmedAsync(
                    authenticatedSession, confirmation, cancellationToken: cancellationToken);
            }
            catch (PagedGroupCleanupExecutionDisabledException) { blocked = true; }

            ReviewGroupPersistenceState? reviewState = await fixture.Reviews.GetGroupStateAsync(
                fixture.Identity, cancellationToken);
            bool noHistory = !Directory.Exists(Path.Combine(fixture.RootPath, "history"));
            realExecutionBlockedAtCoordinator &= blocked &&
                fixture.TransportFor(authenticatedSession).Requests.Count == requestsBeforeExecute &&
                fixture.TransportFor(authenticatedSession).Requests.All(request => request.Method != HttpMethod.Patch) &&
                reviewState is { IsReviewConfirmed: true } && noHistory;

            using CleanupDriveSession trialSession = fixture.CreateCleanupSession(
                Email, WriteBehavior.Success, offlineSimulation: false);
            PagedGroupCleanupPlan trialPlan = await fixture.PreparePlanAsync(
                trialSession, cancellationToken, singleCandidateTrial: true);
            PagedGroupCleanupConfirmation trialConfirmation = fixture.Confirm(trialPlan);
            int trialRequestsBefore = fixture.TransportFor(trialSession).Requests.Count;
            bool trialBlocked = false;
            try
            {
                _ = await fixture.Coordinator.ExecuteConfirmedAsync(
                    trialSession, trialConfirmation, cancellationToken: cancellationToken);
            }
            catch (PagedGroupCleanupExecutionDisabledException) { trialBlocked = true; }
            trialRealSessionRejectedWhileDisabled &= trialBlocked &&
                fixture.TransportFor(trialSession).Requests.Count == trialRequestsBefore &&
                fixture.TransportFor(trialSession).Requests.All(request => request.Method != HttpMethod.Patch);
            realExecutionBlockedAtCoordinator &= trialRealSessionRejectedWhileDisabled;
        }

        var outcomeRecord = new CleanupOperationRecord
        {
            OperationId = "offline-paged-outcomes",
            StartedAtUtc = DateTimeOffset.UtcNow,
            StableGroupId = "synthetic-group",
            FileSize = Size,
            Md5Checksum = Hash
        };
        outcomeRecord.FileResults.Add(Outcome("confirmed", CleanupFileStatus.Trashed, verified: true));
        outcomeRecord.FileResults.Add(Outcome("pending-verification", CleanupFileStatus.Trashed, verified: false));
        outcomeRecord.FileResults.Add(Outcome("failed", CleanupFileStatus.Failed, reason: "RemoteFailure"));
        outcomeRecord.FileResults.Add(Outcome("uncertain", CleanupFileStatus.Failed, stage: "Files.UpdateOutcomeUncertain", reason: "OutcomeUncertain"));
        outcomeRecord.FileResults.Add(Outcome("cancelled", CleanupFileStatus.Cancelled));
        outcomeRecord.FileResults.Add(Outcome("not-processed", CleanupFileStatus.NotProcessed));
        string outcomeSummary = Form1.BuildPagedCleanupOutcomeSummary(outcomeRecord);
        bool pagedOutcomesAreSeparated = outcomeSummary.Contains("Confirmados y verificados (1)", StringComparison.Ordinal) &&
            outcomeSummary.Contains("Pendientes de verificar (1)", StringComparison.Ordinal) &&
            outcomeSummary.Contains("Fallidos (1)", StringComparison.Ordinal) &&
            outcomeSummary.Contains("Inciertos (1)", StringComparison.Ordinal) &&
            outcomeSummary.Contains("Cancelados (1)", StringComparison.Ordinal) &&
            outcomeSummary.Contains("No procesados (1)", StringComparison.Ordinal) &&
            outcomeSummary.Contains("No repitas la operación automáticamente", StringComparison.Ordinal);

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
            NoPagedCleanupUiReferences(), localPreviewCompleteCrossPagePlan,
            localPreviewRejectsWrongIdentityAndStaleInventory, localPreviewDiscardsObsoleteRequest,
            localPreviewIsNonExecutableAndRemotePending, PagedPreviewUiSurfacesBlocks(),
            RealUiBlocksWriteOAuthBeforeConnect(), ReadOnlyPreflightAndLocalPreparationRemainAvailable(),
            trialModeDisabledByDefault, trialSingleCandidateAtLimitExecutedOnePatch,
            trialRejectsMultipleCandidates, trialRejectsOversizedCandidate, trialRejectsUnknownCandidateSize,
            trialKeepsAndUndecidedProtected, trialSessionAuthorizationIsSingleUse,
            trialConfirmationRequiresExplicitAcceptance, trialIdentityAndDecisionChangesRejected,
            trialStalePreflightRejected, trialUncertainWriteUsesOneAttempt, trialRealSessionRejectedWhileDisabled,
            realExecutionBlockedAtCoordinator,
            pagedOutcomesAreSeparated);

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
        Assert(checks.NoWritesFromPagedUi, "La UI paginada no enruta la ejecución únicamente por el coordinador protegido.");
        Assert(checks.LocalPreviewCompleteCrossPagePlan, "La vista previa local no reconstruyó todas las decisiones repartidas entre páginas o calculó mal los bytes.");
        Assert(checks.LocalPreviewRejectsWrongIdentityAndStaleInventory, "La vista previa aceptó otra cuenta, alcance, sesión o un inventario no vigente.");
        Assert(checks.LocalPreviewDiscardsObsoleteRequest, "Una preparación obsoleta o una confirmación invalidada produjo un plan visible.");
        Assert(checks.LocalPreviewIsNonExecutableAndRemotePending, "La vista previa local adquirió capacidad de ejecución o se presentó como preflight remoto.");
        Assert(checks.LocalPreviewUiSurfacesBlocks, "La interfaz no conecta la preparación local o no presenta los bloqueos del adaptador.");
        Assert(checks.RealUiBlocksWriteOAuthBeforeConnect,
            "La UI solicitó OAuth de escritura o abrió confirmación de ejecución con la guarda real cerrada.");
        Assert(checks.ReadOnlyPreflightAndLocalPreparationRemainAvailable,
            "El bloqueo de escritura también impidió la preparación local o el preflight de solo lectura.");
        Assert(checks.TrialModeDisabledByDefault, "La modalidad limitada o la ejecución general no permanecen desactivadas.");
        Assert(checks.TrialSingleCandidateAtLimitExecutedOnePatch, "La prueba local válida en el límite no produjo exactamente un PATCH simulado confirmado.");
        Assert(checks.TrialRejectsMultipleCandidates, "La modalidad limitada aceptó más de un candidato.");
        Assert(checks.TrialRejectsOversizedCandidate, "La modalidad limitada aceptó un candidato superior a 10.000.000 bytes.");
        Assert(checks.TrialRejectsUnknownCandidateSize, "La modalidad limitada aceptó un tamaño de candidato desconocido.");
        Assert(checks.TrialKeepsAndUndecidedProtected, "La prueba limitada envió Keep o Undecided a la papelera simulada.");
        Assert(checks.TrialSessionAuthorizationIsSingleUse, "Se pudo reutilizar la sesión de autorización de prueba tras el primer intento.");
        Assert(checks.TrialConfirmationRequiresExplicitAcceptance, "La UI no exige aceptación final explícita del candidato y todos los Keep.");
        Assert(checks.TrialAccountScopeScanAndDecisionChangesRejectedBeforePatch,
            "La modalidad limitada no rechazó cambios de cuenta, alcance, sesión, grupo o decisiones antes de PATCH.");
        Assert(checks.TrialPreflightStaleRejectedBeforePatch, "La modalidad limitada aceptó un preflight obsoleto.");
        Assert(checks.TrialUncertainWriteUsesOneAttempt, "Un resultado incierto de la prueba limitada se reintentó o se clasificó como confirmado.");
        Assert(checks.TrialRealSessionRejectedWhileDisabled, "Una sesión OAuth real atravesó el interruptor apagado de la modalidad limitada.");
        Assert(checks.RealExecutionBlockedAtCoordinator,
            "Una sesión real atravesó la guarda cerrada del coordinador o produjo efectos de limpieza.");
        Assert(checks.PagedOutcomesAreSeparated, "La pantalla de resultado paginado mezcla confirmados, pendientes, fallidos o inciertos.");
        return checks;
    }

    private static CleanupFileResult Outcome(
        string id,
        CleanupFileStatus status,
        bool verified = false,
        string? stage = null,
        string? reason = null) => new()
    {
        File = new CleanupFileSnapshot { Id = id, Name = id, MimeType = "application/pdf" },
        Status = status,
        VerificationSucceeded = verified,
        FailureStage = stage,
        FailureReason = reason
    };

    private static CleanupPreflightResult CopyPreflight(
        CleanupPreflightResult source,
        IEnumerable<CleanupFileSnapshot> candidates)
    {
        var copy = new CleanupPreflightResult
        {
            Review = source.Review,
            IsSuccessful = source.IsSuccessful,
            WasCancelled = source.WasCancelled,
            FailureKind = source.FailureKind,
            KeepFile = source.KeepFile,
            KeepFiles = source.KeepFiles.ToList()
        };
        copy.CandidateFiles.AddRange(candidates);
        return copy;
    }

    private static CleanupFileSnapshot CopySnapshot(CleanupFileSnapshot source, long? size) => new()
    {
        Id = source.Id,
        Name = source.Name,
        MimeType = source.MimeType,
        Size = size,
        Md5Checksum = source.Md5Checksum,
        ModifiedTime = source.ModifiedTime,
        ParentIds = source.ParentIds,
        Trashed = source.Trashed,
        ExplicitlyTrashed = source.ExplicitlyTrashed,
        OwnedByMe = source.OwnedByMe,
        IsShared = source.IsShared,
        IsStarred = source.IsStarred,
        DriveId = source.DriveId,
        Version = source.Version,
        CanTrash = source.CanTrash,
        Path = source.Path
    };

    private static bool TrialConfirmationRequiresExplicitAcceptance()
    {
        string formPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Form1.cs"));
        string confirmationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "PagedGroupCleanupConfirmationForm.cs"));
        if (!File.Exists(formPath) || !File.Exists(confirmationPath)) return false;
        string authPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Services", "GoogleDriveCleanupAuthService.cs"));
        if (!File.Exists(authPath)) return false;
        string source = File.ReadAllText(formPath);
        string? handler = ExtractHandler(source, "btnPagedPrepareCleanup_Click");
        string confirmation = File.ReadAllText(confirmationPath);
        string auth = File.ReadAllText(authPath);
        string servicePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Services", "PagedGroupCleanupService.cs"));
        if (!File.Exists(servicePath)) return false;
        string service = File.ReadAllText(servicePath);
        int acceptedDialog = handler?.IndexOf("finalConfirmation.ShowDialog(this) != DialogResult.OK", StringComparison.Ordinal) ?? -1;
        int tokenCreation = handler?.IndexOf("_pagedGroupCleanupService.ConfirmPlan(", StringComparison.Ordinal) ?? -1;
        int trialConfirm = handler?.IndexOf("singleCandidateTrial: singleCandidateTrial", StringComparison.Ordinal) ?? -1;
        int trialAdmission = handler?.IndexOf("GetSingleCandidateTrialBlockReason", StringComparison.Ordinal) ?? -1;
        int oauthRequest = handler?.IndexOf("ConnectSessionAsync", StringComparison.Ordinal) ?? -1;
        return handler is not null && acceptedDialog >= 0 && trialConfirm >= 0 && trialConfirm < acceptedDialog &&
            tokenCreation > acceptedDialog && confirmation.Contains("TODOS los archivos que se conservarán", StringComparison.Ordinal) &&
            confirmation.Contains("ÚNICAMENTE estos candidatos explícitos", StringComparison.Ordinal) &&
            confirmation.Contains("Confirmar un único candidato", StringComparison.Ordinal) &&
            trialAdmission >= 0 && oauthRequest > trialAdmission &&
            handler.Contains("requireAllKeepSnapshots: false", StringComparison.Ordinal) &&
            service.Contains("private const bool RealPagedCleanupExecutionEnabled = false", StringComparison.Ordinal) &&
            service.Contains("private const bool SingleCandidateTrialExecutionEnabled = false", StringComparison.Ordinal) &&
            service.Contains("SingleCandidateTrialMaximumBytes = 10_000_000", StringComparison.Ordinal) &&
            auth.Contains("CleanupScope = DriveService.Scope.Drive", StringComparison.Ordinal) &&
            auth.Contains("HasCleanupScope(credential.Token.Scope)", StringComparison.Ordinal) &&
            auth.Contains("isOfflineSimulation: false", StringComparison.Ordinal);
    }

    private static async Task<bool> LocalPreviewRejectedAsync(
        TestFixture fixture,
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await fixture.Coordinator.PrepareLocalPreviewAsync(identity, cancellationToken);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task<bool> RejectedAfterMutationAsync(
        FixtureKind kind,
        Func<TestFixture, Task> mutate,
        CancellationToken cancellationToken)
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(kind, cancellationToken);
        using CleanupDriveSession cleanup = fixture.CreateCleanupSession(Email, WriteBehavior.None);
        PagedGroupCleanupPlan plan = await fixture.PreparePlanAsync(cleanup, cancellationToken, singleCandidateTrial: true);
        PagedGroupCleanupConfirmation confirmation = fixture.Confirm(plan);
        await mutate(fixture);
        bool rejected = false;
        try { _ = await fixture.Coordinator.ExecuteConfirmedAsync(cleanup, confirmation, cancellationToken: cancellationToken); }
        catch (InvalidOperationException) { rejected = true; }
        return rejected && fixture.TransportFor(cleanup).Requests.All(request => request.Method != HttpMethod.Patch);
    }

    private static bool NoPagedCleanupUiReferences()
    {
        // The normal paged path may call only the single-group coordinator, whose hard gate is closed.
        string sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Form1.cs"));
        if (!File.Exists(sourcePath) || PagedReadOnlyViewSafety.LegacyActionsEnabled(pagedReadOnlyMode: true)) return false;
        string source = File.ReadAllText(sourcePath);
        string? previewHandler = ExtractHandler(source, "btnPagedPrepareGroupPlan_Click");
        string? executionHandler = ExtractHandler(source, "btnPagedPrepareCleanup_Click");
        string coordinatorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Services", "PagedGroupCleanupService.cs"));
        if (!File.Exists(coordinatorPath)) return false;
        string coordinator = File.ReadAllText(coordinatorPath);
        int preflightIndex = executionHandler?.IndexOf("_pagedGroupPreflightService.CheckOneGroupAsync", StringComparison.Ordinal) ?? -1;
        int reportIndex = executionHandler?.IndexOf("new PagedGroupPreflightResultForm", StringComparison.Ordinal) ?? -1;
        int authIndex = executionHandler?.IndexOf("ConnectSessionAsync", StringComparison.Ordinal) ?? -1;
        int planIndex = executionHandler?.IndexOf("_pagedGroupCleanupService.PreparePlanAsync", StringComparison.Ordinal) ?? -1;
        int confirmationIndex = executionHandler?.IndexOf("new PagedGroupCleanupConfirmationForm(", StringComparison.Ordinal) ?? -1;
        int executeIndex = executionHandler?.IndexOf("_pagedGroupCleanupService.ExecuteConfirmedAsync", StringComparison.Ordinal) ?? -1;
        return source.Contains("_pagedGroupCleanupService.PrepareLocalPreviewAsync(identity, request.Token)", StringComparison.Ordinal) &&
            previewHandler is not null && !previewHandler.Contains("ExecuteConfirmedAsync", StringComparison.Ordinal) &&
            executionHandler is not null &&
            executionHandler.Contains("PagedGroupCleanupExecutionDisabledException", StringComparison.Ordinal) &&
            executionHandler.Contains("ShowPagedCleanupOutcome(operation)", StringComparison.Ordinal) &&
            !executionHandler.Contains("_cleanupBatchService.ExecuteAsync", StringComparison.Ordinal) &&
            !executionHandler.Contains("_trashService.ExecuteAsync", StringComparison.Ordinal) &&
            preflightIndex >= 0 && reportIndex > preflightIndex && authIndex > reportIndex &&
            planIndex > authIndex && confirmationIndex > planIndex && executeIndex > confirmationIndex &&
            source.Contains("btnPagedPrepareCleanup.Visible = !_demoMode", StringComparison.Ordinal) &&
            coordinator.Contains("private const bool RealPagedCleanupExecutionEnabled = false", StringComparison.Ordinal) &&
            coordinator.Contains("private const bool SingleCandidateTrialExecutionEnabled = false", StringComparison.Ordinal) &&
            coordinator.Contains("if (!RealPagedCleanupExecutionEnabled)", StringComparison.Ordinal) &&
            coordinator.Contains("if (!SingleCandidateTrialExecutionEnabled)", StringComparison.Ordinal) &&
            HandlerHasPagedGuardBeforeExecution(source, "btnEnviarGruposPapelera_Click", "_cleanupBatchService.ExecuteAsync") &&
            HandlerHasPagedGuardBeforeExecution(source, "btnEnviarGrupoPapelera_Click", "_trashService.ExecuteAsync");
    }

    private static bool RealUiBlocksWriteOAuthBeforeConnect()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Form1.cs"));
        if (!File.Exists(sourcePath)) return false;
        string? handler = ExtractHandler(File.ReadAllText(sourcePath), "btnPagedPrepareCleanup_Click");
        if (handler is null) return false;
        int preflightResultIndex = handler.IndexOf("CheckOneGroupAsync", StringComparison.Ordinal);
        int resultReviewIndex = handler.IndexOf("new PagedGroupPreflightResultForm", StringComparison.Ordinal);
        int closedGateIndex = handler.IndexOf("if (!PagedGroupCleanupService.IsRealExecutionEnabled && !singleCandidateTrial)", StringComparison.Ordinal);
        int oauthIndex = handler.IndexOf("ConnectSessionAsync", StringComparison.Ordinal);
        int finalConfirmationIndex = handler.IndexOf("new PagedGroupCleanupConfirmationForm(", StringComparison.Ordinal);
        int disabledMessageIndex = handler.IndexOf("executionDisabledMessage =", StringComparison.Ordinal);
        int returnIndex = handler.IndexOf("return;", closedGateIndex, StringComparison.Ordinal);
        return preflightResultIndex >= 0 && resultReviewIndex > preflightResultIndex &&
            closedGateIndex > resultReviewIndex && disabledMessageIndex > closedGateIndex &&
            handler.Contains("IsSingleCandidateTrialExecutionEnabled", StringComparison.Ordinal) &&
            handler.Contains("GetSingleCandidateTrialBlockReason", StringComparison.Ordinal) &&
            handler.Contains("No se solicitará OAuth de escritura", StringComparison.Ordinal) &&
            returnIndex > disabledMessageIndex && oauthIndex > returnIndex &&
            finalConfirmationIndex > oauthIndex &&
            handler.Contains("MessageBox.Show(executionDisabledMessage", StringComparison.Ordinal);
    }

    private static bool ReadOnlyPreflightAndLocalPreparationRemainAvailable()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Form1.cs"));
        if (!File.Exists(sourcePath)) return false;
        string source = File.ReadAllText(sourcePath);
        string? localPreparationHandler = ExtractHandler(source, "btnPagedPrepareGroupPlan_Click");
        string? preflightHandler = ExtractHandler(source, "btnPagedCheckDrive_Click");
        return localPreparationHandler is not null &&
            localPreparationHandler.Contains("PrepareLocalPreviewAsync", StringComparison.Ordinal) &&
            preflightHandler is not null &&
            preflightHandler.Contains("CheckOneGroupAsync", StringComparison.Ordinal) &&
            preflightHandler.Contains("DriveMetadataReadonly", StringComparison.Ordinal);
    }

    private static bool PagedPreviewUiSurfacesBlocks()
    {
        string formPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Form1.cs"));
        string previewPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "PagedGroupCleanupLocalPreviewForm.cs"));
        if (!File.Exists(formPath) || !File.Exists(previewPath)) return false;
        string? handler = ExtractHandler(File.ReadAllText(formPath), "btnPagedPrepareGroupPlan_Click");
        if (handler is null) return false;
        string preview = File.ReadAllText(previewPath);
        return handler.Contains("_pagedGroupReviewConfirmed", StringComparison.Ordinal) &&
            handler.Contains("exception.Message", StringComparison.Ordinal) && handler.Contains("MessageBox.Show", StringComparison.Ordinal) &&
            handler.Contains("_pagedRequestGeneration.IsCurrent(request)", StringComparison.Ordinal) &&
            handler.Contains("_activePagedGroup != identity", StringComparison.Ordinal) &&
            preview.Contains("UndecidedExcludedCount", StringComparison.Ordinal) &&
            preview.Contains("PREPARACIÓN LOCAL NO EJECUTABLE", StringComparison.Ordinal) &&
            preview.Contains("preflight remoto", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractHandler(string source, string handlerName)
    {
        int start = source.IndexOf("private async void " + handlerName, StringComparison.Ordinal);
        if (start < 0) return null;
        int end = source.IndexOf("\n    private ", start + handlerName.Length, StringComparison.Ordinal);
        if (end < 0) end = source.Length;
        return source[start..end];
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

    private enum FixtureKind { Standard, TwoKeeps, MultipleCandidates, CrossPageDecisions, SingleCandidateWithUndecided }
    private enum WriteBehavior { None, Success, FailSecondBeforeApply, LoseSecondResponseAfterApply, LoseResponseAfterApply }
    private sealed record CapturedRequest(HttpMethod Method, string? FileId);

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly long _groupSize;
        private readonly Dictionary<CleanupDriveSession, LocalTransport> _transports = new();
        private TestFixture(string root, SqliteConnectionFactory factory, ScanSessionRepository sessions,
            ReviewStateRepository reviews, ScanInventoryIdentity inventory, DuplicateGroupIdentity identity,
            GoogleDriveFileService mapper, long groupSize)
        {
            _root = root; _groupSize = groupSize; Factory = factory; Sessions = sessions; Reviews = reviews; Inventory = inventory;
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
        public int TotalRemoteRequests => _transports.Values.Sum(transport => transport.Requests.Count);

        public static async Task<TestFixture> CreateAsync(
            FixtureKind kind,
            CancellationToken token,
            bool prepareApproval = true,
            long? groupSize = null)
        {
            long actualGroupSize = groupSize ?? Size;
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
                var identity = new DuplicateGroupIdentity(inventory, actualGroupSize, Hash);
                DriveFileInfo[] files = FilesFor(kind, actualGroupSize);
                await CreateInventoryAsync(factory, sessions, reviews, inventory, identity, files, actualGroupSize, token);
                var fixture = new TestFixture(root, factory, sessions, reviews, inventory, identity,
                    new GoogleDriveFileService(), actualGroupSize);
                if (prepareApproval)
                {
                    using DriveService readDrive = new LocalTransport(Email, WriteBehavior.None, actualGroupSize).CreateDrive();
                    fixture.Approval = await new PagedGroupPreflightService(fixture.Scan, fixture.Adapter, fixture.Trash)
                        .CheckOneGroupAsync(readDrive, identity, cancellationToken: token);
                    if (!fixture.Approval.Preflight.IsSuccessful || fixture.Approval.IsStale)
                        throw new InvalidOperationException("La aprobación local de preflight no se pudo construir en el fixture.");
                }
                return fixture;
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public string RootPath => _root;

        public CleanupDriveSession CreateCleanupSession(string email, WriteBehavior behavior, bool offlineSimulation = true)
        {
            var transport = new LocalTransport(email, behavior, _groupSize);
            var session = new CleanupDriveSession(transport.CreateDrive(), isOfflineSimulation: offlineSimulation);
            _transports.Add(session, transport);
            return session;
        }

        public LocalTransport TransportFor(CleanupDriveSession session) => _transports[session];

        public async Task<PagedGroupCleanupPlan> PreparePlanAsync(
            CleanupDriveSession session,
            CancellationToken token,
            bool singleCandidateTrial = false)
        {
            return await Coordinator.PreparePlanAsync(session, Approval, cancellationToken: token,
                singleCandidateTrial: singleCandidateTrial);
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

        public async Task ChangeCandidateChecksumAsync(CancellationToken token)
        {
            await using SqliteConnection connection = await Factory.OpenAsync(token);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE DriveFiles SET Md5Checksum=$checksum WHERE FileId='candidate-1';";
            command.Parameters.AddWithValue("$checksum", "fedcbafedcbafedcbafedcbafedcbafe");
            await command.ExecuteNonQueryAsync(token);
        }

        public async ValueTask DisposeAsync()
        {
            _transports.Clear();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            await ValueTask.CompletedTask;
        }

        private static DriveFileInfo[] FilesFor(FixtureKind kind, long groupSize)
        {
            string[] ids = kind switch
            {
                FixtureKind.TwoKeeps => ["keep-file", "keep-file-2", "candidate-1"],
                FixtureKind.MultipleCandidates => ["keep-file", "keep-file-2", "candidate-1", "candidate-2", "pending-file"],
                FixtureKind.SingleCandidateWithUndecided => ["keep-file", "keep-file-2", "candidate-1", "pending-file"],
                FixtureKind.CrossPageDecisions => [
                    .. Enumerable.Range(0, 100).Select(index => $"a-candidate-{index:D3}"),
                    .. Enumerable.Range(0, 103).Select(index => $"m-pending-{index:D3}"),
                    "z-keep-001", "z-keep-002"],
                _ => ["keep-file", "candidate-1"]
            };
            return ids.Select(id => new DriveFileInfo
            {
                Id = id, Name = id + ".pdf", MimeType = "application/pdf", Size = groupSize, Md5Checksum = Hash,
                ParentIds = ["root"], Path = "Mi unidad/Test/" + id + ".pdf", OwnedByMe = true,
                IsShared = false, IsStarred = false, CanTrash = true
            }).ToArray();
        }

        private static async Task CreateInventoryAsync(SqliteConnectionFactory factory, ScanSessionRepository sessions,
            ReviewStateRepository reviews, ScanInventoryIdentity inventory, DuplicateGroupIdentity identity,
            IReadOnlyList<DriveFileInfo> files, long groupSize, CancellationToken token)
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
                        ScopeKey = inventory.ScopeKey, MimeType = file.MimeType, SizeBytes = groupSize,
                        Md5Checksum = Hash, ParentIds = ["root"], OwnedByMe = true, CanTrash = true,
                        IsShared = false, IsStarred = false, LastSeenScanId = inventory.ScanId, CachedAtUtc = now
                    }, token);
                await transaction.CommitAsync(token);
            }
            await sessions.UpdateStatusAsync(inventory.ScanId, ScanStatus.Completed, DateTimeOffset.UtcNow, cancellationToken: token);
            await reviews.RegisterCompletedInventoryAsync(inventory, token);
            await reviews.RegisterGroupAsync(identity, token);
            foreach (DriveFileInfo file in files.Where(file => file.Id.StartsWith("keep-", StringComparison.Ordinal) ||
                         file.Id.StartsWith("z-keep-", StringComparison.Ordinal)))
                await reviews.SetExplicitDecisionAsync(identity, file.Id, DuplicateFileDecision.Keep, token);
            foreach (DriveFileInfo file in files.Where(file => file.Id.Contains("candidate-", StringComparison.Ordinal)))
                await reviews.SetExplicitDecisionAsync(identity, file.Id, DuplicateFileDecision.CandidateForTrash, token);
            await reviews.ConfirmGroupReviewAsync(identity, token);
        }
    }

    private sealed class LocalTransport
    {
        private readonly string _email;
        private readonly WriteBehavior _behavior;
        private readonly long _groupSize;
        private readonly HashSet<string> _trashed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _uncertain = new(StringComparer.Ordinal);
        private bool _remoteMetadataChanged;
        public List<CapturedRequest> Requests { get; } = [];
        public int WriteAttempts { get; private set; }
        public LocalTransport(string email, WriteBehavior behavior, long groupSize)
        { _email = email; _behavior = behavior; _groupSize = groupSize; }
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
                if ((_behavior == WriteBehavior.LoseSecondResponseAfterApply && id == "candidate-2") ||
                    (_behavior == WriteBehavior.LoseResponseAfterApply && id == "candidate-1"))
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
                mimeType = "application/pdf", size = _groupSize.ToString(), md5Checksum = Hash,
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
