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
        _size = c.Constrain(new Size(width: 56f, height: 22f));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        float dt = App.Active?.DeltaTime ?? 0f;
        float fps = dt > 0f ? 1f / dt : 0f;
        var color = fps >= 50f ? _theme.Success : fps >= 25f ? _theme.Warning : _theme.Error;

        string text = $"{fps:F0} fps";
        float fs = _theme.FontSizeCaption;
        float tw = TextMeasure.Width(text: text, fontSize: fs, weight: FontWeight.Medium);
        float x = Bounds.Right - tw - 2f;
        float y = Bounds.Y + ((Bounds.Height - fs) / 2f) + (fs * 0.8f);
        paint.AddText(
            text: text,
            baselineX: x,
            baselineY: y,
            color: color,
            fontSize: fs,
            fontWeight: FontWeight.Medium
        );
    }
}
