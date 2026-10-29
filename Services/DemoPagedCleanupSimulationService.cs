using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Runs the paged cleanup workflow only against an in-memory HTTP handler, and only for
/// the isolated synthetic demo inventory. No OAuth credential or live Drive client is accepted.
/// </summary>
public sealed class DemoPagedCleanupSimulationService
{
    private readonly DemoReviewSeed _seed;
    private readonly PagedGroupPreflightService _preflight;
    private readonly PagedGroupCleanupService _cleanup;

    public DemoPagedCleanupSimulationService(
        DemoReviewSeed seed,
        PagedGroupPreflightService preflight,
        PagedGroupCleanupService cleanup)
    {
        if (!DemoReviewData.IsValidSimulationSeed(seed))
            throw new ArgumentException("La simulación requiere un inventario sintético aislado.", nameof(seed));
        _seed = seed;
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
    }

    public async Task<DemoPagedCleanupSimulationResult> RunAsync(
        DuplicateGroupIdentity identity,
        Func<PagedGroupCleanupPlan, CancellationToken, Task<bool>> requestExplicitConfirmationAsync,
        Func<bool> isRequestCurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(requestExplicitConfirmationAsync);
        ArgumentNullException.ThrowIfNull(isRequestCurrent);
        if (!DemoReviewData.IsValidSimulationSeed(_seed) || identity.Inventory != _seed.Inventory)
            throw new InvalidOperationException("La simulación se bloqueó porque el grupo no pertenece al inventario demo aislado.");
        cancellationToken.ThrowIfCancellationRequested();

        PagedGroupCleanupLocalPreview localPreview = await _cleanup.PrepareLocalPreviewAsync(identity, cancellationToken);
        if (!isRequestCurrent())
            return DemoPagedCleanupSimulationResult.CancelledAsStale();

        var preflightTransport = new DemoOfflineDriveTransport(localPreview.Entries);
        using DriveService readOnlyDrive = preflightTransport.CreateDriveService();
        PagedGroupPreflightResult preflight = await _preflight.CheckOneGroupAsync(
            readOnlyDrive, identity, cancellationToken: cancellationToken);
        int localRequestCount = preflightTransport.Requests.Count;
        if (preflight.IsStale || !preflight.Preflight.IsSuccessful)
        {
            return new DemoPagedCleanupSimulationResult(
                preflight, null, null, false,
                string.Join(Environment.NewLine, preflight.Preflight.ValidationMessages),
                Array.Empty<string>(), localRequestCount);
        }
        if (!isRequestCurrent())
            return new DemoPagedCleanupSimulationResult(preflight, null, null, false,
                "La solicitud fue sustituida antes de preparar el plan.", Array.Empty<string>(), localRequestCount);

        var executionTransport = new DemoOfflineDriveTransport(localPreview.Entries);
        using var cleanupSession = new CleanupDriveSession(executionTransport.CreateDriveService(), isOfflineSimulation: true);
        PagedGroupCleanupPlan plan;
        try
        {
            plan = await _cleanup.PreparePlanAsync(cleanupSession, preflight, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DemoPagedCleanupSimulationResult(
                preflight, null, null, false, exception.Message,
                Array.Empty<string>(), localRequestCount + executionTransport.Requests.Count);
        }

        if (!isRequestCurrent())
            return new DemoPagedCleanupSimulationResult(preflight, plan, null, false,
                "La solicitud fue sustituida antes de pedir confirmación.", Array.Empty<string>(),
                localRequestCount + executionTransport.Requests.Count);

        bool accepted = await requestExplicitConfirmationAsync(plan, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!accepted)
        {
            return new DemoPagedCleanupSimulationResult(
                preflight, plan, null, ConfirmationDeclined: true, null,
                Array.Empty<string>(), localRequestCount + executionTransport.Requests.Count);
        }
        if (!isRequestCurrent())
        {
            return new DemoPagedCleanupSimulationResult(preflight, plan, null, false,
                "La confirmación quedó obsoleta; no se ejecutó ni simuló ninguna operación.",
                Array.Empty<string>(), localRequestCount + executionTransport.Requests.Count);
        }

        // The confirmation is built from the exact IDs displayed by the explicit confirmation UI.
        PagedGroupCleanupConfirmation confirmation = _cleanup.ConfirmPlan(
            plan,
            plan.KeepFiles.Select(file => file.Id),
            plan.CandidateFiles.Select(file => file.Id));
        CleanupOperationResult operation = await _cleanup.ExecuteConfirmedAsync(
            cleanupSession, confirmation, cancellationToken: cancellationToken);
        return new DemoPagedCleanupSimulationResult(
            preflight, plan, operation, false, null,
            Array.AsReadOnly(executionTransport.SimulatedPatchFileIds.ToArray()),
            localRequestCount + executionTransport.Requests.Count)
        {
            ObservedWriteRequests = Array.AsReadOnly(executionTransport.Requests
                .Where(request => request.Method != HttpMethod.Get)
                .Select(request => $"{request.Method.Method} {request.FileId ?? "(sin FileId)"}")
                .ToArray())
        };
    }
}

public sealed record DemoPagedCleanupSimulationResult(
    PagedGroupPreflightResult? Preflight,
    PagedGroupCleanupPlan? Plan,
    CleanupOperationResult? Operation,
    bool ConfirmationDeclined,
    string? BlockingReason,
    IReadOnlyList<string> SimulatedPatchFileIds,
    int LocalTransportRequestCount)
{
    public bool IsOfflineSimulation => true;
    public bool RealExecutionAvailable => false;
    public IReadOnlyList<string> ObservedWriteRequests { get; init; } = Array.Empty<string>();

    internal static DemoPagedCleanupSimulationResult CancelledAsStale() => new(
        null, null, null, false, "La preparación dejó de ser vigente.", Array.Empty<string>(), 0);
}

/// <summary>HTTP handler: every Drive SDK request is answered locally and cannot reach the network.</summary>
internal sealed class DemoOfflineDriveTransport : HttpClientFactory
{
    private readonly Dictionary<string, DriveFileInfo> _files;
    private readonly HashSet<string> _candidateIds;
    private readonly HashSet<string> _simulatedTrashed = new(StringComparer.Ordinal);

