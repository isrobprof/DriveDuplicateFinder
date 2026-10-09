namespace DriveDuplicateFinder;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static async Task Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--verify-sqlite-infrastructure", StringComparison.Ordinal))
        {
            Data.Sqlite.SqliteInfrastructureCheckResult result =
                await Data.Sqlite.SqliteInfrastructureLocalChecks.VerifyAsync();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                result,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }
}
