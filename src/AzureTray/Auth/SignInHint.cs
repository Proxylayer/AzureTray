using System;

namespace AzureTray.Auth;

// Picks the best value to hand MSAL as its LoginHint when signing a tenant in.
//
// For B2B *guest* users the userPrincipalName (and the token's `upn` claim) is
// the synthetic "external" form that only exists in the resource tenant and
// has no password of its own, e.g.
//
//     jbland_proxylayer.net#EXT#@cfoadminpgcfo.onmicrosoft.com
//
// Pre-filling that into the broker traps the user: they type their real
// home-tenant password (for jbland@proxylayer.net) and it never matches the
// #EXT# shadow identity, so sign-in fails. We therefore prefer any clean
// candidate (the mail attribute, the email/preferred_username claim) over the
// #EXT# UPN, and only as a last resort un-mangle the #EXT# form back into the
// home email it was derived from. When nothing usable is found we return null:
// an empty hint lets the user type the right address, which beats a
// guaranteed-wrong one.
public static class SignInHint
{
    private const string ExternalMarker = "#EXT#";

    // True for the synthetic guest UPN form ("...#EXT#@resourcetenant").
    public static bool IsExternal(string? value)
        => value is not null && value.Contains(ExternalMarker, StringComparison.OrdinalIgnoreCase);

    // Candidates are tried in priority order. The first clean (non-#EXT#)
    // value wins. If every candidate is the #EXT# form, the first one is
    // un-mangled back to its home email. If there's nothing at all, null.
    public static string? Pick(params string?[] candidates)
    {
        string? firstExternal = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var trimmed = candidate.Trim();
            if (!IsExternal(trimmed)) return trimmed;
            firstExternal ??= trimmed;
        }
        return firstExternal is null ? null : UnmangleExternalUpn(firstExternal);
    }

    // "jbland_proxylayer.net#EXT#@tenant.onmicrosoft.com" -> "jbland@proxylayer.net"
    //
    // Entra builds the #EXT# UPN by replacing the '@' of the home email with
    // '_' and appending "#EXT#@{resourceTenant}". Reverse it by taking the
    // segment before #EXT# and turning the LAST '_' back into '@'. This is a
    // heuristic — a home local-part that itself contains '_' breaks it — so it
    // is only used as a last resort when no clean email is available.
    public static string? UnmangleExternalUpn(string? upn)
    {
        if (string.IsNullOrWhiteSpace(upn)) return null;

        var marker = upn.IndexOf(ExternalMarker, StringComparison.OrdinalIgnoreCase);
        if (marker <= 0) return null;

        var prefix = upn[..marker];
        var lastUnderscore = prefix.LastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore >= prefix.Length - 1) return null;

        return string.Concat(prefix.AsSpan(0, lastUnderscore), "@", prefix.AsSpan(lastUnderscore + 1));
    }
}
