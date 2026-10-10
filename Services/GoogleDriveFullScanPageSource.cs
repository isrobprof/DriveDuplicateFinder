using System.Net;
using System.Text;
using DriveDuplicateFinder.Models.Persistence;
using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace DriveDuplicateFinder.Services;

internal sealed class GoogleDriveFullScanPageSource : IFullScanPageSource
{
    private const string RequestedFields =
        "nextPageToken,files(id,name,mimeType,size,md5Checksum,createdTime,modifiedTime,parents,driveId,owners(displayName),shared,starred,ownedByMe,version,capabilities(canTrash),trashed)";

    private readonly DriveService _driveService;
    private readonly string _accountKey;
    private readonly string _scopeKey;

    public GoogleDriveFullScanPageSource(DriveService driveService, string accountKey, string scopeKey)
    {
        _driveService = driveService ?? throw new ArgumentNullException(nameof(driveService));
        _accountKey = accountKey;
        _scopeKey = scopeKey;
    }

    public async Task<FullScanPage> ReadPageAsync(string? pageToken, CancellationToken cancellationToken)
    {
        try
        {
            var request = _driveService.Files.List();
            request.Q = "trashed = false";
            request.PageSize = 1000;
            request.PageToken = pageToken;
            request.Corpora = "user";
            request.IncludeItemsFromAllDrives = true;
            request.SupportsAllDrives = true;
            request.Fields = RequestedFields;
            FileList response = await request.ExecuteAsync(cancellationToken);
            DateTimeOffset cachedAtUtc = DateTimeOffset.UtcNow;
            DriveFileCacheRecord[] records = (response.Files ?? [])
                .Where(file => !string.IsNullOrWhiteSpace(file.Id))
                .Select(file => ToCacheRecord(file, cachedAtUtc))
                .ToArray();
            return new FullScanPage(records, response.NextPageToken, response.Files?.Count ?? 0);
        }
        catch (GoogleApiException exception) when (pageToken is not null &&
            IsInvalidPageToken(
                exception.HttpStatusCode,
                exception.Error?.Message,
                exception.Error?.Errors?.Select(error =>
                    new PageTokenErrorDetail(error.Reason, error.Message, error.Location))))
        {
            throw new InvalidScanCheckpointException(
                "Google Drive rechazó el pageToken guardado como expirado o inválido.",
                exception);
        }
    }

    internal static bool IsInvalidPageToken(
        HttpStatusCode statusCode,
        string? errorMessage,
        IEnumerable<PageTokenErrorDetail>? errorDetails)
    {
        if (statusCode != HttpStatusCode.BadRequest)
        {
            return false;
        }

        string topLevelMessage = NormalizeErrorText(errorMessage);
        if (ContainsTokenReference(topLevelMessage) && ContainsInvalidMarker(topLevelMessage))
        {
            return true;
        }

        if (errorDetails is null)
        {
            return false;
        }

        foreach (PageTokenErrorDetail detail in errorDetails)
        {
            string reason = NormalizeErrorText(detail.Reason);
            string location = NormalizeErrorText(detail.Location);
            string message = NormalizeErrorText(detail.Message);

            bool explicitTokenReason = reason is "invalidpagetoken" or "expiredpagetoken" or
                "pagetokenexpired" or "invalidpagecursor" or "expiredpagecursor" or "pagecursorexpired";
            bool invalidAtTokenLocation = (location is "pagetoken" or "pagecursor") &&
                (reason is "invalid" or "expired");
            bool specificTokenMessage = ContainsTokenReference(message) && ContainsInvalidMarker(message);

            if (explicitTokenReason || invalidAtTokenLocation || specificTokenMessage)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeErrorText(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool ContainsTokenReference(string value) =>
        value.Contains("pagetoken", StringComparison.Ordinal) ||
        value.Contains("pagecursor", StringComparison.Ordinal) ||
        value.Contains("cursor", StringComparison.Ordinal);

    private static bool ContainsInvalidMarker(string value)
    {
        if (value.Contains("notexpired", StringComparison.Ordinal) ||
            value.Contains("unexpired", StringComparison.Ordinal) ||
            value.Contains("isvalid", StringComparison.Ordinal))
        {
            return false;
        }

        return value.Contains("invalid", StringComparison.Ordinal) ||
            value.Contains("expired", StringComparison.Ordinal) ||
            value.Contains("expire", StringComparison.Ordinal) ||
            value.Contains("malformed", StringComparison.Ordinal) ||
            value.Contains("notvalid", StringComparison.Ordinal) ||
            value.Contains("rejected", StringComparison.Ordinal);
    }

    private DriveFileCacheRecord ToCacheRecord(GoogleFile file, DateTimeOffset cachedAtUtc)
    {
        string name = string.IsNullOrWhiteSpace(file.Name) ? "Archivo sin nombre" : file.Name;
        return new DriveFileCacheRecord
        {
            FileId = file.Id!,
            Name = name,
            AccountKey = _accountKey,
            ScopeKey = _scopeKey,
            NormalizedName = name.Normalize(NormalizationForm.FormKC).ToUpperInvariant(),
            Extension = Path.GetExtension(name),
            MimeType = file.MimeType,
            SizeBytes = file.Size,
            Md5Checksum = file.Md5Checksum,
            Version = file.Version,
            OwnerNames = file.Owners?.Select(owner => owner.DisplayName).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray() ?? [],
            ModifiedTimeUtc = file.ModifiedTimeDateTimeOffset,
            CreatedTimeUtc = file.CreatedTimeDateTimeOffset,
            ParentIds = file.Parents?.ToArray() ?? [],
            DriveId = file.DriveId,
            OwnedByMe = file.OwnedByMe,
            CanTrash = file.Capabilities?.CanTrash,
            IsShared = file.Shared,
            IsStarred = file.Starred,
            IsTrashed = file.Trashed == true,
            LastChangedAtUtc = file.ModifiedTimeDateTimeOffset,
            CachedAtUtc = cachedAtUtc
        };
    }
}

internal sealed record PageTokenErrorDetail(string? Reason, string? Message, string? Location);
