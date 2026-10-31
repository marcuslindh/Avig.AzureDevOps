using System.Text.Json;

namespace Avig.AzureDevOps;

/// <summary>
/// The package's enums to and from the names the REST API uses.
/// </summary>
/// <remarks>
/// The API writes its values in camelCase — <c>active</c>, <c>wontFix</c>,
/// <c>rejectedByPolicy</c> — and the enums are named after them, so the translation is the
/// name itself. A flags value comes as a comma-separated list, <c>edit, rename</c>, which
/// <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> already reads.
/// </remarks>
internal static class ApiEnum
{
    /// <summary>
    /// The value the API sent, or <paramref name="fallback"/> when it is missing or one the
    /// package does not know — the API adds values, and a new one must not break a list.
    /// </summary>
    public static TEnum Parse<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        // A number is not a name; the API never sends one, and Enum.TryParse would accept it.
        if (string.IsNullOrWhiteSpace(value) || char.IsAsciiDigit(value.TrimStart()[0]))
        {
            return fallback;
        }

        if (Enum.TryParse(value, ignoreCase: true, out TEnum parsed))
        {
            return parsed;
        }

        return fallback;
    }

    /// <summary>The name the API wants for a value: <see cref="PullRequests.PullRequestStatus.Active"/> → <c>active</c>.</summary>
    public static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
