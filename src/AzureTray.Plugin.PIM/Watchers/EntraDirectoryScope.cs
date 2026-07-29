using System;

namespace AzureTray.Plugin.PIM.Watchers;

// Reads Graph's directoryScopeId on a role eligibility. Almost every Entra
// eligibility is directory-wide ("/"), but an eligibility can be scoped to an
// administrative unit or a single application object, and treating those as "/"
// both mislabels the row and activates the role at the wrong scope.
internal static class EntraDirectoryScope
{
    // What Graph expects when the eligibility covers the whole directory, and
    // the fallback whenever the scope is absent from the response.
    public const string Directory = "/";

    private const string DirectoryDisplay = "Entra ID directory";
    private const string AdministrativeUnits = "/administrativeUnits/";

    // The scope to send on an activation / deactivation request.
    public static string OrDirectory(string? directoryScopeId)
        => IsDirectory(directoryScopeId) ? Directory : directoryScopeId!.Trim();

    // Menu label for the row. Derived purely from the scope id — resolving an
    // administrative unit's display name would cost an extra Graph call per
    // scope, and the eligibility response does not carry it, so a non-directory
    // scope shows its id.
    public static string DisplayFor(string? directoryScopeId)
    {
        if (IsDirectory(directoryScopeId)) return DirectoryDisplay;

        var scope = directoryScopeId!.Trim();
        return scope.StartsWith(AdministrativeUnits, StringComparison.OrdinalIgnoreCase)
            ? $"Administrative unit {scope[AdministrativeUnits.Length..]}"
            : scope;
    }

    // Group key component: an absent scope and an explicit "/" are the same
    // scope, and Graph's casing on object ids is not guaranteed stable.
    public static string NormalizeForKey(string? directoryScopeId)
        => IsDirectory(directoryScopeId)
            ? Directory
            : directoryScopeId!.Trim().TrimEnd('/').ToLowerInvariant();

    private static bool IsDirectory(string? directoryScopeId)
        => string.IsNullOrWhiteSpace(directoryScopeId)
            || string.Equals(directoryScopeId.Trim(), Directory, StringComparison.Ordinal);
}
