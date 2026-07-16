using System.Globalization;

namespace DriveDuplicateFinder.Utilities;

public static class FileSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        string format = unitIndex == 0 ? "0" : "0.##";
        return string.Format(CultureInfo.CurrentCulture, "{0} {1}", value.ToString(format, CultureInfo.CurrentCulture), Units[unitIndex]);
    }
}
