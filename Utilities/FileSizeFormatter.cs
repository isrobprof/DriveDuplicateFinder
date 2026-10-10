using System.Globalization;
using System.Numerics;

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

    public static string Format(BigInteger bytes)
    {
        if (bytes < BigInteger.Zero)
        {
            return "0 B";
        }

        BigInteger unitSize = BigInteger.One;
        int unitIndex = 0;
        while (bytes >= unitSize * 1024 && unitIndex < Units.Length - 1)
        {
            unitSize *= 1024;
            unitIndex++;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        if (unitIndex == 0)
        {
            return string.Format(culture, "{0} {1}", bytes.ToString("0", culture), Units[unitIndex]);
        }

        BigInteger hundredths = BigInteger.DivRem(bytes * 100, unitSize, out BigInteger remainder);
        int midpointComparison = (remainder * 2).CompareTo(unitSize);
        if (midpointComparison > 0 || (midpointComparison == 0 && !hundredths.IsEven))
        {
            hundredths++;
        }

        BigInteger whole = BigInteger.DivRem(hundredths, 100, out BigInteger fractionalPart);
        string formattedValue = whole.ToString("0", culture);
        if (!fractionalPart.IsZero)
        {
            string decimals = fractionalPart.ToString("D2", culture).TrimEnd('0');
            formattedValue += culture.NumberFormat.NumberDecimalSeparator + decimals;
        }

        return string.Format(culture, "{0} {1}", formattedValue, Units[unitIndex]);
    }
}
