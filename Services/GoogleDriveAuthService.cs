using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace DriveDuplicateFinder.Services;

public sealed class GoogleDriveAuthService
{
    private static readonly string[] Scopes =
    [
        DriveService.Scope.DriveMetadataReadonly
    ];

    public async Task<DriveService> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        string credentialsPath = Path.Combine(
            AppContext.BaseDirectory,
            "credentials.json");

        if (!File.Exists(credentialsPath))
        {
            throw new FileNotFoundException(
                "No se encuentra el archivo credentials.json.",
                credentialsPath);
        }

        await using FileStream stream = new(
            credentialsPath,
            FileMode.Open,
            FileAccess.Read);

        GoogleClientSecrets secrets = await GoogleClientSecrets.FromStreamAsync(
            stream,
            cancellationToken);

        string tokenPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "DriveDuplicateFinder",
            "token.json");

        UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets,
            Scopes,
            "usuario",
            cancellationToken,
            new FileDataStore(tokenPath, true));

        return new DriveService(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Drive Duplicate Finder"
            });
    }
}
