namespace DriveDuplicateFinder;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--verify-sqlite-infrastructure", StringComparison.Ordinal))
        {
            WinFormsStartupLocalChecks.EnsureStaThread();
            VerifyAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Length == 1 && string.Equals(args[0], "--demo-review", StringComparison.Ordinal))
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1(demoMode: true));
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }

    private static async Task VerifyAsync()
    {
        var result = new
        {
            WinFormsSta = WinFormsStartupLocalChecks.Verify(),
            Infrastructure = await Data.Sqlite.SqliteInfrastructureLocalChecks.VerifyAsync(),
            RecoverableFullScan = await Data.Sqlite.RecoverableFullScanLocalChecks.VerifyAsync(),
            ReviewStatePersistence = await Data.Sqlite.ReviewStatePersistenceLocalChecks.VerifyAsync(),
            PersistedCleanupGroupReviewAdapter = await Data.Sqlite.PersistedCleanupGroupReviewAdapterLocalChecks.VerifyAsync(),
            GoogleDriveTrashPreflight = await Services.GoogleDriveTrashPreflightLocalChecks.VerifyAsync(),
            PagedReadOnlyView = Services.PagedReadOnlyViewLocalChecks.Verify(),
            DemoReviewIsolation = await Services.DemoReviewData.VerifyIsolationAsync()
        };
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            result,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
