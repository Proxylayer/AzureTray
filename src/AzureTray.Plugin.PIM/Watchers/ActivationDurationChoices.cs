using System;

namespace AzureTray.Plugin.PIM.Watchers;

// Geometry for the activation duration slider: 30-minute steps from Minimum
// up to the role's policy cap. Initial is where the thumb starts.
internal sealed record ActivationDurationSlider(
    TimeSpan Minimum, TimeSpan Maximum, TimeSpan Step, TimeSpan Initial);

// Derives the activation duration slider for an eligible role from its PIM
// policy's maximum activation duration (Expiration_EndUser_Assignment).
// Offering a duration above the cap earns a server-side 400 surfaced as a
// generic activation failure, so the slider's maximum is clamped up front.
internal static class ActivationDurationChoices
{
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultInitial = TimeSpan.FromHours(1);

    // Both Graph-served sources — Entra ID directory roles and PIM for Groups
    // membership/ownership — are time-bound to at most 8 hours by the service
    // itself; a policy can lower that but not raise it. So when a policy read
    // fails or omits the rule, 8h is a documented ceiling rather than a guess.
    // Azure RBAC has no documented equivalent, but 8h matches the longest
    // duration the pre-slider choice list ever offered for an unknown cap.
    private static readonly TimeSpan FallbackCeiling = TimeSpan.FromHours(8);

    public static ActivationDurationSlider For(UnifiedEligibleRole role)
    {
        var max = EffectiveCap(role) is { } cap && cap > TimeSpan.Zero
            ? cap
            : FallbackCeiling;
        // A cap tighter than one step collapses the slider to that single
        // stop rather than ever offering more than the policy allows.
        var min = Step <= max ? Step : max;
        var initial = DefaultInitial <= max ? DefaultInitial : max;
        return new(min, max, Step, initial);
    }

    // The cap to clamp against: the role's policy value when known, otherwise
    // the provider's documented ceiling (Graph-served sources) or nothing (ARM).
    internal static TimeSpan? EffectiveCap(UnifiedEligibleRole role)
        => role.MaxActivationDuration
            ?? (role.Source is PimSource.EntraId or PimSource.EntraGroup
                ? FallbackCeiling
                : null);

    // Menu-row hint for an eligible role: the policy's max activation duration
    // whenever it is known. Earlier builds hid caps at or above the longest
    // standard step (8h) as "not restrictive", but users read the silence as
    // missing data, so every known cap is now shown. Null only for an unknown
    // cap (a policy-read failure is not news the user can act on) or a
    // nonsensical non-positive one.
    internal static string? CapHint(UnifiedEligibleRole role)
    {
        if (role.MaxActivationDuration is not { } cap) return null;
        if (cap <= TimeSpan.Zero) return null;
        return EligibleRolesWatcher.FormatDuration(cap);
    }
}
