namespace AzureTray.Configuration;

public sealed class UpdateFeedOptions
{
    public const string SectionName = "App:Update";

    public string FeedUrl { get; init; } = string.Empty;
}
