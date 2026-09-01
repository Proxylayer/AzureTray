using System;
using System.Globalization;

namespace AzureTray.Plugin.PIM.Groups;

// The two things a user can hold on a PIM-onboarded group. PIM for Groups has
// no role definitions: accessId is the whole vocabulary, and it fills the
// "role" slot everywhere a directory role's roleDefinitionId would — the menu
// row, the dedup key, the active-assignment match.
//
// Send camelCase; read case-insensitively. Graph's schema documents "member" /
// "owner" but live payloads have been seen returning them capitalized, so no
// comparison here is ordinal-exact and nothing round-trips a wire value
// straight back into a request without normalizing it first.
internal static class GroupAccess
{
    public const string Member = "member";
    public const string Owner = "owner";

    // The form to put in a request body. An unrecognized value is passed
    // through lower-cased rather than coerced to "member": if Microsoft ever
    // adds a third access type, a 400 naming it beats silently activating the
    // wrong access.
    public static string Normalize(string? accessId)
        => string.IsNullOrWhiteSpace(accessId)
            ? Member
            : accessId.Trim().ToLowerInvariant();

    // Menu label — "Member" / "Owner". Title-cased from whatever arrived so an
    // unknown future value still reads as a word rather than a raw token.
    public static string DisplayFor(string? accessId)
    {
        var normalized = Normalize(accessId);
        return normalized switch
        {
            Member => "Member",
            Owner => "Owner",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized),
        };
    }

    // Group key component, matching EntraDirectoryScope.NormalizeForKey: Graph
    // does not guarantee stable casing, so keys are lower-cased.
    public static string NormalizeForKey(string? accessId) => Normalize(accessId);

    public static bool AreSame(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
