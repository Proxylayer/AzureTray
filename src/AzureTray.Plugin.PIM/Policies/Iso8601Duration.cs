using System;
using System.Xml;

namespace AzureTray.Plugin.PIM.Policies;

// Reader and writer for the ISO-8601 durations PIM policy rules and activation
// requests carry ("PT8H", "PT30M", "P365D"). TimeSpan.Parse does NOT accept
// that form — it expects "[d.]hh:mm[:ss]" — so XmlConvert.ToTimeSpan is the
// reader, and Format is the matching writer every PIM client sends activation
// durations with. Both live here so the pair cannot drift apart.
internal static class Iso8601Duration
{
    // Returns null for anything unusable (blank, malformed, non-positive) so
    // callers treat it the same as a missing rule: cap unknown.
    public static TimeSpan? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            var parsed = XmlConvert.ToTimeSpan(value);
            return parsed > TimeSpan.Zero ? parsed : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static string Format(TimeSpan duration)
    {
        var totalMinutes = (long)Math.Round(duration.TotalMinutes);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return (hours, minutes) switch
        {
            (0, var m) => $"PT{m}M",
            (var h, 0) => $"PT{h}H",
            (var h, var m) => $"PT{h}H{m}M",
        };
    }
}
