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
        var slider = new AdwSlider(value: 0f, min: -12f, max: 12f) { Vertical = vertical };
        var wrapper = new ThemeProvider(data: ThemeData.Dark, child: slider);
        wrapper.Measure(Constraints.Tight(width: w, height: h));
        wrapper.Layout(new Offset(x: 0f, y: 0f));
        return slider;
    }

    [Fact]
    public void Vertical_TopIsMax_BottomIsMin()
    {
        float changed = 0f;
        var slider = Laid(vertical: true, w: 40f, h: 200f);
        slider.OnChanged = v => changed = v;

        slider.OnPointerDown(new Offset(x: 20f, y: 0f));
        Assert.Equal(expected: 12f, actual: changed, precision: 3);
        Assert.Equal(expected: 12f, actual: slider.Value, precision: 3);

        slider.OnPointerMove(new Offset(x: 20f, y: 200f));
        Assert.Equal(expected: -12f, actual: changed, precision: 3);

        slider.OnPointerMove(new Offset(x: 20f, y: 100f));
        Assert.Equal(expected: 0f, actual: changed, precision: 1);
    }

    [Fact]
    public void Horizontal_LeftIsMin_RightIsMax()
    {
        float changed = 0f;
        var slider = Laid(vertical: false, w: 200f, h: 40f);
        slider.OnChanged = v => changed = v;

        slider.OnPointerDown(new Offset(x: 200f, y: 20f));
        Assert.Equal(expected: 12f, actual: changed, precision: 3);

        slider.OnPointerMove(new Offset(x: 0f, y: 20f));
        Assert.Equal(expected: -12f, actual: changed, precision: 3);
    }
}
