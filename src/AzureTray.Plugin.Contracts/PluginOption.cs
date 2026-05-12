namespace AzureTray.Plugin.Contracts;

// One generic option exposed to the host's Settings UI. The host renders
// based on Kind; the plugin reads back via IPluginConfigurable.Values.
public sealed record PluginOption(
    string Key,
    string Label,
    PluginOptionKind Kind,
    string? Description = null,
    object? DefaultValue = null);

public enum PluginOptionKind
{
    Boolean,
    Text,
    Number,
}
