using System.Security.Cryptography;
using System.Text;
using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using DriveDuplicateFinder.Services;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite;

public sealed record ReviewStatePersistenceCheckResult(
    string TemporaryDirectory,
    bool TemporaryDirectoryDeleted,
    bool MigrationsOneThroughFourAppliedIdempotently,
    bool PagedReviewWorkflowVerified,
    bool PagedCleanupPlanVerified,
    bool DecisionLifecyclePreserved,
    bool SelectionPersistedAndCleared,
    bool ReopenedDatabasePreservedState,
    bool AccountScopeSessionAndGroupIsolation,
    bool IncompleteAndObsoleteInventoriesRejected,
    bool FileMembershipIntegrityEnforced,
    bool LegacyJsonPreserved);

/// <summary>Deterministic, offline checks for inventory-scoped review persistence.</summary>
public static class ReviewStatePersistenceLocalChecks
{
    public static async Task<ReviewStatePersistenceCheckResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(Path.GetTempPath(), "DriveDuplicateFinder", $"review-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string legacyJsonPath = Path.Combine(root, "review-state.json");
        byte[] legacyJson = Encoding.UTF8.GetBytes("{\"legacy\":\"must remain untouched\"}");
        await File.WriteAllBytesAsync(legacyJsonPath, legacyJson, cancellationToken);
        string legacyHash = Convert.ToHexString(SHA256.HashData(legacyJson));
        ReviewStatePersistenceCheckResult? result = null;
        try
        {
            var factory = new SqliteConnectionFactory(new LocalDataPathService(root));
            var initializer = new SqliteDatabaseInitializer(factory);
            await initializer.InitializeAsync(cancellationToken);
            await initializer.InitializeAsync(cancellationToken);
            var sessions = new ScanSessionRepository(factory);
            var files = new DriveFileCacheRepository(factory);
            var repository = new ReviewStateRepository(factory);

            IReadOnlyList<int> versions = await ReadMigrationVersionsAsync(factory, cancellationToken);
            bool migrationsOneThroughFourAppliedIdempotently = versions.SequenceEqual([1, 2, 3, 4]);
            Assert(migrationsOneThroughFourAppliedIdempotently, "Las migraciones SQLite 1 a 4 no se aplicaron una sola vez.");

            var scope = new ScanInventoryIdentity("review-account", "review-scope", "review-current");
            var oldInventory = new ScanInventoryIdentity(scope.AccountKey, scope.ScopeKey, "review-old");
            await CreateCompletedScanAsync(sessions, oldInventory, DateTimeOffset.UtcNow.AddMinutes(-3), cancellationToken);
            await InsertGroupFilesAsync(files, oldInventory, "review-hash", ["old-a", "old-b"], cancellationToken);
            await repository.RegisterCompletedInventoryAsync(oldInventory, cancellationToken);
            var oldGroup = new DuplicateGroupIdentity(oldInventory, 123, "review-hash");
            await repository.RegisterGroupAsync(oldGroup, cancellationToken);
            await repository.SetDecisionAsync(oldGroup, "old-a", DuplicateFileDecision.Keep, cancellationToken);

            await CreateIncompleteScanAsync(sessions, scope, cancellationToken);
            bool olderSessionRejected = false;
            try
            {
                await repository.GetGroupStateAsync(oldGroup, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                olderSessionRejected = true;
            }
            bool olderExplicitKeepRejected = false;
            try { await repository.SetExplicitKeepAsync(oldGroup, "old-a", true, cancellationToken); }
            catch (InvalidOperationException) { olderExplicitKeepRejected = true; }
            Assert(olderSessionRejected && olderExplicitKeepRejected, "El inicio de un Full nuevo no invalidó inmediatamente la revisión anterior.");
            await sessions.UpdateStatusAsync(scope.ScanId, ScanStatus.Completed, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
            await InsertGroupFilesAsync(files, scope, "review-hash", ["review-a", "review-b", "review-c"], cancellationToken);
            await repository.RegisterCompletedInventoryAsync(scope, cancellationToken);
            var group = new DuplicateGroupIdentity(scope, 123, "review-hash");

            ReviewGroupPersistenceState initial = await repository.RegisterGroupAsync(group, cancellationToken);
            PersistedFileDecisionPage initialDecisions = await repository.GetDecisionsPageAsync(group, 0, 10, cancellationToken);
            Assert(initial.MemberCount == 3 && initial.Status == DuplicateGroupReviewStatus.Pending &&
                initialDecisions.TotalCount == 3 && initialDecisions.Items.All(item => item.Decision == DuplicateFileDecision.Undecided),
                "El registro inicial no creó una decisión Undecided por cada miembro.");

            bool fileMembershipRejected = false;
            bool explicitKeepMembershipRejected = false;
            try
            {
                await repository.SetDecisionAsync(group, "not-a-member", DuplicateFileDecision.Keep, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                fileMembershipRejected = true;
            }
            try
            {
                await repository.SetExplicitKeepAsync(group, "not-a-member", true, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                explicitKeepMembershipRejected = true;
            }
            Assert(fileMembershipRejected && explicitKeepMembershipRejected, "Se persistió una decisión para un FileId ajeno al grupo.");

            ReviewGroupPersistenceState afterKeep = (await repository.SetDecisionAsync(
                group, "review-a", DuplicateFileDecision.Keep, cancellationToken)).State;
            ReviewGroupPersistenceState afterFirstCandidate = (await repository.SetDecisionAsync(
                group, "review-b", DuplicateFileDecision.CandidateForTrash, cancellationToken)).State;
            ReviewGroupPersistenceState ready = (await repository.SetDecisionAsync(
                group, "review-c", DuplicateFileDecision.CandidateForTrash, cancellationToken)).State;
            ReviewGroupPersistenceState repeated = (await repository.SetDecisionAsync(
                group, "review-c", DuplicateFileDecision.CandidateForTrash, cancellationToken)).State;
            Assert(afterKeep.Status == DuplicateGroupReviewStatus.PartiallyReviewed &&
                afterFirstCandidate.Status == DuplicateGroupReviewStatus.PartiallyReviewed &&
                ready.Status == DuplicateGroupReviewStatus.ReadyForCleanup &&
                repeated.Status == DuplicateGroupReviewStatus.ReadyForCleanup,
                "Los estados Pending/Partial/Ready o la actualización idempotente cambiaron de significado.");

            bool keptFileCannotBecomeCandidate = false;
            try
            {
                await repository.SetDecisionAsync(group, "review-a", DuplicateFileDecision.CandidateForTrash, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                keptFileCannotBecomeCandidate = true;
            }
            bool lastKeepCannotBeCleared = false;
            try
            {
                await repository.SetDecisionAsync(group, "review-a", DuplicateFileDecision.Undecided, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                lastKeepCannotBeCleared = true;
            }
            Assert(keptFileCannotBecomeCandidate && lastKeepCannotBeCleared,
                "Se permitió convertir la copia conservada o dejar el grupo sin copia conservada.");

            var autoGroup = new DuplicateGroupIdentity(scope, 124, "auto-keep-hash");
            await InsertGroupFilesAsync(files, scope, "auto-keep-hash", ["auto-a", "auto-b", "auto-c"], cancellationToken);
            ReviewGroupPersistenceState autoInitial = await repository.RegisterGroupAsync(autoGroup, cancellationToken);
            Assert(autoInitial.Status == DuplicateGroupReviewStatus.Pending,
                "Las decisiones del grupo anterior se mezclaron con otro grupo del mismo inventario.");
            await repository.SetDecisionAsync(autoGroup, "auto-a", DuplicateFileDecision.CandidateForTrash, cancellationToken);
            bool automaticKeepRequired = false;
            try
            {
                await repository.SetDecisionAsync(autoGroup, "auto-b", DuplicateFileDecision.CandidateForTrash, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                automaticKeepRequired = true;
            }
            Assert(automaticKeepRequired, "El repositorio permitió completar candidatos sin guardar la conservación automática.");
            ReviewGroupPersistenceState autoReady = (await repository.SetDecisionAsync(
                autoGroup, "auto-b", DuplicateFileDecision.CandidateForTrash, cancellationToken,
                automaticallyKeptFileId: "auto-c")).State;
            PersistedFileDecisionPage autoDecisions = await repository.GetDecisionsPageAsync(autoGroup, 0, 10, cancellationToken);
            Assert(autoReady.Status == DuplicateGroupReviewStatus.ReadyForCleanup &&
                autoDecisions.Items.Single(item => item.FileId == "auto-c").Decision == DuplicateFileDecision.Keep,
                "No se preservó el FileId que la revisión decidió conservar automáticamente.");

            ReviewGroupPersistenceState reviewed = await repository.SetReviewedWithoutCleanupAsync(group, true, cancellationToken);
            PersistedFileDecisionPage reviewedDecisions = await repository.GetDecisionsPageAsync(group, 0, 10, cancellationToken);
            Assert(reviewed.Status == DuplicateGroupReviewStatus.ReviewedWithoutCleanup &&
                reviewedDecisions.Items.All(item => item.Decision != DuplicateFileDecision.CandidateForTrash) &&
                reviewedDecisions.Items.Single(item => item.FileId == "review-a").Decision == DuplicateFileDecision.Keep,
                "Revisado sin limpieza no preservó Keep o no limpió candidatos.");
            await repository.SetNotesAsync(group, "  nota local  ", cancellationToken);
            await repository.SetSelectedAsync(group, true, cancellationToken);
            bool selectedPageValid = await AssertSelectedPageAsync(repository, scope, group, expectedCount: 1, cancellationToken);
            await repository.SetSelectedAsync(group, false, cancellationToken);
            bool deselectedPageValid = await AssertSelectedPageAsync(repository, scope, group, expectedCount: 0, cancellationToken);

            ReviewGroupSummaryPage summaryPage = await repository.GetReviewGroupSummaryPageAsync(scope, 0, 10, cancellationToken);
            ReviewGroupSummary persistedSummary = summaryPage.Items.Single(item => item.Group.Identity.NormalizedChecksum == "REVIEW-HASH");
            Assert(persistedSummary.Status == DuplicateGroupReviewStatus.ReviewedWithoutCleanup &&
                persistedSummary.ReviewedWithoutCleanup && persistedSummary.Notes == "nota local" && !persistedSummary.IsSelected,
                "La página de resumen no recuperó el estado individual del grupo.");
            ReviewGroupSummaryPage firstSummaryPage = await repository.GetReviewGroupSummaryPageAsync(scope, 0, 1, cancellationToken);
            ReviewGroupSummaryPage secondSummaryPage = await repository.GetReviewGroupSummaryPageAsync(scope, 1, 1, cancellationToken);
            Assert(firstSummaryPage.TotalCount == 2 && secondSummaryPage.TotalCount == 2 &&
                firstSummaryPage.Items.Count == 1 && secondSummaryPage.Items.Count == 1 &&
                firstSummaryPage.Items[0].Group.Identity != secondSummaryPage.Items[0].Group.Identity,
                "Las páginas de resumen no respetaron el offset o el total de grupos.");

            var pagedGroup = new DuplicateGroupIdentity(scope, 100, "paged-group-hash");
            ScanSessionRecord? latestCompleted = await sessions.GetLatestCompletedFullAsync(scope.AccountKey, scope.ScopeKey, cancellationToken);
            bool latestInventoryIsReopenable = latestCompleted?.ScanId == scope.ScanId;
            string[] pagedFileIds = Enumerable.Range(0, 205).Select(index => $"paged-{index:D4}").ToArray();
            await InsertGroupFilesAsync(files, scope, "paged-group-hash", pagedFileIds, cancellationToken);
            ReviewGroupPersistenceState pagedInitial = await repository.RegisterGroupAsync(pagedGroup, cancellationToken);
            bool confirmWithoutKeepRejected = false;
            try { await repository.ConfirmGroupReviewAsync(pagedGroup, cancellationToken); }
            catch (InvalidOperationException) { confirmWithoutKeepRejected = true; }
            ReviewDecisionCounts pendingCounts = await repository.GetGroupDecisionCountsAsync(pagedGroup, cancellationToken);
            bool skipLeavesPending = !pagedInitial.IsReviewConfirmed && pendingCounts.KeepCount == 0 &&
                pendingCounts.UndecidedCount == 205 && pendingCounts.CandidateCount == 0;
            DuplicateGroupMembersPage membersPage1 = await files.GetDuplicateGroupMembersPageAsync(pagedGroup, 0, 100, cancellationToken);
            DuplicateGroupMembersPage membersPage2 = await files.GetDuplicateGroupMembersPageAsync(pagedGroup, 100, 100, cancellationToken);
            DuplicateGroupMembersPage membersPage3 = await files.GetDuplicateGroupMembersPageAsync(pagedGroup, 200, 100, cancellationToken);
            Assert(confirmWithoutKeepRejected && skipLeavesPending && membersPage1.Items.Count == 100 &&
                membersPage2.Items.Count == 100 && membersPage3.Items.Count == 5 &&
                membersPage1.Items.Select(file => file.FileId).Intersect(membersPage2.Items.Select(file => file.FileId), StringComparer.Ordinal).Any() == false,
                "La revisión paginada no respetó confirmación, salto sin decisión o límites de página.");
            await repository.SetExplicitKeepAsync(pagedGroup, "paged-0000", true, cancellationToken);
            await repository.SetExplicitKeepAsync(pagedGroup, "paged-0175", true, cancellationToken);
            bool confirmWithoutCandidateRejected = false;
            try { await repository.ConfirmGroupReviewAsync(pagedGroup, cancellationToken); }
            catch (InvalidOperationException) { confirmWithoutCandidateRejected = true; }
            await repository.SetExplicitDecisionAsync(pagedGroup, "paged-0204", DuplicateFileDecision.CandidateForTrash, cancellationToken);
            PersistedFileDecisionPage page1Decisions = await repository.GetDecisionsPageAsync(pagedGroup, 0, 100, cancellationToken);
            PersistedFileDecisionPage page2Decisions = await repository.GetDecisionsPageAsync(pagedGroup, 100, 100, cancellationToken);
            PersistedFileDecisionPage page3Decisions = await repository.GetDecisionsPageAsync(pagedGroup, 200, 100, cancellationToken);
            ReviewDecisionCounts multipleKeeps = await repository.GetGroupDecisionCountsAsync(pagedGroup, cancellationToken);
            ReviewGroupPersistenceState confirmedPaged = await repository.ConfirmGroupReviewAsync(pagedGroup, cancellationToken);
            ReviewGroupPersistenceState idempotentCandidate = await repository.SetExplicitDecisionAsync(
                pagedGroup, "paged-0204", DuplicateFileDecision.CandidateForTrash, cancellationToken);
            PagedCleanupPlanSummary planSummary = await repository.GetCleanupPlanSummaryAsync(scope, cancellationToken);
            PagedCleanupPlanPage planPage1 = await repository.GetCleanupPlanPageAsync(scope, 0, 1, cancellationToken);
            PagedCleanupPlanPage planPage2 = await repository.GetCleanupPlanPageAsync(scope, 1, 1, cancellationToken);
            PagedCleanupPlanPage planPage3 = await repository.GetCleanupPlanPageAsync(scope, 2, 1, cancellationToken);
            var wrongInventory = new ScanInventoryIdentity("wrong-account", scope.ScopeKey, scope.ScanId);
            bool wrongInventoryPlanRejected = false;
            try { await repository.GetCleanupPlanSummaryAsync(wrongInventory, cancellationToken); }
            catch (InvalidOperationException) { wrongInventoryPlanRejected = true; }
            bool wrongScopePlanRejected = false;
            try { await repository.GetCleanupPlanSummaryAsync(new ScanInventoryIdentity(scope.AccountKey, "wrong-scope", scope.ScanId), cancellationToken); }
            catch (InvalidOperationException) { wrongScopePlanRejected = true; }
            bool wrongSessionPlanRejected = false;
            try { await repository.GetCleanupPlanSummaryAsync(new ScanInventoryIdentity(scope.AccountKey, scope.ScopeKey, "wrong-session"), cancellationToken); }
            catch (InvalidOperationException) { wrongSessionPlanRejected = true; }
            SqliteConnection.ClearAllPools();
            var reopenedPaged = new ReviewStateRepository(new SqliteConnectionFactory(new LocalDataPathService(root)));
            ReviewGroupPersistenceState persistedPaged = await reopenedPaged.GetGroupStateAsync(pagedGroup, cancellationToken)
                ?? throw new InvalidOperationException("La revisión paginada no persistió tras reabrir SQLite.");
            await reopenedPaged.SetExplicitKeepAsync(pagedGroup, "paged-0000", false, cancellationToken);
            PersistedFileDecisionPage afterUnkeep = await reopenedPaged.GetDecisionsPageAsync(pagedGroup, 0, 100, cancellationToken);
            ReviewDecisionCounts afterUnkeepCounts = await reopenedPaged.GetGroupDecisionCountsAsync(pagedGroup, cancellationToken);
            ReviewGroupPersistenceState afterUnkeepState = await reopenedPaged.GetGroupStateAsync(pagedGroup, cancellationToken)
                ?? throw new InvalidOperationException("No se pudo recuperar el grupo después de desmarcar.");
            bool pagedReviewWorkflowVerified = latestInventoryIsReopenable && multipleKeeps.MemberCount == 205 && multipleKeeps.KeepCount == 2 &&
                multipleKeeps.UndecidedCount == 202 && multipleKeeps.CandidateCount == 1 &&
                multipleKeeps.KeepCount + multipleKeeps.CandidateCount + multipleKeeps.UndecidedCount == multipleKeeps.MemberCount &&
                page1Decisions.Items.Count == 100 && page2Decisions.Items.Count == 100 && page3Decisions.Items.Count == 5 &&
                page1Decisions.Items.Single(item => item.FileId == "paged-0000").Decision == DuplicateFileDecision.Keep &&
                page2Decisions.Items.Single(item => item.FileId == "paged-0175").Decision == DuplicateFileDecision.Keep &&
                page3Decisions.Items.Single(item => item.FileId == "paged-0204").Decision == DuplicateFileDecision.CandidateForTrash &&
                confirmedPaged.IsReviewConfirmed && persistedPaged.IsReviewConfirmed &&
                afterUnkeep.Items.Single(item => item.FileId == "paged-0000").Decision == DuplicateFileDecision.Undecided &&
                afterUnkeep.TotalCount == 205 && afterUnkeep.Items.Count == 100 && afterUnkeepCounts.KeepCount == 1 &&
                !afterUnkeepState.IsReviewConfirmed;
            Assert(pagedReviewWorkflowVerified, "No se conservaron decisiones individuales/paginadas o la confirmación permitió clasificar miembros ocultos.");
            bool pagedCleanupPlanVerified = confirmWithoutCandidateRejected && idempotentCandidate.IsReviewConfirmed && planSummary.EligibleGroupCount == 1 && planSummary.CandidateFileCount == 1 &&
                planSummary.FileEntryCount == 3 && planSummary.PotentiallyRecoverableBytes == 100 &&
                planPage1.Items.Count == 1 && planPage1.Items[0].Decision == DuplicateFileDecision.CandidateForTrash &&
                planPage1.Items[0].File.FileId == "paged-0204" && planPage2.Items[0].Decision == DuplicateFileDecision.Keep &&
                planPage3.Items[0].Decision == DuplicateFileDecision.Keep && wrongInventoryPlanRejected && wrongScopePlanRejected && wrongSessionPlanRejected;
            Assert(pagedCleanupPlanVerified, "La vista previa SQLite no contó/paginó el plan explícito o mezcló inventarios.");
            await reopenedPaged.SetExplicitDecisionAsync(pagedGroup, "paged-0204", DuplicateFileDecision.Undecided, cancellationToken);
            ReviewGroupPersistenceState editedAfterConfirmation = await reopenedPaged.GetGroupStateAsync(pagedGroup, cancellationToken)
                ?? throw new InvalidOperationException("No se pudo recuperar el grupo tras editar una decisión confirmada.");
            PagedCleanupPlanSummary invalidatedPlan = await reopenedPaged.GetCleanupPlanSummaryAsync(scope, cancellationToken);
            bool editInvalidatedConfirmation = !editedAfterConfirmation.IsReviewConfirmed && invalidatedPlan.EligibleGroupCount == 0;
            Assert(editInvalidatedConfirmation, "Una edición posterior no invalidó la confirmación operativa del grupo.");
            pagedCleanupPlanVerified &= editInvalidatedConfirmation;
            await reopenedPaged.SetExplicitDecisionAsync(pagedGroup, "paged-0204", DuplicateFileDecision.CandidateForTrash, cancellationToken);
            await reopenedPaged.ConfirmGroupReviewAsync(pagedGroup, cancellationToken);

            var otherAccount = new ScanInventoryIdentity("other-review-account", scope.ScopeKey, "review-other-account");
            await CreateCompletedScanAsync(sessions, otherAccount, DateTimeOffset.UtcNow.AddMinutes(-1), cancellationToken);
            await InsertGroupFilesAsync(files, otherAccount, "review-hash", ["other-account-a", "other-account-b"], cancellationToken);
            await repository.RegisterCompletedInventoryAsync(otherAccount, cancellationToken);
            var otherAccountGroup = new DuplicateGroupIdentity(otherAccount, 123, "review-hash");
            ReviewGroupPersistenceState accountIsolated = await repository.RegisterGroupAsync(otherAccountGroup, cancellationToken);
            Assert(accountIsolated.Status == DuplicateGroupReviewStatus.Pending,
                "La revisión de otra cuenta heredó las decisiones existentes.");
            await repository.SetDecisionAsync(otherAccountGroup, "other-account-a", DuplicateFileDecision.Keep, cancellationToken);
            await repository.SetSelectedAsync(otherAccountGroup, true, cancellationToken);

            var otherScope = new ScanInventoryIdentity(scope.AccountKey, "other-review-scope", "review-other-scope");
            await CreateCompletedScanAsync(sessions, otherScope, DateTimeOffset.UtcNow.AddMinutes(-1), cancellationToken);
            await InsertGroupFilesAsync(files, otherScope, "review-hash", ["other-scope-a", "other-scope-b"], cancellationToken);
            await repository.RegisterCompletedInventoryAsync(otherScope, cancellationToken);
            ReviewGroupPersistenceState scopeIsolated = await repository.RegisterGroupAsync(new DuplicateGroupIdentity(otherScope, 123, "review-hash"), cancellationToken);
            Assert(scopeIsolated.Status == DuplicateGroupReviewStatus.Pending,
                "La revisión de otro alcance heredó las decisiones existentes.");

            var incomplete = new ScanInventoryIdentity(scope.AccountKey, scope.ScopeKey, "review-incomplete");
            await CreateIncompleteScanAsync(sessions, incomplete, cancellationToken);
            await InsertGroupFilesAsync(files, incomplete, "review-hash", ["incomplete-a", "incomplete-b"], cancellationToken);
            bool incompleteRejected = false;
            try
            {
                await repository.RegisterCompletedInventoryAsync(incomplete, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                incompleteRejected = true;
            }
            bool incompleteGroupRejected = false;
            try
            {
                await repository.RegisterGroupAsync(new DuplicateGroupIdentity(incomplete, 123, "review-hash"), cancellationToken);
            }
            catch (InvalidOperationException)
            {
                incompleteGroupRejected = true;
            }
            bool incompletePlanRejected = false;
            try { await repository.GetCleanupPlanSummaryAsync(scope, cancellationToken); }
            catch (InvalidOperationException) { incompletePlanRejected = true; }
            Assert(incompleteRejected && incompleteGroupRejected && incompletePlanRejected,
                "Se registraron decisiones o grupos para una sesión incompleta.");

            await repository.InvalidateInventoryAsync(scope, cancellationToken);
            bool obsoleteWriteRejected = false;
            try
            {
                await repository.SetSelectedAsync(group, true, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                obsoleteWriteRejected = true;
            }
            bool obsoletePlanRejected = false;
            try { await repository.GetCleanupPlanSummaryAsync(scope, cancellationToken); }
            catch (InvalidOperationException) { obsoletePlanRejected = true; }
            Assert(obsoleteWriteRejected && obsoletePlanRejected, "Se modificó selección o se mostró un plan para un inventario invalidado.");

            var reopenedFactory = new SqliteConnectionFactory(new LocalDataPathService(root));
            var reopened = new ReviewStateRepository(reopenedFactory);
            ReviewGroupPersistenceState reopenedAccountState = await reopened.GetGroupStateAsync(
                otherAccountGroup, cancellationToken)
                ?? throw new InvalidOperationException("El estado no sobrevivió al cierre y reapertura de SQLite.");
            PersistedFileDecisionPage reopenedDecisions = await reopened.GetDecisionsPageAsync(otherAccountGroup, 0, 10, cancellationToken);
            SelectedReviewGroupPage reopenedSelection = await reopened.GetSelectedGroupsPageAsync(otherAccount, 0, 10, cancellationToken);
            bool reopenedDatabasePreservedState = reopenedAccountState.Status == DuplicateGroupReviewStatus.PartiallyReviewed &&
                reopenedDecisions.Items.Single(item => item.FileId == "other-account-a").Decision == DuplicateFileDecision.Keep &&
                reopenedAccountState.IsSelected && reopenedSelection.Items.Single() == otherAccountGroup &&
                selectedPageValid && deselectedPageValid && legacyHash == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(legacyJsonPath, cancellationToken)));
            Assert(reopenedDatabasePreservedState, "La selección/estado no sobrevivió al reopen o el JSON heredado cambió.");

            result = new ReviewStatePersistenceCheckResult(
                root, false, migrationsOneThroughFourAppliedIdempotently, pagedReviewWorkflowVerified,
                pagedCleanupPlanVerified,
                ready.Status == DuplicateGroupReviewStatus.ReadyForCleanup && reviewed.Status == DuplicateGroupReviewStatus.ReviewedWithoutCleanup,
                selectedPageValid && deselectedPageValid,
                reopenedDatabasePreservedState,
                accountIsolated.Status == DuplicateGroupReviewStatus.Pending && scopeIsolated.Status == DuplicateGroupReviewStatus.Pending &&
                    autoInitial.Status == DuplicateGroupReviewStatus.Pending && olderSessionRejected && olderExplicitKeepRejected,
                incompleteRejected && incompletePlanRejected && obsoleteWriteRejected && obsoletePlanRejected,
                fileMembershipRejected && explicitKeepMembershipRejected && keptFileCannotBecomeCandidate && lastKeepCannotBeCleared && automaticKeepRequired,
                legacyHash == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(legacyJsonPath, cancellationToken))));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        return result is null
            ? throw new InvalidOperationException("Las comprobaciones de persistencia de revisión no produjeron resultado.")
            : result with { TemporaryDirectoryDeleted = !Directory.Exists(root) };
    }

    private static async Task<bool> AssertSelectedPageAsync(
        ReviewStateRepository repository,
        ScanInventoryIdentity inventory,
        DuplicateGroupIdentity group,
        long expectedCount,
        CancellationToken cancellationToken)
    {
        SelectedReviewGroupPage page = await repository.GetSelectedGroupsPageAsync(inventory, 0, 10, cancellationToken);
        return page.TotalCount == expectedCount && page.Items.Count == expectedCount &&
            (expectedCount == 0 || page.Items.Single() == group);
    }

    private static async Task CreateCompletedScanAsync(
        ScanSessionRepository sessions,
        ScanInventoryIdentity inventory,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset updated = startedAt;
        await sessions.CreateAsync(new ScanSessionRecord
        {
            ScanId = inventory.ScanId,
            AccountKey = inventory.AccountKey,
            ScopeKey = inventory.ScopeKey,
            RootFolderId = "root",
            ScanType = ScanType.Full,
            Status = ScanStatus.Running,
            StartedAtUtc = startedAt,
            UpdatedAtUtc = updated
        }, cancellationToken);
        await sessions.UpdateStatusAsync(inventory.ScanId, ScanStatus.Completed, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
    }

    private static Task CreateIncompleteScanAsync(
        ScanSessionRepository sessions,
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return sessions.CreateAsync(new ScanSessionRecord
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
    }

    private static async Task InsertGroupFilesAsync(
        DriveFileCacheRepository files,
        ScanInventoryIdentity inventory,
        string checksum,
        IReadOnlyList<string> fileIds,
        CancellationToken cancellationToken)
    {
        foreach (string fileId in fileIds)
        {
            await files.UpsertAsync(new DriveFileCacheRecord
            {
                FileId = fileId,
                Name = $"{fileId}.pdf",
                AccountKey = inventory.AccountKey,
                ScopeKey = inventory.ScopeKey,
                MimeType = "application/pdf",
                SizeBytes = checksum == "review-hash" ? 123 : checksum == "auto-keep-hash" ? 124 : 100,
                Md5Checksum = checksum,
                LastSeenScanId = inventory.ScanId,
                CachedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<int>> ReadMigrationVersionsAsync(
        SqliteConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await factory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM SchemaMigrations ORDER BY Version;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
