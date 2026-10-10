using System.Globalization;
using System.Text.Json;

namespace DriveDuplicateFinder.Data.Sqlite;

internal static class SqlitePersistenceFormat
{
    public static string ToUtcText(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? FromUtcText(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static string ToParentIdsJson(IReadOnlyList<string> parentIds) => JsonSerializer.Serialize(parentIds ?? Array.Empty<string>());

    public static IReadOnlyList<string> FromParentIdsJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
    }

    public static string ToOwnerNamesJson(IReadOnlyList<string> ownerNames) => JsonSerializer.Serialize(ownerNames ?? Array.Empty<string>());

    public static IReadOnlyList<string> FromOwnerNamesJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
    }
}
