using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Widgets;

/// <summary>
///     A live frames-per-second readout for the toolbar. Self-painting: it reads the app's current
///     <see cref="App.DeltaTime" /> on every paint and tints green / amber / red by frame rate. Like
///     the viewport badge, it refreshes whenever the shell repaints (continuously during Play).
/// </summary>
public sealed class FpsChip : Widget
{
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(new Size(56f, 22f));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        var dt = App.Active?.DeltaTime ?? 0f;
        var fps = dt > 0f ? 1f / dt : 0f;
        var color = fps >= 50f ? _theme.Success : fps >= 25f ? _theme.Warning : _theme.Error;

        var text = $"{fps:F0} fps";
        var fs = _theme.FontSizeCaption;
        var tw = TextMeasure.Width(text, fs, FontWeight.Medium);
        var x = Bounds.Right - tw - 2f;
        var y = Bounds.Y + (Bounds.Height - fs) / 2f + fs * 0.8f;
        paint.AddText(
            text,
            x,
            y,
            color,
            fs,
            fontWeight: FontWeight.Medium
        );
    }
}
