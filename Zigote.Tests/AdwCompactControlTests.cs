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
        var wrapper = new ThemeProvider(data: ThemeData.Dark, child: w);
        wrapper.Measure(
            new Constraints(
                minWidth: 0f,
                maxWidth: maxWidth,
                minHeight: 0f,
                maxHeight: 400f
            )
        );
        wrapper.Layout(new Offset(x: 0f, y: 0f));
        return new Size(width: w.Bounds.Width, height: w.Bounds.Height);
    }

    [Fact]
    public void EveryCompactControlResolvesTheSameHeight()
    {
        float[] heights = new[] {
            Measure(new AdwEntry { Compact = true }).Height,
            Measure(new AdwDropDown(["one", "two"]) { Compact = true }).Height,
            Measure(new AdwSpinButton(value: 1, min: 0, max: 10) { Compact = true }).Height,
            Measure(new AdwButton(label: "Go", onPressed: () => { }) { Compact = true }).Height,
        };

        Assert.All(
            collection: heights,
            action: h => Assert.Equal(
                expected: AdwMetrics.CompactControlHeight,
                actual: h,
                precision: 3
            )
        );
    }

    [Fact]
    public void EveryRegularControlResolvesTheSameHeight()
    {
        float[] heights = new[] {
            Measure(new AdwEntry()).Height,
            Measure(new AdwDropDown(["one", "two"])).Height,
            Measure(new AdwSpinButton(value: 1, min: 0, max: 10)).Height,
            Measure(new AdwButton(label: "Go", onPressed: () => { })).Height,
        };

        Assert.All(
            collection: heights,
            action: h => Assert.Equal(expected: AdwMetrics.ButtonHeight, actual: h, precision: 3)
        );
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
        var search = new AdwSearchEntry {
            Compact = true,
            Text = "x",
        };
        var size = Measure(search);
        Assert.Equal(expected: AdwMetrics.CompactControlHeight, actual: size.Height, precision: 3);

        // Press the centre of the trailing square. Sized off EntryHeight instead, that box would
        // start 34px from the right of a 28px-tall field and this press would land in the text.
        search.OnPointerDown(new Offset(x: size.Width - (size.Height / 2f), y: size.Height / 2f));
        Assert.Equal(expected: "", actual: search.Text);
    }

    /// <summary>
    ///     libadwaita pads buttons by role: <c>.text-button</c> 17px, <c>.image-text-button</c> 9px,
    ///     <c>.pill</c> 32px with a 44px height. Sizing every button like a text-button (which this
    ///     kit used to) leaves icon+label buttons visibly loose and pills too short.
    /// </summary>
    [Fact]
    public void ButtonPaddingAndHeightFollowTheirLibadwaitaRole()
    {
        var label = Measure(w: new AdwButton(label: "Save", onPressed: () => { }), maxWidth: 400f);
        var iconText = Measure(
            w: new AdwButton(label: "Save", onPressed: () => { }) { IconName = Icons.Save },
            maxWidth: 400f
        );
        var pill = Measure(
            w: new AdwButton(label: "Save", onPressed: () => { }) { Pill = true },
            maxWidth: 400f
        );

        Assert.Equal(expected: AdwMetrics.ButtonHeight, actual: label.Height, precision: 3);
        Assert.Equal(expected: AdwMetrics.PillHeight, actual: pill.Height, precision: 3);

        // Same label; the icon adds its glyph + gap but the frame tightens by 8px a side, so an
        // icon+label button is NOT simply the text button plus the icon's width.
        float tightening = (AdwMetrics.ButtonPaddingX - AdwMetrics.ImageTextPaddingX) * 2f;
        Assert.Equal(
            expected: label.Width + AdwMetrics.IconSize + 6f - tightening,
            actual: iconText.Width,
            precision: 1
        );
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
        Assert.Equal(expected: 34f, actual: AdwMetrics.ButtonHeight);
        Assert.Equal(expected: 17f, actual: AdwMetrics.ButtonPaddingX);
        Assert.Equal(expected: 9f, actual: AdwMetrics.ImageTextPaddingX); // .image-text-button
        Assert.Equal(
            expected: 32f,
            actual: AdwMetrics.PillPaddingX
        ); // .pill { padding: 10px 32px }
        Assert.Equal(expected: 44f, actual: AdwMetrics.PillHeight); // 24 + 10 + 10

        // _entries.scss: entry { min-height: 34px }
        Assert.Equal(expected: 34f, actual: AdwMetrics.EntryHeight);
        // _header-bar.scss: headerbar { min-height: 47px }
        Assert.Equal(expected: 47f, actual: AdwMetrics.HeaderBarHeight);
        // _menus.scss: modelbutton { min-height: 32px }
        Assert.Equal(expected: 32f, actual: AdwMetrics.MenuRowHeight);
        // _lists.scss: row > box.header { min-height: 50px }, .rich-list padding 8px 12px
        Assert.Equal(expected: 50f, actual: AdwMetrics.RowMinHeight);
        Assert.Equal(expected: 12f, actual: AdwMetrics.RowPaddingX);
        Assert.Equal(expected: 8f, actual: AdwMetrics.RowPaddingY);

        // _checks.scss: check { min-height: 14px; padding: 3px } → 20
        Assert.Equal(expected: 20f, actual: AdwMetrics.CheckSize);
        // _switch.scss: slider 20px + 3px padding either side → 26
        Assert.Equal(expected: 26f, actual: AdwMetrics.SwitchHeight);
        Assert.Equal(expected: 20f, actual: AdwMetrics.SwitchHeight - 6f);
        // _scale.scss: trough { min-height: 10px }, slider { min-width: 20px }
        Assert.Equal(expected: 10f, actual: AdwMetrics.SliderTrack);
        Assert.Equal(expected: 20f, actual: AdwMetrics.SliderKnob);
        // _progress-bar.scss: trough/progress { min-height: 8px }
        Assert.Equal(expected: 8f, actual: AdwMetrics.ProgressBarHeight);
        // _toggle-group.scss: --group-padding: 3px; .round radius 17px
        Assert.Equal(expected: 3f, actual: AdwMetrics.ToggleGroupPadding);
        Assert.Equal(expected: 17f, actual: AdwMetrics.RoundToggleRadius);
        // _sidebars.scss: .navigation-sidebar row { min-height: 36px; padding: 0 8px; margin ...2px }
        Assert.Equal(expected: 8f, actual: AdwMetrics.SidebarRowPaddingX);
        Assert.Equal(expected: 2f, actual: AdwMetrics.SidebarRowGap);
    }
}
