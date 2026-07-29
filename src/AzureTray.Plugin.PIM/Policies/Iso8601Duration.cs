using System;
using System.Xml;

namespace AzureTray.Plugin.PIM.Policies;

// Reader for the ISO-8601 durations PIM policy rules carry ("PT8H", "PT30M",
// "P365D"). TimeSpan.Parse does NOT accept that form — it expects
// "[d.]hh:mm[:ss]" — so XmlConvert.ToTimeSpan is the reader that pairs with
// the ISO-8601 writers in GraphPimClient/ArmPimClient.
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
}
