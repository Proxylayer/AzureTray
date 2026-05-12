using System.Collections.Generic;

namespace AzureTray.Configuration;

public sealed class PluginOptions
{
    public const string SectionName = "App:Plugins";

    public PluginTrustMode TrustMode { get; init; } = PluginTrustMode.AllowUnsigned;

    public IList<string> TrustedPublisherThumbprints { get; init; } = new List<string>();
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
