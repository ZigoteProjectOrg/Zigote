using Xunit;
using Zigote.Core;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Pins the dialog-content layout contract the export dialog tripped over: <see cref="Dialog" />
///     measures its card with a bounded max height (85% of the window) and content columns must be
///     <see cref="MainAxisSize.Min" /> — a default (Max) column fills the whole bound and starves
///     every later sibling to zero height (rows vanish / text overlaps at the bottom).
/// </summary>
public class DialogContentLayoutTests
{
    // The Dialog's real card constraints: loose, width-capped, height-capped at a screen fraction.
    private static readonly Constraints DialogCard = new(
        0f,
        560f,
        0f,
        1200f
    );

    private static Widget CheckRow(string label, string? caption, ThemeData theme)
    {
        var text = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
            Children = { new Label(label, theme.FontSizeBody, theme.OnSurface) },
        };
        if (caption is not null)
            text.Children.Add(new Label(caption, theme.FontSizeCaption, theme.Hint));
        return new Padding(
            EdgeInsets.Only(bottom: 6f),
            new Row {
                CrossAxisAlignment = CrossAxisAlignment.Start,
                Children = {
                    new Checkbox(false),
                    new SizedBox(8f),
                    text,
                },
            }
        );
    }

    [Fact]
    public void MinColumn_DialogContent_HugsItsChildren()
    {
        var theme = ThemeData.Dark;
        var rows = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };
        for (var i = 0; i < 4; i++)
            rows.Children.Add(CheckRow($"Platform {i}", i == 3 ? "a caption line" : null, theme));
        var marker = new Label("BOTTOM", theme.FontSizeBody, theme.OnSurface);
        rows.Children.Add(marker);

        var body = new SizedBox(540f, child: new Padding(EdgeInsets.All(20f), rows));
        var size = body.Measure(DialogCard);
        body.Layout(new Offset(0, 0));

        // Content-sized, not bound-filling.
        Assert.True(size.Height < 300f, $"dialog content ballooned to {size.Height}");

        // Every row got real height and the last child sits below the first (no zero-height pile-up).
        var children = rows.GetChildren().ToList();
        foreach (var c in children.Take(4))
            Assert.True(c.Bounds.Height > 10f, $"row collapsed to {c.Bounds.Height}");
        Assert.True(marker.Bounds.Y > children[0].Bounds.Y + 40f, "children overlap");
    }

    [Fact]
    public void MaxColumn_InsideRow_FillsTheBound_TheTrapThisGuards()
    {
        var theme = ThemeData.Dark;
        // The broken shape: a default (Max) column nested in a Row under bounded height.
        var inner = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            Children = { new Label("label", theme.FontSizeBody, theme.OnSurface) },
        };
        var row = new Row {
            Children = {
                new Checkbox(false),
                inner,
            },
        };

        var size = row.Measure(DialogCard);

        // Documents the framework behavior the fix relies on: Max fills the bounded main axis.
        // If this ever starts sizing to content, the Min annotations become optional (not wrong).
        Assert.True(
            size.Height >= 1000f,
            $"expected the Max column to expand under a bounded height, got {size.Height}"
        );
    }
}
