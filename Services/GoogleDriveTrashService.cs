using DriveDuplicateFinder.Models;
using Google;
using Google.Apis.Drive.v3;
using GoogleFile = Google.Apis.Drive.v3.Data.File;
using System.Text.RegularExpressions;

namespace DriveDuplicateFinder.Services;

public sealed class GoogleDriveTrashService
{
    private const int MaximumCandidateCount = 10;
    private const string PreflightFields =
        "id,name,mimeType,size,md5Checksum,modifiedTime,parents,trashed,explicitlyTrashed,ownedByMe,shared,starred,driveId,version,capabilities(canTrash)";
    private const string PostUpdateFailureFields = "id,trashed,explicitlyTrashed,version,modifiedTime";

    public CleanupPlanValidationResult ValidateLocalPlan(DuplicateGroupReview review, bool resultsAreObsolete)
    {
        ArgumentNullException.ThrowIfNull(review);
        var result = new CleanupPlanValidationResult();

        if (resultsAreObsolete)
        {
            result.Reasons.Add("Los resultados están obsoletos. Debes repetir la búsqueda antes de limpiar.");
        }

        if (review.Status != DuplicateGroupReviewStatus.ReadyForCleanup)
        {
            result.Reasons.Add("El grupo no está listo para limpieza.");
        }

        int keepCount = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.Keep);
        int candidateCount = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.CandidateForTrash);
        int undecidedCount = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.Undecided);
        if (keepCount != 1)
        {
            result.Reasons.Add("El grupo debe tener exactamente un archivo marcado como Conservar.");
        }

        if (candidateCount == 0)
        {
            result.Reasons.Add("El grupo debe tener al menos un candidato a papelera.");
        }

        if (candidateCount > MaximumCandidateCount)
        {
            result.Reasons.Add($"Esta fase permite como máximo {MaximumCandidateCount} candidatos por grupo.");
        }

        if (undecidedCount != 0)
        {
            result.Reasons.Add("No puede haber archivos sin decidir en el grupo.");
        }

        if (candidateCount >= review.Group.Files.Count)
        {
            result.Reasons.Add("El grupo debe conservar al menos una copia.");
        }

        foreach (DriveFileInfo file in review.Group.Files)
        {
            ValidateFileForLocalPlan(file, review.Group, result.Reasons);
        }

        return result;
    }

    public CleanupEligibilityResult EvaluateEligibility(
        DuplicateGroupReview? selectedReview,
        CleanupEligibilityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reasons = new List<string>();

        if (!context.CleanupModeActive)
        {
            reasons.Add("El modo limpieza no está activo.");
        }

        if (!context.SearchCompleted)
        {
            reasons.Add("No hay una búsqueda de duplicados completada.");
        }

        if (context.ResultsAreObsolete)
        {
            reasons.Add("Los resultados están obsoletos. Debes repetir la búsqueda.");
        }

        if (context.OperationInProgress)
        {
            reasons.Add("Hay una operación de limpieza en curso.");
        }

        if (selectedReview is null)
        {
            reasons.Add("No hay un grupo seleccionado.");
        }
        else
        {
            reasons.AddRange(ValidateLocalPlan(selectedReview, resultsAreObsolete: false).Reasons);
        }

        return new CleanupEligibilityResult
        {
            IsAllowed = reasons.Count == 0,
            BlockingReasons = reasons
        };
    }

    public static CleanupEligibilityResult ValidateSimulatedSafeGroup()
    {
        const string md5 = "0123456789abcdef0123456789abcdef";
        var first = new DriveFileInfo
        {
            Id = "keep-file",
            Name = "conservar.txt",
            MimeType = "text/plain",
            Size = 1024,
            Md5Checksum = md5,
            Path = "Mi unidad/Prueba/conservar.txt",
            OwnedByMe = true,
            IsShared = false,
            IsStarred = false,
            CanTrash = true
        };
        var second = new DriveFileInfo
        {
            Id = "candidate-file",
            Name = "candidato.txt",
            MimeType = "text/plain",
            Size = 1024,
            Md5Checksum = md5,
            Path = "Mi unidad/Prueba/candidato.txt",
            OwnedByMe = true,
            IsShared = false,
            IsStarred = false,
            CanTrash = true
        };
        var group = new DuplicateGroup
        {
            GroupNumber = 1,
            FileSize = 1024,
            Md5Checksum = md5,
            Files = [first, second]
        };
        var review = new DuplicateGroupReview
        {
            StableId = DuplicateReviewService.CreateStableGroupId(group.FileSize, group.Md5Checksum),
            Group = group,
            Status = DuplicateGroupReviewStatus.ReadyForCleanup
        };
        review.DecisionsByFileId[first.Id] = DuplicateFileDecision.Keep;
        review.DecisionsByFileId[second.Id] = DuplicateFileDecision.CandidateForTrash;

        return new GoogleDriveTrashService().EvaluateEligibility(review, new CleanupEligibilityContext(
            CleanupModeActive: true,
            SearchCompleted: true,
            ResultsAreObsolete: false,
            OperationInProgress: false));
    }

    public async Task<CleanupPreflightResult> PreflightAsync(
        DriveService cleanupDriveService,
        DuplicateGroupReview review,
        bool resultsAreObsolete,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleanupDriveService);
        ArgumentNullException.ThrowIfNull(review);

        var result = new CleanupPreflightResult { Review = review };
        CleanupPlanValidationResult localValidation = ValidateLocalPlan(review, resultsAreObsolete);
        result.ValidationMessages.AddRange(localValidation.Reasons);
        if (!localValidation.IsAllowed)
        {
            return result;
        }

        DriveFileInfo keepFile = review.Group.Files.Single(file =>
            review.DecisionsByFileId[file.Id] == DuplicateFileDecision.Keep);
        DriveFileInfo[] candidates = review.Group.Files.Where(file =>
            review.DecisionsByFileId[file.Id] == DuplicateFileDecision.CandidateForTrash).ToArray();

        try
        {
            progress?.Report($"Verificando archivo que se conservará: {keepFile.Name}");
            CleanupFileSnapshot keepSnapshot = await GetCurrentSnapshotAsync(cleanupDriveService, keepFile, cancellationToken);
            result.KeepFile = keepSnapshot;
            result.ValidationMessages.AddRange(ValidateKeepSnapshot(keepFile, review.Group, keepSnapshot));

            foreach (DriveFileInfo candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Verificando candidato: {candidate.Name}");
                CleanupFileSnapshot candidateSnapshot = await GetCurrentSnapshotAsync(cleanupDriveService, candidate, cancellationToken);
                result.CandidateFiles.Add(candidateSnapshot);
                result.ValidationMessages.AddRange(ValidateCandidateSnapshot(candidate, review.Group, candidateSnapshot));
            }
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
            result.ValidationMessages.Add("El preflight fue cancelado antes de modificar Google Drive.");
            return result;
        }
        catch (Exception)
        {
            result.ValidationMessages.Add("No se pudo completar la verificación previa. Repite la búsqueda y vuelve a intentarlo.");
            return result;
        }

        result.IsSuccessful = result.ValidationMessages.Count == 0;
        return result;
    }

    public async Task<CleanupOperationResult> ExecuteAsync(
        DriveService cleanupDriveService,
        CleanupPreflightResult preflight,
        CleanupHistoryService historyService,
        CleanupHistoryHandle history,
        IProgress<(int Current, int Total, string Name)>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? stopAfterCurrentFileRequested = null)
    {
        ArgumentNullException.ThrowIfNull(cleanupDriveService);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(history);

        CleanupOperationRecord record = history.Record;
        if (!preflight.IsSuccessful || preflight.KeepFile is null || preflight.CandidateFiles.Count == 0)
        {
            record.Status = CleanupOperationStatus.PreflightFailed;
            record.FinishedAtUtc = DateTimeOffset.UtcNow;
            record.SanitizedErrorMessage = "El preflight no permite enviar este grupo a la papelera.";
            await historyService.SaveAsync(history, cancellationToken);
            return new CleanupOperationResult { Record = record, WriteAttempted = false };
        }

        record.KeepFile = preflight.KeepFile;
        record.CandidateFiles.Clear();
        record.CandidateFiles.AddRange(preflight.CandidateFiles);
        record.Validations.Clear();
        record.Validations.Add("Preflight completado: el archivo conservado y todos los candidatos coincidían con el escaneo.");
        await historyService.SaveAsync(history, cancellationToken);

        bool writeAttempted = false;
        for (int index = 0; index < preflight.CandidateFiles.Count; index++)
        {
            if (stopAfterCurrentFileRequested?.Invoke() == true)
            {
                MarkRemainingAsNotProcessed(record, preflight.CandidateFiles, index);
                record.Status = record.ProcessedFiles > 0 ? CleanupOperationStatus.Partial : CleanupOperationStatus.Cancelled;
                record.FinishedAtUtc = DateTimeOffset.UtcNow;
                record.SanitizedErrorMessage = "El usuario solicitó detener el proceso antes de enviar el siguiente archivo a la papelera.";
                await historyService.SaveAsync(history, CancellationToken.None);
                return new CleanupOperationResult
                {
                    Record = record,
                    WriteAttempted = writeAttempted,
                    ShouldInvalidateScan = record.ProcessedFiles > 0,
                    NoFilesChanged = record.ProcessedFiles == 0
                };
            }

            CleanupFileSnapshot candidate = preflight.CandidateFiles[index];
            var fileResult = new CleanupFileResult { File = candidate };
            record.FileResults.Add(fileResult);
            progress?.Report((index + 1, preflight.CandidateFiles.Count, candidate.Name));
            string failureStage = "Files.Update";

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                writeAttempted = true;
                var metadata = new GoogleFile { Trashed = true };
                FilesResource.UpdateRequest updateRequest = cleanupDriveService.Files.Update(metadata, candidate.Id);
                updateRequest.SupportsAllDrives = true;
                updateRequest.Fields = "id,name,trashed,modifiedTime";
                GoogleFile updateResponse = await updateRequest.ExecuteAsync(cancellationToken);
                if (updateResponse.Trashed != true)
                {
                    fileResult.Status = CleanupFileStatus.Failed;
                    fileResult.FailureStage = "Files.Update";
                    fileResult.Message = "Google Drive no confirmó el envío a la papelera.";
                    fileResult.SanitizedTechnicalMessage = fileResult.Message;
                    await FinalizeAfterFailureAsync(record, historyService, history, cancellationToken);
                    return new CleanupOperationResult
                    {
                        Record = record,
                        WriteAttempted = writeAttempted,
                        ShouldInvalidateScan = true,
                        NoFilesChanged = false
                    };
                }

                failureStage = "VerificationGet";
                CleanupFileSnapshot verification = await GetCurrentSnapshotAsync(cleanupDriveService, candidate, cancellationToken);
                if (verification.Trashed != true)
                {
                    fileResult.Status = CleanupFileStatus.Failed;
                    fileResult.Message = "La verificación posterior no confirmó que el archivo esté en la papelera.";
                    await FinalizeAfterFailureAsync(record, historyService, history, cancellationToken);
                    return new CleanupOperationResult { Record = record, WriteAttempted = writeAttempted };
                }

                fileResult.Status = CleanupFileStatus.Trashed;
                fileResult.VerificationSucceeded = true;
                fileResult.Message = "Enviado a la papelera y verificado.";
                record.ProcessedFiles++;
                record.BytesSentToTrash += candidate.Size ?? 0;
                failureStage = "HistoryWrite";
                await historyService.SaveAsync(history, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                bool trashedAfterCancellation = await VerifyTrashedAfterCancellationAsync(cleanupDriveService, candidate);
                fileResult.Status = trashedAfterCancellation ? CleanupFileStatus.Trashed : CleanupFileStatus.Cancelled;
                fileResult.VerificationSucceeded = trashedAfterCancellation;
                fileResult.Message = trashedAfterCancellation
                    ? "La cancelación llegó después de que Google Drive enviara el archivo a la papelera."
                    : "Operación cancelada antes de confirmar el envío a la papelera.";
                if (trashedAfterCancellation)
                {
                    record.ProcessedFiles++;
                    record.BytesSentToTrash += candidate.Size ?? 0;
                }

                MarkRemainingAsNotProcessed(record, preflight.CandidateFiles, index + 1);
                record.Status = record.ProcessedFiles > 0 ? CleanupOperationStatus.Partial : CleanupOperationStatus.Cancelled;
                record.FinishedAtUtc = DateTimeOffset.UtcNow;
                record.SanitizedErrorMessage = "La operación fue cancelada. Revisa la papelera y repite el análisis.";
                await historyService.SaveAsync(history, CancellationToken.None);
                return new CleanupOperationResult
                {
                    Record = record,
                    WriteAttempted = writeAttempted,
                    ShouldInvalidateScan = record.ProcessedFiles > 0,
                    NoFilesChanged = record.ProcessedFiles == 0
                };
            }
            catch (GoogleApiException exception) when (failureStage == "Files.Update")
            {
                PopulateGoogleApiFailure(fileResult, failureStage, exception);
                return await HandleFailedUpdateAsync(
                    cleanupDriveService,
                    candidate,
                    fileResult,
                    record,
                    preflight.CandidateFiles,
                    index,
                    historyService,
                    history,
                    writeAttempted);
            }
            catch (Exception exception) when (failureStage == "Files.Update")
            {
                PopulateExceptionFailure(fileResult, failureStage, "No se pudo enviar este archivo a la papelera.", exception);
                return await HandleFailedUpdateAsync(
                    cleanupDriveService,
                    candidate,
                    fileResult,
                    record,
                    preflight.CandidateFiles,
                    index,
                    historyService,
                    history,
                    writeAttempted);
            }
            catch (Exception exception)
            {
                if (failureStage == "HistoryWrite" && fileResult.Status == CleanupFileStatus.Trashed)
                {
                    fileResult.FailureStage = failureStage;
                    fileResult.FailureReason = exception.GetType().Name;
                    fileResult.SanitizedTechnicalMessage = $"No se pudo actualizar el historial tras enviar el archivo a la papelera. Tipo: {exception.GetType().Name}. Mensaje: {SanitizeMessage(exception.Message)}";
                }
                else
                {
                    PopulateExceptionFailure(fileResult, failureStage, "No se pudo verificar el archivo después de Files.Update.", exception);
                }

                await FinalizeAfterFailureAsync(record, historyService, history, cancellationToken);
                return new CleanupOperationResult
                {
                    Record = record,
                    WriteAttempted = writeAttempted,
                    ShouldInvalidateScan = true,
                    NoFilesChanged = false
                };
            }
        }

        record.Status = CleanupOperationStatus.Completed;
        record.FinishedAtUtc = DateTimeOffset.UtcNow;
        await historyService.SaveAsync(history, cancellationToken);
        return new CleanupOperationResult
        {
            Record = record,
            WriteAttempted = writeAttempted,
            ShouldInvalidateScan = record.ProcessedFiles > 0,
            NoFilesChanged = false
        };
    }

    private static async Task<CleanupOperationResult> HandleFailedUpdateAsync(
        DriveService driveService,
        CleanupFileSnapshot candidate,
        CleanupFileResult fileResult,
        CleanupOperationRecord record,
        IReadOnlyList<CleanupFileSnapshot> candidates,
        int index,
        CleanupHistoryService historyService,
        CleanupHistoryHandle history,
        bool writeAttempted)
    {
        bool? isTrashed = await GetTrashedStateAfterUpdateFailureAsync(driveService, candidate.Id);
        if (isTrashed == true)
        {
            fileResult.Status = CleanupFileStatus.Trashed;
            fileResult.VerificationSucceeded = true;
            fileResult.Message = $"{fileResult.Message} Files.Get confirmó que el archivo quedó en la papelera.";
            record.ProcessedFiles++;
            record.BytesSentToTrash += candidate.Size ?? 0;
            MarkRemainingAsNotProcessed(record, candidates, index + 1);
            record.Status = CleanupOperationStatus.Partial;
            record.FinishedAtUtc = DateTimeOffset.UtcNow;
            record.SanitizedErrorMessage = "Files.Update devolvió un error, pero se confirmó una modificación. Debes repetir el análisis.";
            await historyService.SaveAsync(history, CancellationToken.None);
            return new CleanupOperationResult
            {
                Record = record,
                WriteAttempted = writeAttempted,
                ShouldInvalidateScan = true,
                NoFilesChanged = false
            };
        }

        fileResult.Status = CleanupFileStatus.Failed;
        if (isTrashed is null)
        {
            fileResult.Message = $"{fileResult.Message} No se pudo verificar si el archivo cambió; revisa manualmente la papelera.";
            await FinalizeAfterFailureAsync(record, historyService, history, CancellationToken.None);
            return new CleanupOperationResult
            {
                Record = record,
                WriteAttempted = writeAttempted,
                ShouldInvalidateScan = true,
                NoFilesChanged = false
            };
        }

        fileResult.Message = $"{fileResult.Message} Files.Get confirmó que el archivo sigue fuera de la papelera.";
        await FinalizeAfterFailureAsync(record, historyService, history, CancellationToken.None);
        return new CleanupOperationResult
        {
            Record = record,
            WriteAttempted = writeAttempted,
            ShouldInvalidateScan = false,
            NoFilesChanged = true
        };
    }

    private static async Task<bool?> GetTrashedStateAfterUpdateFailureAsync(DriveService driveService, string fileId)
    {
        try
        {
            FilesResource.GetRequest request = driveService.Files.Get(fileId);
            request.SupportsAllDrives = true;
            request.Fields = PostUpdateFailureFields;
            GoogleFile file = await request.ExecuteAsync(CancellationToken.None);
            return file.Trashed;
        }
        catch
        {
            return null;
        }
    }

    private static void PopulateGoogleApiFailure(
        CleanupFileResult fileResult,
        string stage,
        GoogleApiException exception)
    {
        fileResult.Status = CleanupFileStatus.Failed;
        fileResult.FailureStage = stage;
        fileResult.HttpStatusCode = (int)exception.HttpStatusCode;
        fileResult.HttpStatusDescription = exception.HttpStatusCode.ToString();
        fileResult.GoogleErrorCode = exception.Error?.Code;
        fileResult.FailureReason = exception.Error?.Errors?.FirstOrDefault()?.Reason;
        string message = SanitizeMessage(exception.Error?.Message ?? exception.Message);
        fileResult.SanitizedTechnicalMessage = $"{stage} falló. HTTP {fileResult.HttpStatusCode}. Motivo: {fileResult.FailureReason ?? "no disponible"}. Mensaje: {message}";
        fileResult.Message = fileResult.SanitizedTechnicalMessage;

        foreach (var error in exception.Error?.Errors ?? [])
        {
            fileResult.ApiErrors.Add(new CleanupApiErrorDetail
            {
                Reason = error.Reason,
                Domain = error.Domain,
                Location = error.Location,
                LocationType = error.LocationType,
                Message = SanitizeMessage(error.Message)
            });
        }
    }

    private static void PopulateExceptionFailure(
        CleanupFileResult fileResult,
        string stage,
        string summary,
        Exception exception)
    {
        fileResult.Status = CleanupFileStatus.Failed;
        fileResult.FailureStage = stage;
        fileResult.FailureReason = exception.GetType().Name;
        fileResult.SanitizedTechnicalMessage = $"{summary} Tipo: {exception.GetType().Name}. Mensaje: {SanitizeMessage(exception.Message)}";
        fileResult.Message = fileResult.SanitizedTechnicalMessage;
    }

    private static string SanitizeMessage(string? message)
    {
        string sanitized = string.IsNullOrWhiteSpace(message) ? "No disponible." : message.Trim();
        sanitized = Regex.Replace(sanitized, "(?i)(bearer\\s+|access_token=|refresh_token=|client_secret=)[^\\s&]+", "$1[redactado]");
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }

    private static void ValidateFileForLocalPlan(
        DriveFileInfo file,
        DuplicateGroup group,
        ICollection<string> reasons)
    {
        if (!string.IsNullOrWhiteSpace(file.DriveId) || !string.IsNullOrWhiteSpace(file.SharedDriveId))
        {
            reasons.Add($"{file.Name}: pertenece a una unidad compartida.");
        }

        if (!file.Path.StartsWith("Mi unidad/", StringComparison.Ordinal))
        {
            reasons.Add($"{file.Name}: la ruta no está completamente resuelta en Mi unidad.");
        }

        if (file.OwnedByMe != true)
        {
            reasons.Add($"{file.Name}: no está confirmado como propiedad de la cuenta.");
        }

        if (file.IsShared == true)
        {
            reasons.Add($"{file.Name}: el archivo está compartido.");
        }

        if (file.IsStarred == true)
        {
            reasons.Add($"{file.Name}: el archivo está destacado.");
        }

        if (file.CanTrash != true)
        {
            reasons.Add($"{file.Name}: Google Drive no permite enviarlo a la papelera.");
        }

        if (file.Size < 0 || string.IsNullOrWhiteSpace(file.Md5Checksum) ||
            file.Size != group.FileSize ||
            !string.Equals(file.Md5Checksum, group.Md5Checksum, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"{file.Name}: no mantiene los metadatos de duplicado esperados.");
        }
    }

    private static IEnumerable<string> ValidateKeepSnapshot(
        DriveFileInfo expected,
        DuplicateGroup group,
        CleanupFileSnapshot actual)
    {
        foreach (string message in CompareObservedMetadata(expected, group, actual))
        {
            yield return $"Archivo conservado {expected.Name}: {message}";
        }

        if (actual.Trashed == true)
        {
            yield return $"Archivo conservado {expected.Name}: ya está en la papelera.";
        }
    }

    private static IEnumerable<string> ValidateCandidateSnapshot(
        DriveFileInfo expected,
        DuplicateGroup group,
        CleanupFileSnapshot actual)
    {
        foreach (string message in CompareObservedMetadata(expected, group, actual))
        {
            yield return $"Candidato {expected.Name}: {message}";
        }

        if (actual.Trashed == true)
        {
            yield return $"Candidato {expected.Name}: ya está en la papelera.";
        }

        if (actual.OwnedByMe != true || actual.IsShared == true || actual.IsStarred == true ||
            !string.IsNullOrWhiteSpace(actual.DriveId) || actual.CanTrash != true)
        {
            yield return $"Candidato {expected.Name}: ya no cumple las condiciones de seguridad para enviarlo a la papelera.";
        }
    }

    private static IEnumerable<string> CompareObservedMetadata(
        DriveFileInfo expected,
        DuplicateGroup group,
        CleanupFileSnapshot actual)
    {
        if (!string.Equals(expected.Id, actual.Id, StringComparison.Ordinal)) yield return "el ID cambió.";
        if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal)) yield return "el nombre cambió.";
        if (expected.Size != actual.Size) yield return "el tamaño cambió.";
        if (!string.Equals(expected.Md5Checksum, actual.Md5Checksum, StringComparison.OrdinalIgnoreCase)) yield return "el MD5 cambió.";
        if (!SameParents(expected.ParentIds, actual.ParentIds)) yield return "los padres cambiaron.";
        if (expected.ModifiedTime != actual.ModifiedTime) yield return "la fecha de modificación cambió.";
        if (expected.Version is not null && expected.Version != actual.Version) yield return "la versión cambió.";
        if (actual.Size != group.FileSize || !string.Equals(actual.Md5Checksum, group.Md5Checksum, StringComparison.OrdinalIgnoreCase))
        {
            yield return "ya no pertenece al grupo de duplicados esperado.";
        }
    }

    private static bool SameParents(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
        first.Count == second.Count &&
        new HashSet<string>(first, StringComparer.Ordinal).SetEquals(second);

    private static async Task<CleanupFileSnapshot> GetCurrentSnapshotAsync(
        DriveService driveService,
        DriveFileInfo observed,
        CancellationToken cancellationToken)
    {
        FilesResource.GetRequest request = driveService.Files.Get(observed.Id);
        request.SupportsAllDrives = true;
        request.Fields = PreflightFields;
        GoogleFile file = await request.ExecuteAsync(cancellationToken);
        return new CleanupFileSnapshot
        {
            Id = file.Id ?? observed.Id,
            Name = file.Name ?? string.Empty,
            MimeType = file.MimeType ?? string.Empty,
            Size = file.Size,
            Md5Checksum = file.Md5Checksum,
            ModifiedTime = file.ModifiedTimeDateTimeOffset,
            ParentIds = file.Parents?.ToArray() ?? [],
            Trashed = file.Trashed,
            ExplicitlyTrashed = file.ExplicitlyTrashed,
            OwnedByMe = file.OwnedByMe,
            IsShared = file.Shared,
            IsStarred = file.Starred,
            DriveId = file.DriveId,
            Version = file.Version,
            CanTrash = file.Capabilities?.CanTrash,
            Path = observed.Path
        };
    }

    private static Task<CleanupFileSnapshot> GetCurrentSnapshotAsync(
        DriveService driveService,
        CleanupFileSnapshot observed,
        CancellationToken cancellationToken)
    {
        return GetCurrentSnapshotAsync(driveService, new DriveFileInfo
        {
            Id = observed.Id,
            Name = observed.Name,
            MimeType = observed.MimeType,
            Size = observed.Size ?? -1,
            Md5Checksum = observed.Md5Checksum ?? string.Empty,
            ParentIds = observed.ParentIds,
            Path = observed.Path
        }, cancellationToken);
    }

    private static async Task<bool> VerifyTrashedAfterCancellationAsync(DriveService driveService, CleanupFileSnapshot candidate)
    {
        try
        {
            CleanupFileSnapshot verification = await GetCurrentSnapshotAsync(driveService, candidate, CancellationToken.None);
            return verification.Trashed == true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task FinalizeAfterFailureAsync(
        CleanupOperationRecord record,
        CleanupHistoryService historyService,
        CleanupHistoryHandle history,
        CancellationToken cancellationToken)
    {
        int completed = record.FileResults.Count(result => result.Status == CleanupFileStatus.Trashed);
        MarkRemainingAsNotProcessed(record, record.CandidateFiles, record.FileResults.Count);
        record.Status = completed > 0 ? CleanupOperationStatus.Partial : CleanupOperationStatus.Failed;
        record.FinishedAtUtc = DateTimeOffset.UtcNow;
        record.SanitizedErrorMessage = completed > 0
            ? "La operación quedó incompleta. Revisa la papelera y repite el análisis."
            : "La operación falló antes de completar el primer envío a la papelera.";
        await historyService.SaveAsync(history, cancellationToken);
    }

    private static void MarkRemainingAsNotProcessed(
        CleanupOperationRecord record,
        IReadOnlyList<CleanupFileSnapshot> candidates,
        int startIndex)
    {
        for (int index = startIndex; index < candidates.Count; index++)
        {
            record.FileResults.Add(new CleanupFileResult
            {
                File = candidates[index],
                Status = CleanupFileStatus.NotProcessed,
                Message = "No se procesó porque la operación se detuvo."
            });
        }
    }
}
