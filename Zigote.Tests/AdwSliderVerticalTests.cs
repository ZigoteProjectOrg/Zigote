using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     <see cref="AdwSlider.Vertical" /> only swaps an axis, but it swaps a direction with it: a
///     fader's maximum is at the <i>top</i>. These drive the slider headlessly and check both ends.
/// </summary>
public class AdwSliderVerticalTests
{
    private static AdwSlider Laid(bool vertical, float w, float h)
    {
        var slider = new AdwSlider(0f, -12f, 12f) { Vertical = vertical };
        var wrapper = new ThemeProvider(ThemeData.Dark, slider);
        wrapper.Measure(Constraints.Tight(w, h));
        wrapper.Layout(new Offset(0f, 0f));
        return slider;
    }

    [Fact]
    public void Vertical_TopIsMax_BottomIsMin()
    {
        var changed = 0f;
        var slider = Laid(true, 40f, 200f);
        slider.OnChanged = v => changed = v;

        slider.OnPointerDown(new Offset(20f, 0f));
        Assert.Equal(12f, changed, 3);
        Assert.Equal(12f, slider.Value, 3);

        slider.OnPointerMove(new Offset(20f, 200f));
        Assert.Equal(-12f, changed, 3);

        slider.OnPointerMove(new Offset(20f, 100f));
        Assert.Equal(0f, changed, 1);
    }

    [Fact]
    public void Horizontal_LeftIsMin_RightIsMax()
    {
        var changed = 0f;
        var slider = Laid(false, 200f, 40f);
        slider.OnChanged = v => changed = v;

        slider.OnPointerDown(new Offset(200f, 20f));
        Assert.Equal(12f, changed, 3);

        slider.OnPointerMove(new Offset(0f, 20f));
        Assert.Equal(-12f, changed, 3);
    }
}