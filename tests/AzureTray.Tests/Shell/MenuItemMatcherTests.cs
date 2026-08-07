using System.Collections.Generic;
using AzureTray.Plugin.Contracts;
using AzureTray.Shell;
using Xunit;

namespace AzureTray.Tests.Shell;

public sealed class MenuItemMatcherTests
{
    // ---- FindRefreshedParent: Key matching (rule 1) ----

    [Fact]
    public void FindRefreshedParent_KeyMatch_WinsOverTextMatchElsewhere()
    {
        var oldItem = new PluginMenuItem("Pending Approvals (3)") { Key = "pending" };
        var keyMatchDifferentText = new PluginMenuItem("Pending Approvals (5)") { Key = "pending" };
        var textMatchDifferentKey = new PluginMenuItem("Pending Approvals (3)") { Key = "other" };
        var textMatchNoKey = new PluginMenuItem("Pending Approvals (3)");
        var newItems = new List<PluginMenuItem>
        {
            textMatchNoKey,
            textMatchDifferentKey,
            keyMatchDifferentText,
        };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Same(keyMatchDifferentText, result);
    }

    [Fact]
    public void FindRefreshedParent_OldHasKey_KeyGone_ReturnsNullDespiteExactTextCandidate()
    {
        var oldItem = new PluginMenuItem("Pending Approvals") { Key = "pending" };
        var newItems = new List<PluginMenuItem>
        {
            new("Pending Approvals"),                       // exact text, no key
            new("Pending Approvals") { Key = "different" }, // exact text, other key
        };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Null(result);
    }

    // ---- FindRefreshedParent: exact-text fallback (rule 2) ----

    [Fact]
    public void FindRefreshedParent_NoKeys_ExactTextMatch()
    {
        var oldItem = new PluginMenuItem("Active Roles");
        var expected = new PluginMenuItem("Active Roles");
        var newItems = new List<PluginMenuItem>
        {
            new("Eligible Roles"),
            expected,
            new("Settings"),
        };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Same(expected, result);
    }

    [Fact]
    public void FindRefreshedParent_UnkeyedOldItem_DoesNotMatchKeyedCandidate()
    {
        var oldItem = new PluginMenuItem("Active Roles");
        var newItems = new List<PluginMenuItem>
        {
            new PluginMenuItem("Active Roles") { Key = "active" },
        };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Null(result);
    }

    // ---- FindRefreshedParent: count-suffix tolerance (rule 3) ----

    [Fact]
    public void FindRefreshedParent_CountSuffixChanged_Matches()
    {
        var oldItem = new PluginMenuItem("Pending Approvals (3)");
        var expected = new PluginMenuItem("Pending Approvals (5)");
        var newItems = new List<PluginMenuItem> { new("Other"), expected };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Same(expected, result);
    }

    [Fact]
    public void FindRefreshedParent_CountSuffixAppeared_Matches()
    {
        // Both sides are stripped, so "Pending Approvals" (no suffix)
        // matches a new "Pending Approvals (2)".
        var oldItem = new PluginMenuItem("Pending Approvals");
        var expected = new PluginMenuItem("Pending Approvals (2)");
        var newItems = new List<PluginMenuItem> { expected };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Same(expected, result);
    }

    [Fact]
    public void FindRefreshedParent_CountSuffixRemoved_Matches()
    {
        var oldItem = new PluginMenuItem("Pending Approvals (2)");
        var expected = new PluginMenuItem("Pending Approvals");
        var newItems = new List<PluginMenuItem> { expected };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Same(expected, result);
    }

    [Fact]
    public void FindRefreshedParent_ExactTextMatch_PreferredOverSuffixStrippedMatch()
    {
        var oldItem = new PluginMenuItem("Pending Approvals (3)");
        var exact = new PluginMenuItem("Pending Approvals (3)");
        var stripped = new PluginMenuItem("Pending Approvals (9)");
        var newItems = new List<PluginMenuItem> { stripped, exact };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Same(exact, result);
    }

    // ---- FindRefreshedParent: separators and no match ----

    [Fact]
    public void FindRefreshedParent_SeparatorCandidates_NeverMatch()
    {
        // A separator has empty Text and no Key; an old item with empty Text
        // must not be matched to it.
        var oldItem = new PluginMenuItem(string.Empty);
        var newItems = new List<PluginMenuItem>
        {
            PluginMenuItem.Separator,
            new(string.Empty, IsSeparator: true),
        };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Null(result);
    }

    [Fact]
    public void FindRefreshedParent_KeyedOldItem_SeparatorWithSameTextNeverMatches()
    {
        var oldItem = new PluginMenuItem("Divider") { Key = "k" };
        var newItems = new List<PluginMenuItem>
        {
            new PluginMenuItem("Divider", IsSeparator: true) { Key = "k" },
        };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Null(result);
    }

    [Fact]
    public void FindRefreshedParent_NoCounterpart_ReturnsNull()
    {
        var oldItem = new PluginMenuItem("Gone");
        var newItems = new List<PluginMenuItem> { new("Something Else"), new("Other (2)") };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, newItems);

        Assert.Null(result);
    }

    [Fact]
    public void FindRefreshedParent_EmptyNewItems_ReturnsNull()
    {
        var oldItem = new PluginMenuItem("Anything") { Key = "k" };

        var result = MenuItemMatcher.FindRefreshedParent(oldItem, new List<PluginMenuItem>());

        Assert.Null(result);
    }

    // ---- StripCountSuffix ----

    [Theory]
    [InlineData("Name (12)", "Name")]           // canonical count suffix
    [InlineData("Name(12)", "Name")]            // no space before parens — still stripped
    [InlineData("Name (a1)", "Name (a1)")]      // non-digit inside parens — unchanged
    [InlineData("Name ()", "Name ()")]          // empty parens — unchanged
    [InlineData("Name (12) ", "Name")]          // trailing whitespace trimmed, then stripped
    [InlineData("Name ", "Name")]               // trailing whitespace trimmed even without suffix
    [InlineData("Name", "Name")]                // no suffix — unchanged
    [InlineData("", "")]                        // empty input
    [InlineData("(3)", "")]                     // suffix only — strips to empty
    [InlineData("Name (1) (2)", "Name (1)")]    // only the trailing suffix is stripped
    public void StripCountSuffix_ReturnsExpected(string input, string expected)
    {
        Assert.Equal(expected, MenuItemMatcher.StripCountSuffix(input));
    }
}
