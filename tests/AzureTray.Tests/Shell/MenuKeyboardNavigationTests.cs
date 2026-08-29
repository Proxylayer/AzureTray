using System.Collections.Generic;
using AzureTray.Plugin.Contracts;
using AzureTray.Shell;
using Xunit;

namespace AzureTray.Tests.Shell;

public sealed class MenuKeyboardNavigationTests
{
    private static PluginMenuItem Item(string text, bool enabled = true) =>
        new(text, IsEnabled: enabled);

    // ---- IsSelectable ----

    [Fact]
    public void IsSelectable_EnabledItem_True()
    {
        var items = new List<PluginMenuItem> { Item("A") };

        Assert.True(MenuKeyboardNavigation.IsSelectable(items, 0));
    }

    [Fact]
    public void IsSelectable_Separator_False()
    {
        var items = new List<PluginMenuItem> { PluginMenuItem.Separator };

        Assert.False(MenuKeyboardNavigation.IsSelectable(items, 0));
    }

    [Fact]
    public void IsSelectable_DisabledItem_False()
    {
        var items = new List<PluginMenuItem> { Item("A", enabled: false) };

        Assert.False(MenuKeyboardNavigation.IsSelectable(items, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void IsSelectable_IndexOutOfRange_False(int index)
    {
        var items = new List<PluginMenuItem> { Item("A") };

        Assert.False(MenuKeyboardNavigation.IsSelectable(items, index));
    }

    // ---- FindNextSelectableIndex: skipping ----

    [Fact]
    public void FindNext_Down_SkipsSeparator()
    {
        var items = new List<PluginMenuItem> { Item("A"), PluginMenuItem.Separator, Item("B") };

        Assert.Equal(2, MenuKeyboardNavigation.FindNextSelectableIndex(items, 0, +1));
    }

    [Fact]
    public void FindNext_Down_SkipsDisabled()
    {
        var items = new List<PluginMenuItem> { Item("A"), Item("B", enabled: false), Item("C") };

        Assert.Equal(2, MenuKeyboardNavigation.FindNextSelectableIndex(items, 0, +1));
    }

    [Fact]
    public void FindNext_Up_SkipsSeparatorAndDisabled()
    {
        var items = new List<PluginMenuItem>
        {
            Item("A"),
            PluginMenuItem.Separator,
            Item("B", enabled: false),
            Item("C"),
        };

        Assert.Equal(0, MenuKeyboardNavigation.FindNextSelectableIndex(items, 3, -1));
    }

    // ---- FindNextSelectableIndex: wrapping ----

    [Fact]
    public void FindNext_DownFromLast_WrapsToFirstSelectable()
    {
        var items = new List<PluginMenuItem> { Item("A"), Item("B"), Item("C") };

        Assert.Equal(0, MenuKeyboardNavigation.FindNextSelectableIndex(items, 2, +1));
    }

    [Fact]
    public void FindNext_UpFromFirst_WrapsToLastSelectable()
    {
        var items = new List<PluginMenuItem> { Item("A"), Item("B"), Item("C") };

        Assert.Equal(2, MenuKeyboardNavigation.FindNextSelectableIndex(items, 0, -1));
    }

    [Fact]
    public void FindNext_DownFromLast_WrapSkipsLeadingSeparator()
    {
        var items = new List<PluginMenuItem> { PluginMenuItem.Separator, Item("A"), Item("B") };

        Assert.Equal(1, MenuKeyboardNavigation.FindNextSelectableIndex(items, 2, +1));
    }

    // ---- FindNextSelectableIndex: no-selection entry (-1) ----

    [Fact]
    public void FindNext_NoSelection_Down_LandsOnFirstSelectable()
    {
        var items = new List<PluginMenuItem> { PluginMenuItem.Separator, Item("A"), Item("B") };

        Assert.Equal(1, MenuKeyboardNavigation.FindNextSelectableIndex(items, -1, +1));
    }

    [Fact]
    public void FindNext_NoSelection_Up_LandsOnLastSelectable()
    {
        var items = new List<PluginMenuItem> { Item("A"), Item("B"), PluginMenuItem.Separator };

        Assert.Equal(1, MenuKeyboardNavigation.FindNextSelectableIndex(items, -1, -1));
    }

    // ---- FindNextSelectableIndex: degenerate lists ----

    [Fact]
    public void FindNext_EmptyList_ReturnsMinusOne()
    {
        var items = new List<PluginMenuItem>();

        Assert.Equal(-1, MenuKeyboardNavigation.FindNextSelectableIndex(items, -1, +1));
    }

    [Fact]
    public void FindNext_AllUnselectable_ReturnsMinusOne()
    {
        var items = new List<PluginMenuItem>
        {
            PluginMenuItem.Separator,
            Item("A", enabled: false),
            PluginMenuItem.Separator,
        };

        Assert.Equal(-1, MenuKeyboardNavigation.FindNextSelectableIndex(items, -1, +1));
        Assert.Equal(-1, MenuKeyboardNavigation.FindNextSelectableIndex(items, 1, -1));
    }

    [Fact]
    public void FindNext_ZeroDirection_ReturnsMinusOne()
    {
        var items = new List<PluginMenuItem> { Item("A") };

        Assert.Equal(-1, MenuKeyboardNavigation.FindNextSelectableIndex(items, 0, 0));
    }

    [Fact]
    public void FindNext_SingleSelectableItem_WrapsOntoItself()
    {
        var items = new List<PluginMenuItem> { Item("A") };

        Assert.Equal(0, MenuKeyboardNavigation.FindNextSelectableIndex(items, 0, +1));
        Assert.Equal(0, MenuKeyboardNavigation.FindNextSelectableIndex(items, 0, -1));
    }

    // ---- FindFirstSelectableIndex ----

    [Fact]
    public void FindFirst_SkipsLeadingSeparatorAndDisabled()
    {
        var items = new List<PluginMenuItem>
        {
            PluginMenuItem.Separator,
            Item("A", enabled: false),
            Item("B"),
        };

        Assert.Equal(2, MenuKeyboardNavigation.FindFirstSelectableIndex(items));
    }

    [Fact]
    public void FindFirst_EmptyList_ReturnsMinusOne()
    {
        Assert.Equal(-1, MenuKeyboardNavigation.FindFirstSelectableIndex(new List<PluginMenuItem>()));
    }

    [Fact]
    public void FindFirst_AllUnselectable_ReturnsMinusOne()
    {
        var items = new List<PluginMenuItem> { PluginMenuItem.Separator, Item("A", enabled: false) };

        Assert.Equal(-1, MenuKeyboardNavigation.FindFirstSelectableIndex(items));
    }
}