    public DemoOfflineDriveTransport(IReadOnlyList<PagedGroupCleanupLocalPreviewEntry> entries)
    {
        _files = entries.Select(entry => entry.File).ToDictionary(file => file.Id, StringComparer.Ordinal);
        _candidateIds = entries.Where(entry => entry.Decision == DuplicateFileDecision.CandidateForTrash)
            .Select(entry => entry.File.Id).ToHashSet(StringComparer.Ordinal);
    }

    public List<(HttpMethod Method, string? FileId)> Requests { get; } = [];
    public List<string> SimulatedPatchFileIds { get; } = [];

    public DriveService CreateDriveService() => new(new BaseClientService.Initializer
    {
        ApplicationName = "DriveDuplicateFinder offline cleanup demonstration",
        HttpClientFactory = this
    });

    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new LocalHandler(RespondAsync);

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        string? fileId = path.Contains("/files/", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..])
            : null;
        Requests.Add((request.Method, fileId));

        if (request.Method == HttpMethod.Get && path.EndsWith("/about", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { user = new { emailAddress = DemoReviewData.SyntheticEmailForSimulation } }));
        if (request.Method == HttpMethod.Get && path.EndsWith("/files/root", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, "{\"id\":\"demo-root\"}");

        if (request.Method == HttpMethod.Get && fileId is not null && _files.TryGetValue(fileId, out DriveFileInfo? file))
            return Json(HttpStatusCode.OK, FileMetadata(file, _simulatedTrashed.Contains(fileId)));

        if (request.Method == HttpMethod.Patch && fileId is not null && _candidateIds.Contains(fileId) &&
            await RequestsTrashedTrueAsync(request, cancellationToken))
        {
            _simulatedTrashed.Add(fileId);
            SimulatedPatchFileIds.Add(fileId);
            DriveFileInfo candidate = _files[fileId];
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                id = fileId,
                name = candidate.Name,
                trashed = true,
                modifiedTime = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }));
        }

        string rejectionDetail = request.Method == HttpMethod.Patch
            ? $"Synthetic demo transport rejected PATCH for {fileId ?? "(sin FileId)"}; trashed=true={await RequestsTrashedTrueAsync(request, cancellationToken)}; payload={await ReadRequestBodyAsync(request, cancellationToken)}"
            : "Synthetic demo transport rejected an unknown request.";
        return Json(HttpStatusCode.NotFound, JsonSerializer.Serialize(new { error = new { code = 404, message = rejectionDetail } }));
    }

    private static async Task<bool> RequestsTrashedTrueAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(await ReadRequestBodyAsync(request, cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "trashed", StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.True;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null) return "(sin contenido)";
        byte[] bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!request.Content.Headers.ContentEncoding.Any(value => string.Equals(value, "gzip", StringComparison.OrdinalIgnoreCase)))
            return Encoding.UTF8.GetString(bytes);

        using var compressed = new MemoryStream(bytes, writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        await gzip.CopyToAsync(decompressed, cancellationToken);
        return Encoding.UTF8.GetString(decompressed.ToArray());
    }

    private static string FileMetadata(DriveFileInfo file, bool trashed) => JsonSerializer.Serialize(new
    {
        id = file.Id,
        name = file.Name,
        mimeType = file.MimeType,
        size = file.Size.ToString(CultureInfo.InvariantCulture),
        md5Checksum = file.Md5Checksum,
        modifiedTime = file.ModifiedTime?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        parents = file.ParentIds,
        trashed,
        explicitlyTrashed = trashed,
        ownedByMe = file.OwnedByMe,
        shared = file.IsShared,
        starred = file.IsStarred,
        version = (file.Version ?? 1).ToString(CultureInfo.InvariantCulture),
        capabilities = new { canTrash = file.CanTrash }
    });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class LocalHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
