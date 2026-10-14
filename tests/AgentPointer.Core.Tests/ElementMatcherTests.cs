using System.Linq;
using AgentPointer.Core.Targeting;
using Xunit;

public class ElementMatcherTests
{
    private static ElementInfo E(
        string name,
        string role = "button",
        string? id = null,
        int width = 100,
        int height = 30,
        bool enabled = true,
        bool offscreen = false) =>
        new(name, role, id, 0, 0, width, height, enabled, offscreen);

    [Fact]
    public void Rank_OrdersExactThenPrefixThenContains()
    {
        var items = new[] { E("Save and close"), E("Save as"), E("Save"), E("Unsaved changes") };

        var ranked = ElementMatcher.Rank(items, "Save", null);

        Assert.Equal(new[] { "Save", "Save as", "Save and close" }, ranked.Select(r => r.Name));
    }

    [Fact]
    public void Rank_MatchesAutomationIdExactly()
    {
        var items = new[] { E("Weiter"), E("Ok") };

        var ranked = ElementMatcher.Rank(items, "Weiter", null);

        Assert.Equal("Weiter", Assert.Single(ranked).Name);
    }

    [Fact]
    public void Rank_MatchesByAutomationIdWhenNameDiffers()
    {
        var items = new[] { E("Ok"), E("Weiter", id: "Save") };

        var ranked = ElementMatcher.Rank(items, "Save", null);

        Assert.Equal("Weiter", Assert.Single(ranked).Name);
    }

    [Theory]
    [InlineData("AXButton", "button")]
    [InlineData("axbutton", "button")]
    [InlineData("ControlType.Button", "button")]
    [InlineData("link", "hyperlink")]
    [InlineData("ControlType.Edit", "edit")]
    [InlineData("TextField", "edit")]
    [InlineData("textbox", "edit")]
    [InlineData("StaticText", "text")]
    [InlineData("label", "text")]
    [InlineData("tab", "tabitem")]
    [InlineData("popupbutton", "combobox")]
    [InlineData("dropdown", "combobox")]
    [InlineData("row", "dataitem")]
    [InlineData("cell", "dataitem")]
    [InlineData("menu item", "menuitem")]
    [InlineData("AXMenuItem", "menuitem")]
    [InlineData("CheckBox", "checkbox")]
    [InlineData("", "")]
    public void NormalizeRole_AppliesPrefixesAndAliases(string raw, string expected) =>
        Assert.Equal(expected, ElementMatcher.NormalizeRole(raw));

    [Fact]
    public void Rank_FiltersByNormalizedRole()
    {
        var items = new[] { E("Allow", "AXButton"), E("Allow", "link") };

        var byButton = ElementMatcher.Rank(items, "Allow", "button");
        Assert.Equal("AXButton", Assert.Single(byButton).Role);

        var byLink = ElementMatcher.Rank(items, "Allow", "hyperlink");
        Assert.Equal("link", Assert.Single(byLink).Role);
    }

    [Fact]
    public void Rank_PrefersOnscreenThenEnabledThenSmallerArea()
    {
        var items = new[]
        {
            E("Allow", offscreen: true),
            E("Allow", enabled: false),
            E("Allow", width: 400, height: 100),
            E("Allow", width: 100, height: 20),
        };

        var ranked = ElementMatcher.Rank(items, "Allow", "button");

        Assert.Equal(20, ranked[0].Height);
        Assert.False(ranked[1].Enabled);
        Assert.True(ranked[^1].Offscreen);
    }

    [Fact]
    public void Rank_SkipsZeroSizeItemsAndReturnsEmptyWhenNothingMatches()
    {
        Assert.Empty(ElementMatcher.Rank(new[] { E("Allow", width: 0, height: 0) }, "Allow", null));
        Assert.Empty(ElementMatcher.Rank(new[] { E("Allow") }, "Cancel", null));
    }

    [Fact]
    public void Suggestions_ReturnsClosestFirst()
    {
        var items = new[] { E("Cancel"), E("Save as"), E("Save") };

        var suggestions = ElementMatcher.Suggestions(items, "Save");

        Assert.Equal("Save", suggestions[0]);
        Assert.Equal("Save as", suggestions[1]);
        Assert.Equal("Cancel", suggestions[^1]);
    }

    [Fact]
    public void Suggestions_AreDistinctAndRespectMax()
    {
        var items = new[] { E("Save"), E("save"), E("Save as"), E("Cancel") };

        var suggestions = ElementMatcher.Suggestions(items, "Save", max: 2);

        Assert.Equal(2, suggestions.Count);
        Assert.Equal(2, suggestions.Distinct().Count());
    }
}
