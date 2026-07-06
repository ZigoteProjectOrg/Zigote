using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     Wraps any widget and shows a tooltip bubble after hovering for ~0.7 s.
///     UiApp reads <see cref="TooltipText" /> from the hovered widget automatically.
/// </summary>
public sealed class Tooltip(string message, Widget? child = null) : StatelessWidget
{
    public Widget? Child { get; set; } = child;
    public string Message { get; set; } = message;

    public override string? TooltipText => Message;

    protected override Widget Build(BuildContext context)
    {
        return Child ?? new SizedBox();
    }
}

/// <summary>
///     Full-screen overlay that positions a tooltip bubble near a cursor position. The bubble itself
///     is
///     a composed <see cref="DecoratedBox" /> + <see cref="Padding" /> + <see cref="Label" />; this
///     widget only computes where to place it and stays transparent to hit-testing.
///     Managed by UiApp — do not add manually.
/// </summary>
public sealed class TooltipBubble : Widget
{
    private readonly DecoratedBox _bubble;
    private readonly ThemeData _theme;
    private Size _bubbleSize;
    private Size _screen;

    public TooltipBubble(string text, Offset position, ThemeData theme)
    {
        _theme = theme;
        Position = position;
        _bubble = new DecoratedBox {
            Elevation = Elevation.Z2,
            Fill = theme.Surface,
            Radius = Radii.Sm,
            Child = new Padding(
                EdgeInsets.All(Spacing.Sm),
                new Label(text) {
                    FontSize = theme.FontSizeCaption,
                    Color = theme.OnSurface,
                }
            ),
        };
    }

    public Offset Position { get; set; }

    public override Size Measure(Constraints c)
    {
        _screen = new Size(c.MaxWidth, c.MaxHeight);
        _bubbleSize = _bubble.Measure(
            new Constraints(
                0f,
                c.MaxWidth,
                0f,
                c.MaxHeight
            )
        );
        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _screen.Width,
            _screen.Height
        );

        // Position above and to the right of the cursor.
        var bx = MathF.Min(Position.X + Spacing.Md, _screen.Width - _bubbleSize.Width - Spacing.Xs);
        var by = Position.Y - _bubbleSize.Height - Spacing.Sm;
        if (by < Spacing.Xs) by = Position.Y + Spacing.Xl;

        _bubble.Layout(new Offset(bx, by));
    }

    public override void Paint(PaintList paint)
    {
        _bubble.Paint(paint);
    }

    // Transparent to hit-testing — let events through to widgets beneath
    public override Widget? HitTest(Offset point)
    {
        return null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_bubble);
    }
}