using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using AzureTray.Shell;
using Xunit;

namespace AzureTray.Tests.Shell;

public sealed class HighContrastThemeTests
{
    /// <summary>
    /// Extracts every <c>x:Key="Brush.*"</c> definition from Theme.xaml by
    /// text, so the sweep stays headless (no XAML loader / Application).
    /// </summary>
    private static List<string> ThemeBrushKeys()
    {
        var xaml = File.ReadAllText(ThemeXamlPath());
        return Regex.Matches(xaml, "x:Key=\"(Brush\\.[^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    private static string ThemeXamlPath()
    {
        // Walk up from the test output directory to the repo root (marked by
        // the solution file), then down to the theme resource.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AzureTray.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "AzureTray", "Resources", "Theme.xaml");
        Assert.True(File.Exists(path), $"Theme.xaml not found at {path}");
        return path;
    }

    [Fact]
    public void MapBrushKeys_CoversEveryBrushKeyDefinedInThemeXaml()
    {
        var themeKeys = ThemeBrushKeys();
        Assert.NotEmpty(themeKeys);

        var mapped = HighContrastTheme.MapBrushKeys();
        var unmapped = themeKeys.Where(k => !mapped.ContainsKey(k)).ToList();

        // Guards future theme tokens being forgotten in the High Contrast map.
        Assert.True(unmapped.Count == 0,
            "Theme.xaml Brush keys missing from HighContrastTheme.MapBrushKeys(): "
            + string.Join(", ", unmapped));
    }

    [Fact]
    public void MapBrushKeys_HasNoKeysAbsentFromThemeXaml()
    {
        // The inverse sweep: an HC mapping for a key Theme.xaml no longer
        // defines is dead weight (or a typo that silently maps nothing).
        var themeKeys = new HashSet<string>(ThemeBrushKeys(), StringComparer.Ordinal);
        var orphans = HighContrastTheme.MapBrushKeys().Keys
            .Where(k => !themeKeys.Contains(k))
            .ToList();

        Assert.True(orphans.Count == 0,
            "HighContrastTheme.MapBrushKeys() contains keys not defined in Theme.xaml: "
            + string.Join(", ", orphans));
    }

    [Fact]
    public void MapBrushKeys_ThemeXamlDefinesNoDuplicateBrushKeys()
    {
        var duplicates = ThemeBrushKeys()
            .GroupBy(k => k, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            "Theme.xaml defines duplicate Brush keys: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void CreateDictionary_ContainsExactlyTheMappedKeys()
    {
        var mapped = HighContrastTheme.MapBrushKeys();
        var dictionary = HighContrastTheme.CreateDictionary();

        Assert.Equal(mapped.Count, dictionary.Count);
        foreach (var key in mapped.Keys)
        {
            Assert.True(dictionary.Contains(key), $"CreateDictionary() is missing '{key}'");
        }
    }

    [Fact]
    public void CreateDictionary_EveryValueIsAnUnfrozenSolidColorBrushWithMappedColor()
    {
        var mapped = HighContrastTheme.MapBrushKeys();
        var dictionary = HighContrastTheme.CreateDictionary();

        foreach (var (key, expectedColor) in mapped)
        {
            var brush = Assert.IsType<SolidColorBrush>(dictionary[key]);
            Assert.Equal(expectedColor, brush.Color);
            // Pinned: brushes are deliberately UNFROZEN because a theme
            // storyboard animates a brush's Color in place (animating a
            // frozen brush throws). Do not "optimise" by freezing.
            Assert.False(brush.IsFrozen, $"'{key}' brush must stay unfrozen");
        }
    }
}
