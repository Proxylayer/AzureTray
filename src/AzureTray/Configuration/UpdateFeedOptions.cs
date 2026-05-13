namespace AzureTray.Configuration;

public sealed class UpdateFeedOptions
{
    public const string SectionName = "App:Update";

    public string FeedUrl { get; init; } = string.Empty;

    // Background re-check cadence. The tray process stays running for days
    // at a time, so a startup-only check would mean users never see new
    // releases until they restart. Set to 0 to disable the periodic loop
    // (startup check still runs).
    public double CheckIntervalHours { get; init; } = 1;
}
