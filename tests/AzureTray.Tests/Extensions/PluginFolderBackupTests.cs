using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AzureTray.Extensions;
using Xunit;

namespace AzureTray.Tests.Extensions;

// Snapshot/restore of plugins/<packageId>/ around an in-place version bump.
// The snapshot MUST live outside PluginsDir: the plugin loader watches that
// folder, so a backup copy inside it would look like a plugin appearing and
// disappearing.
[Collection(PluginBackupTemp.Name)]
public sealed class PluginFolderBackupTests : IDisposable
{
    private static readonly string BackupRoot =
        Path.Combine(Path.GetTempPath(), "AzureTray.plugin-backup");

    private readonly string _root;
    private readonly string _pluginsDir;

    public PluginFolderBackupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AzureTray.Tests.Backup", Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_pluginsDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort.
        }
    }

    [Fact]
    public void TryCreateThenRestore_ReturnsTheFolderByteIdentical()
    {
        var dll = SeedPlugin("Acme.Plugin.Foo", ("Acme.Plugin.Foo.dll", "v1-primary"), ("Dep.dll", "v1-dep"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(dll)!, "azuretray-plugin.json"), "{\"version\":\"1.0.0\"}");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(dll)!, "runtimes"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(dll)!, "runtimes", "native.dll"), "v1-native");
        var before = SnapshotContents(Path.GetDirectoryName(dll)!);

        using var backup = PluginFolderBackup.TryCreate(dll, "Acme.Plugin.Foo", NullLogger.Instance);
        Assert.NotNull(backup);

        // Simulate the install writing a new version over the folder.
        File.WriteAllText(dll, "v2-primary");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(dll)!, "New.dll"), "v2-extra");

        Assert.True(backup!.TryRestore());

        Assert.Equal(before, SnapshotContents(Path.GetDirectoryName(dll)!));
    }

    [Fact]
    public void TryRestore_RecoversDeletedFiles()
    {
        var dll = SeedPlugin("Acme.Plugin.Foo", ("Acme.Plugin.Foo.dll", "v1"), ("Dep.dll", "dep"));
        var dir = Path.GetDirectoryName(dll)!;
        var before = SnapshotContents(dir);

        using var backup = PluginFolderBackup.TryCreate(dll, "Acme.Plugin.Foo", NullLogger.Instance);

        Directory.Delete(dir, recursive: true);
        Assert.True(backup!.TryRestore());

        Assert.Equal(before, SnapshotContents(dir));
        Assert.Equal("v1", File.ReadAllText(dll));
    }

    [Fact]
    public void TryRestore_DropsFilesTheFailedInstallAdded()
    {
        var dll = SeedPlugin("Acme.Plugin.Foo", ("Acme.Plugin.Foo.dll", "v1"));
        var dir = Path.GetDirectoryName(dll)!;

        using var backup = PluginFolderBackup.TryCreate(dll, "Acme.Plugin.Foo", NullLogger.Instance);
        File.WriteAllText(Path.Combine(dir, "HalfWritten.dll"), "junk");

        Assert.True(backup!.TryRestore());

        Assert.False(File.Exists(Path.Combine(dir, "HalfWritten.dll")));
    }

    [Fact]
    public void TryCreate_WritesNothingInsidePluginsDir()
    {
        var dll = SeedPlugin("Acme.Plugin.Foo", ("Acme.Plugin.Foo.dll", "v1"));
        var pluginsBefore = SnapshotContents(_pluginsDir);
        var backupsBefore = BackupRootChildren();

        using var backup = PluginFolderBackup.TryCreate(dll, "Acme.Plugin.Foo", NullLogger.Instance);
        Assert.NotNull(backup);

        // The FileSystemWatcher on PluginsDir must see nothing at all.
        Assert.Equal(pluginsBefore, SnapshotContents(_pluginsDir));

        // …and the copy landed under %TEMP%\AzureTray.plugin-backup instead.
        var added = BackupRootChildren().Except(backupsBefore, StringComparer.OrdinalIgnoreCase).ToArray();
        var backupDir = Assert.Single(added);
        Assert.False(
            Path.GetFullPath(backupDir).StartsWith(Path.GetFullPath(_pluginsDir), StringComparison.OrdinalIgnoreCase),
            "the snapshot must not live inside the plugins folder");
        Assert.True(File.Exists(Path.Combine(backupDir, "Acme.Plugin.Foo.dll")));
    }

    [Fact]
    public void Dispose_RemovesTheTemporaryCopy()
    {
        var dll = SeedPlugin("Acme.Plugin.Foo", ("Acme.Plugin.Foo.dll", "v1"));
        var backupsBefore = BackupRootChildren();

        var backup = PluginFolderBackup.TryCreate(dll, "Acme.Plugin.Foo", NullLogger.Instance);
        var backupDir = Assert.Single(BackupRootChildren().Except(backupsBefore, StringComparer.OrdinalIgnoreCase));

        backup!.Dispose();

        Assert.False(Directory.Exists(backupDir));
        // Double dispose is harmless.
        backup.Dispose();
    }

    [Fact]
    public void Dispose_LeavesARestoredFolderInPlace()
    {
        var dll = SeedPlugin("Acme.Plugin.Foo", ("Acme.Plugin.Foo.dll", "v1"));

        var backup = PluginFolderBackup.TryCreate(dll, "Acme.Plugin.Foo", NullLogger.Instance);
        File.WriteAllText(dll, "v2");
        Assert.True(backup!.TryRestore());
        backup.Dispose();

        Assert.Equal("v1", File.ReadAllText(dll));
    }

    [Fact]
    public void TryCreate_ReturnsNullForTheLegacyTopLevelLayout()
    {
        // plugins/Legacy.dll — the containing folder is the plugins root, which
        // must never be copied or deleted wholesale.
        var dll = Path.Combine(_pluginsDir, "Legacy.dll");
        File.WriteAllText(dll, "v1");
        var backupsBefore = BackupRootChildren();

        Assert.Null(PluginFolderBackup.TryCreate(dll, "Legacy", NullLogger.Instance));

        // Nothing was copied anywhere.
        Assert.Empty(BackupRootChildrenAddedSince(backupsBefore));
    }

    [Fact]
    public void TryCreate_ReturnsNullWhenTheFolderDoesNotMatchThePackageId()
    {
        var dll = SeedPlugin("Acme.Plugin.Foo", ("Acme.Plugin.Foo.dll", "v1"));

        Assert.Null(PluginFolderBackup.TryCreate(dll, "Some.Other.Package", NullLogger.Instance));
    }

    [Fact]
    public void TryCreate_ReturnsNullWhenTheFolderIsMissing()
        => Assert.Null(PluginFolderBackup.TryCreate(
            Path.Combine(_pluginsDir, "Ghost", "Ghost.dll"), "Ghost", NullLogger.Instance));

    [Theory]
    [InlineData(null, "Acme.Plugin.Foo")]
    [InlineData("", "Acme.Plugin.Foo")]
    [InlineData("   ", "Acme.Plugin.Foo")]
    [InlineData(@"C:\plugins\Foo\Foo.dll", null)]
    [InlineData(@"C:\plugins\Foo\Foo.dll", "")]
    public void TryCreate_ReturnsNullForBlankArguments(string? dllPath, string? packageId)
        => Assert.Null(PluginFolderBackup.TryCreate(dllPath!, packageId!, NullLogger.Instance));

    [Fact]
    public void TryCreate_ThrowsOnNullLogger()
    {
        var dll = SeedPlugin("Acme.Plugin.Foo", ("Acme.Plugin.Foo.dll", "v1"));

        Assert.Throws<ArgumentNullException>(() => PluginFolderBackup.TryCreate(dll, "Acme.Plugin.Foo", null!));
    }

    [Fact]
    public void TryRestore_ReportsFailureWithoutThrowingWhenTheFolderIsLocked()
    {
        var dll = SeedPlugin("Acme.Plugin.Foo", ("Acme.Plugin.Foo.dll", "v1"));
        var logger = new RecordingLogger<PluginFolderBackupTests>();
        using var backup = PluginFolderBackup.TryCreate(dll, "Acme.Plugin.Foo", logger);

        // An open handle blocks the recursive delete restore starts with.
        using (File.Open(dll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(backup!.TryRestore());
        }

        Assert.True(
            logger.HasMessageContaining(LogLevel.Error, "Failed to restore"),
            "a failed restore has to be reported, not swallowed");
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private string SeedPlugin(string packageId, params (string Name, string Contents)[] files)
    {
        var dir = Path.Combine(_pluginsDir, packageId);
        Directory.CreateDirectory(dir);
        foreach (var (name, contents) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), contents);
        }
        return Path.Combine(dir, files[0].Name);
    }

    // Sorted "relative path = base64 contents" lines, so comparing two
    // snapshots is a byte-identity check that doesn't depend on file order.
    private static string[] SnapshotContents(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();

        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetRelativePath(dir, f)}={Convert.ToBase64String(File.ReadAllBytes(f))}")
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BackupRootChildren()
        => Directory.Exists(BackupRoot) ? Directory.GetDirectories(BackupRoot) : Array.Empty<string>();

    private static string[] BackupRootChildrenAddedSince(string[] before)
        => BackupRootChildren().Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
}
