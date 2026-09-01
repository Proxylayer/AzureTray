namespace AzureTray.Configuration;

public sealed class TokenFreshnessOptions
{
    public const string SectionName = "App:TokenFreshness";

    // How often the background loop compares each ready tenant's cached
    // access token against the scopes the host and its plugins need.
    //
    // A token acquired before an admin-consent change keeps serving the old
    // scope set for its full lifetime (~1 hour), and MSAL's cache is
    // persisted to disk, so a restart does not clear it either. Without this
    // loop the only cures are Settings -> Fix permissions / Refresh tokens,
    // or waiting the hour out. Half an hour bounds the damage without adding
    // meaningful traffic: a healthy tenant costs one cached-token read per
    // resource and nothing else.
    //
    // Set to 0 to disable the loop entirely.
    public double CheckIntervalMinutes { get; init; } = 30;

    // Delay before the first check. Startup already runs sign-in, the
    // readiness probe and two update checks; a stale token has by definition
    // been stale for a while, so this waits for that burst to finish rather
    // than competing with it.
    public int FirstCheckDelaySeconds { get; init; } = 120;
}
