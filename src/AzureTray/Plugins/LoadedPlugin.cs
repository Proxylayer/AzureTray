using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugins;

public sealed record LoadedPlugin(
    ITrayPlugin Plugin,
    string AssemblyPath,
    SignatureVerdict Signature);
