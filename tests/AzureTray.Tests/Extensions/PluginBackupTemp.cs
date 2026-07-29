using Xunit;

namespace AzureTray.Tests.Extensions;

// PluginFolderBackup snapshots land in a single well-known folder under %TEMP%
// (AzureTray.plugin-backup). Tests that assert on what appears and disappears
// there have to run one at a time, or they see each other's snapshots.
[CollectionDefinition(Name)]
public sealed class PluginBackupTemp
{
    public const string Name = "PluginBackupTemp";
}
