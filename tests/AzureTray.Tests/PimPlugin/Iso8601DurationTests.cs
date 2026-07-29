using System;
using AzureTray.Plugin.PIM.Policies;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The reader for the ISO-8601 durations PIM policy rules carry. Every unusable
// input must come back null, because null is what the callers treat as "cap
// unknown" — a wrong non-null answer here becomes a wrong activation duration
// offered to the user.
public sealed class Iso8601DurationTests
{
    [Theory]
    [InlineData("PT8H", 8 * 60)]
    [InlineData("PT7H", 7 * 60)]
    [InlineData("PT30M", 30)]
    [InlineData("PT1H30M", 90)]
    [InlineData("P365D", 365 * 24 * 60)]
    [InlineData("PT1H", 60)]
    [InlineData("PT12H", 12 * 60)]
    public void TryParse_ReadsIso8601Durations(string value, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), Iso8601Duration.TryParse(value));
    }

    // A day component and a time component in one value must sum, not clobber
    // one another — the shape an ARM cap longer than a day comes back in.
    [Fact]
    public void TryParse_CombinedDaysAndHours_IsSummed()
    {
        Assert.Equal(TimeSpan.FromHours(36), Iso8601Duration.TryParse("P1DT12H"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void TryParse_BlankIsUnknown(string? value)
    {
        Assert.Null(Iso8601Duration.TryParse(value));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("8 hours")]
    [InlineData("PT")]
    [InlineData("P")]
    [InlineData("PTH")]
    [InlineData("8H")]
    [InlineData("{}")]
    public void TryParse_MalformedIsUnknown(string value)
    {
        Assert.Null(Iso8601Duration.TryParse(value));
    }

    // Zero and negative are not "unlimited" and not "instant" — they are
    // nonsense for an activation cap, so they degrade to unknown and the
    // caller's fallback applies.
    [Theory]
    [InlineData("PT0S")]
    [InlineData("PT0H")]
    [InlineData("P0D")]
    [InlineData("-PT8H")]
    [InlineData("-P1D")]
    public void TryParse_NonPositiveIsUnknown(string value)
    {
        Assert.Null(Iso8601Duration.TryParse(value));
    }

    // Documented behaviour, not an aspiration: XmlConvert.ToTimeSpan implements
    // xsd:duration, which requires the leading "P" designator. TimeSpan.Parse's
    // clock notation ("08:00:00", "1.12:00:00") is therefore rejected and comes
    // back as unknown. Nothing in Graph or ARM emits that form; the cases exist
    // so a future switch to TimeSpan.Parse (which would in turn reject "PT8H")
    // cannot happen silently.
    [Theory]
    [InlineData("08:00:00")]
    [InlineData("1.12:00:00")]
    [InlineData("00:30:00")]
    public void TryParse_TimeSpanClockNotation_IsUnknown(string value)
    {
        Assert.Null(Iso8601Duration.TryParse(value));
    }

    // An out-of-range value overflows rather than parsing; that must not escape
    // as an exception into a policy read.
    [Fact]
    public void TryParse_AbsurdlyLargeValue_IsUnknownRatherThanThrowing()
    {
        Assert.Null(Iso8601Duration.TryParse("P100000000000Y"));
    }
}
