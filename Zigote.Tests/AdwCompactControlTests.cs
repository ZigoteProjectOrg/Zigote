using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     The compact density is a contract between controls, not a per-widget knob: a property row
///     mixes an entry, a drop-down, a spin button and a button, and if any one of them resolves a
///     different height the row's controls no longer share a baseline. These measure all four
///     headlessly and pin them to the same number.
/// </summary>
public class AdwCompactControlTests
{
    private static Size Measure(Widget w, float maxWidth = 200f)
    {
        var wrapper = new ThemeProvider(ThemeData.Dark, w);
        wrapper.Measure(new Constraints(0f, maxWidth, 0f, 400f));
        wrapper.Layout(new Offset(0f, 0f));
        return new Size(w.Bounds.Width, w.Bounds.Height);
    }

    [Fact]
    public void EveryCompactControlResolvesTheSameHeight()
    {
        var heights = new[] {
            Measure(new AdwEntry { Compact = true }).Height,
            Measure(new AdwDropDown(["one", "two"]) { Compact = true }).Height,
            Measure(new AdwSpinButton(1, 0, 10) { Compact = true }).Height,
            Measure(new AdwButton("Go", () => { }) { Compact = true }).Height,
        };

        Assert.All(heights, h => Assert.Equal(AdwMetrics.CompactControlHeight, h, 3));
    }

    [Fact]
    public void EveryRegularControlResolvesTheSameHeight()
    {
        var heights = new[] {
            Measure(new AdwEntry()).Height,
            Measure(new AdwDropDown(["one", "two"])).Height,
            Measure(new AdwSpinButton(1, 0, 10)).Height,
            Measure(new AdwButton("Go", () => { })).Height,
        };

        Assert.All(heights, h => Assert.Equal(AdwMetrics.ButtonHeight, h, 3));
        Assert.True(AdwMetrics.CompactControlHeight < AdwMetrics.ButtonHeight);
    }

    /// <summary>
    ///     The search entry's clear button and the password entry's reveal eye are square insets
    ///     cut from the trailing edge — they have to follow the resolved height, or a compact search
    ///     field reserves a 34px gutter inside a 28px box and the glyph sits outside it.
    /// </summary>
    [Fact]
    public void CompactSearchEntryKeepsItsTrailingButtonSquare()
    {
        var search = new AdwSearchEntry { Compact = true, Text = "x" };
        var size = Measure(search);
        Assert.Equal(AdwMetrics.CompactControlHeight, size.Height, 3);

        // Press the centre of the trailing square. Sized off EntryHeight instead, that box would
        // start 34px from the right of a 28px-tall field and this press would land in the text.
        search.OnPointerDown(new Offset(size.Width - size.Height / 2f, size.Height / 2f));
        Assert.Equal("", search.Text);
    }

    /// <summary>
    ///     libadwaita pads buttons by role: <c>.text-button</c> 17px, <c>.image-text-button</c> 9px,
    ///     <c>.pill</c> 32px with a 44px height. Sizing every button like a text-button (which this
    ///     kit used to) leaves icon+label buttons visibly loose and pills too short.
    /// </summary>
    [Fact]
    public void ButtonPaddingAndHeightFollowTheirLibadwaitaRole()
    {
        var label = Measure(new AdwButton("Save", () => { }), 400f);
        var iconText = Measure(
            new AdwButton("Save", () => { }) { IconName = Icons.Save },
            400f
        );
        var pill = Measure(new AdwButton("Save", () => { }) { Pill = true }, 400f);

        Assert.Equal(AdwMetrics.ButtonHeight, label.Height, 3);
        Assert.Equal(AdwMetrics.PillHeight, pill.Height, 3);

        // Same label; the icon adds its glyph + gap but the frame tightens by 8px a side, so an
        // icon+label button is NOT simply the text button plus the icon's width.
        var tightening = (AdwMetrics.ButtonPaddingX - AdwMetrics.ImageTextPaddingX) * 2f;
        Assert.Equal(label.Width + AdwMetrics.IconSize + 6f - tightening, iconText.Width, 1);
        Assert.True(pill.Width > label.Width);
    }

    /// <summary>
    ///     Metrics taken straight from libadwaita's stylesheet. They are single constants, which is
    ///     exactly why they drift: nothing fails when one is wrong, the widget just stops looking
    ///     like GNOME. Sources are quoted per line.
    /// </summary>
    [Fact]
    public void CoreMetricsMatchTheLibadwaitaStylesheet()
    {
        // _buttons.scss: button { min-height: 24px; padding: 5px 10px } → 34; .text-button 17px.
        Assert.Equal(34f, AdwMetrics.ButtonHeight);
        Assert.Equal(17f, AdwMetrics.ButtonPaddingX);
        Assert.Equal(9f, AdwMetrics.ImageTextPaddingX); // .image-text-button
        Assert.Equal(32f, AdwMetrics.PillPaddingX); // .pill { padding: 10px 32px }
        Assert.Equal(44f, AdwMetrics.PillHeight); // 24 + 10 + 10

        // _entries.scss: entry { min-height: 34px }
        Assert.Equal(34f, AdwMetrics.EntryHeight);
        // _header-bar.scss: headerbar { min-height: 47px }
        Assert.Equal(47f, AdwMetrics.HeaderBarHeight);
        // _menus.scss: modelbutton { min-height: 32px }
        Assert.Equal(32f, AdwMetrics.MenuRowHeight);
        // _lists.scss: row > box.header { min-height: 50px }, .rich-list padding 8px 12px
        Assert.Equal(50f, AdwMetrics.RowMinHeight);
        Assert.Equal(12f, AdwMetrics.RowPaddingX);
        Assert.Equal(8f, AdwMetrics.RowPaddingY);

        // _checks.scss: check { min-height: 14px; padding: 3px } → 20
        Assert.Equal(20f, AdwMetrics.CheckSize);
        // _switch.scss: slider 20px + 3px padding either side → 26
        Assert.Equal(26f, AdwMetrics.SwitchHeight);
        Assert.Equal(20f, AdwMetrics.SwitchHeight - 6f);
        // _scale.scss: trough { min-height: 10px }, slider { min-width: 20px }
        Assert.Equal(10f, AdwMetrics.SliderTrack);
        Assert.Equal(20f, AdwMetrics.SliderKnob);
        // _progress-bar.scss: trough/progress { min-height: 8px }
        Assert.Equal(8f, AdwMetrics.ProgressBarHeight);
        // _toggle-group.scss: --group-padding: 3px; .round radius 17px
        Assert.Equal(3f, AdwMetrics.ToggleGroupPadding);
        Assert.Equal(17f, AdwMetrics.RoundToggleRadius);
        // _sidebars.scss: .navigation-sidebar row { min-height: 36px; padding: 0 8px; margin ...2px }
        Assert.Equal(8f, AdwMetrics.SidebarRowPaddingX);
        Assert.Equal(2f, AdwMetrics.SidebarRowGap);
    }
}
