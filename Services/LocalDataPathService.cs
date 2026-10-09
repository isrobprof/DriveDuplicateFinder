namespace DriveDuplicateFinder.Services;

/// <summary>Resuelve las rutas locales sin crear directorios hasta que un consumidor las inicializa.</summary>
public sealed class LocalDataPathService
{
    public LocalDataPathService(string? baseDataDirectory = null)
    {
        BaseDataDirectory = baseDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DriveDuplicateFinder");
    }

    public string BaseDataDirectory { get; }

    public string DatabasePath => Path.Combine(BaseDataDirectory, "drive-cache.db");
}
