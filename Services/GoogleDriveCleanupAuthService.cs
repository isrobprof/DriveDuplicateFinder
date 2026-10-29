using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace DriveDuplicateFinder.Services;

public sealed class GoogleDriveCleanupAuthService
{
    public static readonly string CleanupScope = DriveService.Scope.Drive;

    private static readonly string[] Scopes =
    [
        CleanupScope
    ];

    public static string CleanupTokenDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DriveDuplicateFinder",
        "token-cleanup");

    public async Task<DriveService> ConnectAsync(CancellationToken cancellationToken = default)
    {
        string credentialsPath = Path.Combine(AppContext.BaseDirectory, "credentials.json");
        if (!File.Exists(credentialsPath))
        {
            throw new FileNotFoundException("No se encuentra el archivo credentials.json necesario para activar el modo limpieza.", credentialsPath);
        }

        await using FileStream stream = new(credentialsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        GoogleClientSecrets secrets = await GoogleClientSecrets.FromStreamAsync(stream, cancellationToken);

        UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets,
            Scopes,
            "usuario-cleanup",
            cancellationToken,
            new FileDataStore(CleanupTokenDirectory, true));

        if (!HasCleanupScope(credential.Token.Scope))
        {
            throw new CleanupAuthorizationScopeException();
        }

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Drive Duplicate Finder - Cleanup"
        });
    }

    /// <summary>
    /// Creates a cleanup session only after ConnectAsync has verified the separate Drive scope.
    /// This session type is required by the paged single-group cleanup coordinator.
    /// </summary>
    public async Task<CleanupDriveSession> ConnectSessionAsync(CancellationToken cancellationToken = default) =>
        new(await ConnectAsync(cancellationToken), isOfflineSimulation: false);

    public void ResetStoredAuthorization()
    {
        if (Directory.Exists(CleanupTokenDirectory))
        {
            Directory.Delete(CleanupTokenDirectory, recursive: true);
        }
    }

    private static bool HasCleanupScope(string? grantedScopes)
    {
        return grantedScopes?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(CleanupScope, StringComparer.Ordinal) == true;
    }
}

/// <summary>Capability wrapper proving this DriveService came from the cleanup-scope authorization flow.</summary>
public sealed class CleanupDriveSession : IDisposable
{
    private int _singleCandidateTrialAttempted;

    internal CleanupDriveSession(DriveService driveService, bool isOfflineSimulation = false)
    {
        DriveService = driveService ?? throw new ArgumentNullException(nameof(driveService));
        IsOfflineSimulation = isOfflineSimulation;
    }

    internal DriveService DriveService { get; }

    internal bool IsOfflineSimulation { get; }

    /// <summary>Consumes this OAuth session's one permitted trial attempt.</summary>
    internal bool TryBeginSingleCandidateTrialAttempt() =>
        Interlocked.CompareExchange(ref _singleCandidateTrialAttempted, 1, 0) == 0;

    internal bool HasUsedSingleCandidateTrialAttempt => Volatile.Read(ref _singleCandidateTrialAttempted) != 0;

    public void Dispose() => DriveService.Dispose();
}

public sealed class CleanupAuthorizationScopeException : Exception
{
    public CleanupAuthorizationScopeException()
        : base("Google no concedió el permiso completo necesario para el modo limpieza.")
    {
    }
}
