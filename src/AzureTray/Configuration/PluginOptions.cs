using System.Collections.Generic;

namespace AzureTray.Configuration;

public sealed class PluginOptions
{
    public const string SectionName = "App:Plugins";

    public PluginTrustMode TrustMode { get; init; } = PluginTrustMode.AllowUnsigned;

    public IList<string> TrustedPublisherThumbprints { get; init; } = new List<string>();

    // How often the background loop asks nuget.org whether any installed
    // plugin has a newer version. Plugins ship independently of the host, so
    // without this a user on the newest host can sit on a months-old plugin.
    // Set to 0 to disable the loop entirely.
    public int UpdateCheckIntervalHours { get; init; } = 6;

    // Seeds the "Update plugins automatically" checkbox on first run. Off by
    // default; the user's own choice is persisted separately and wins after
    // that. Even when enabled, an update that would need a user decision
    // (High/Critical advisory, unsigned-plugin trust prompt) is refused and
    // left for a manual click.
    public bool AutoUpdate { get; init; }
}

public enum PluginTrustMode
{
    // Default. The host prompts the user once at INSTALL time whenever a
    // plugin binary is not Authenticode-signed; the user can accept or
    // decline. At LOAD time (startup or hot-load) the host loads any
    // installed plugin without re-checking — install was the trust gate.
    AllowUnsigned,

    // Treated identically to AllowUnsigned in v0.x. Kept for backward
    // compatibility with existing config files that set this value; future
    // releases may add stricter semantics here once code signing ships.
    RequireSigned,

    // Org-managed mode for enterprise rollouts. Plugins must be signed by a
    // certificate whose thumbprint is listed in TrustedPublisherThumbprints.
    // The user is NOT prompted — non-matching signatures are silently
    // rejected at both install and load time so a managed deployment can't
    // be overridden interactively.
    RequireTrustedPublisher,
}
