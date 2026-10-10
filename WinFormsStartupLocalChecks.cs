namespace DriveDuplicateFinder;

internal static class WinFormsStartupLocalChecks
{
    public static bool Verify()
    {
        EnsureStaThread();
        return Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
    }

    public static void EnsureStaThread()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("WinForms debe inicializarse y ejecutarse en un hilo STA.");
    }
}
