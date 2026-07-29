using System;
using System.Collections.Generic;
using System.Linq;

namespace AzureTray.Plugin.PIM.Watchers;

// One option in the activation duration prompt. The TimeSpan travels with the
// label so a pick is resolved by looking the label up in the list it came
// from — the duration never has to be recovered by parsing a display string.
internal sealed record ActivationDurationChoice(string Label, TimeSpan Duration);

// Derives the durations an eligible role may be activated for from its PIM
// policy's maximum activation duration (Expiration_EndUser_Assignment). Offering
// a duration above the cap earns a server-side 400 surfaced as a generic
// activation failure, so the list is clamped up front.
internal static class ActivationDurationChoices
{
    // Steps offered when the policy allows them. Also the list used verbatim
    // when the cap is unknown — i.e. the behaviour that predates policy reads.
    private static readonly TimeSpan[] StandardSteps =
    {
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(8),
    };

    // Entra ID role activation is time-bound to at most 8 hours by the service
    // itself; a role's policy can lower that but not raise it. So when an Entra
    // policy read fails or omits the rule, 8h is a documented ceiling rather
    // than a guess. Azure RBAC has no documented equivalent, so an ARM role with
    // an unknown cap keeps the full standard list.
    private static readonly TimeSpan EntraActivationCeiling = TimeSpan.FromHours(8);

    public static IReadOnlyList<ActivationDurationChoice> For(UnifiedEligibleRole role)
        => Build(EffectiveCap(role));

    // The cap to clamp against: the role's policy value when known, otherwise
    // the provider's documented ceiling (Entra) or nothing (ARM).
    internal static TimeSpan? EffectiveCap(UnifiedEligibleRole role)
        => role.MaxActivationDuration
            ?? (role.Source == PimSource.EntraId ? EntraActivationCeiling : null);

    // Standard steps at or below the cap, plus the cap itself when it is not
    // already one of them. Never empty and never above the cap: a cap tighter
    // than the smallest step yields just the cap.
    internal static IReadOnlyList<ActivationDurationChoice> Build(TimeSpan? cap)
    {
        if (cap is not { } max || max <= TimeSpan.Zero)
        {
            return StandardSteps.Select(ToChoice).ToArray();
        }

        var durations = StandardSteps.Where(step => step <= max).ToList();
        if (durations.Count == 0 || durations[^1] < max)
        {
            durations.Add(max);
        }

        return durations.Select(ToChoice).ToArray();
    }

    // The duration behind a label the notifier handed back, or null when the
    // user dismissed the prompt or the label is not one we offered.
    internal static TimeSpan? Match(IReadOnlyList<ActivationDurationChoice> choices, string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        foreach (var choice in choices)
        {
            if (string.Equals(choice.Label, label, StringComparison.Ordinal)) return choice.Duration;
        }
        return null;
    }

    // Menu-row hint for an eligible role, or null when there is nothing worth
    // saying: an unknown cap (a policy-read failure is not news the user can
    // act on) or a cap that does not restrict the standard steps.
    internal static string? CapHint(UnifiedEligibleRole role)
    {
        if (role.MaxActivationDuration is not { } cap) return null;
        if (cap <= TimeSpan.Zero || cap >= StandardSteps[^1]) return null;
        return EligibleRolesWatcher.FormatDuration(cap);
    }

    private static ActivationDurationChoice ToChoice(TimeSpan duration)
        => new(Label(duration), duration);

    // Whole-hour durations under a day keep the long-standing "1 hour" /
    // "4 hours" wording; anything else (a 30-minute or 90-minute cap, a
    // multi-day ARM cap) falls back to the compact shared formatter.
    private static string Label(TimeSpan duration)
    {
        var wholeHours = duration.Ticks % TimeSpan.TicksPerHour == 0;
        if (!wholeHours || duration.TotalHours is < 1 or >= 24)
        {
            return EligibleRolesWatcher.FormatDuration(duration);
        }

        var hours = (int)duration.TotalHours;
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }
}
