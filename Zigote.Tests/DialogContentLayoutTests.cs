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
        minWidth: 0f,
        maxWidth: 560f,
        minHeight: 0f,
        maxHeight: 1200f
    );

    private static Widget CheckRow(string label, string? caption, ThemeData theme)
    {
        var text = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
            Children = {
                new Label(text: label, fontSize: theme.FontSizeBody, color: theme.OnSurface),
            },
        };
        if (caption is not null)
        {
            text.Children.Add(
                new Label(text: caption, fontSize: theme.FontSizeCaption, color: theme.Hint)
            );
        }

        return new Padding(
            padding: EdgeInsets.Only(bottom: 6f),
            child: new Row {
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
        for (int i = 0; i < 4; i++)
        {
            rows.Children.Add(
                CheckRow(
                    label: $"Platform {i}",
                    caption: i == 3 ? "a caption line" : null,
                    theme: theme
                )
            );
        }

        var marker = new Label(
            text: "BOTTOM",
            fontSize: theme.FontSizeBody,
            color: theme.OnSurface
        );
        rows.Children.Add(marker);

        var body = new SizedBox(
            width: 540f,
            child: new Padding(padding: EdgeInsets.All(20f), child: rows)
        );
        var size = body.Measure(DialogCard);
        body.Layout(new Offset(x: 0, y: 0));

        // Content-sized, not bound-filling.
        Assert.True(
            condition: size.Height < 300f,
            userMessage: $"dialog content ballooned to {size.Height}"
        );

        // Every row got real height and the last child sits below the first (no zero-height pile-up).
        var children = rows.GetChildren().ToList();
        foreach (var c in children.Take(4))
        {
            Assert.True(
                condition: c.Bounds.Height > 10f,
                userMessage: $"row collapsed to {c.Bounds.Height}"
            );
        }

        Assert.True(
            condition: marker.Bounds.Y > children[0].Bounds.Y + 40f,
            userMessage: "children overlap"
        );
    }

    [Fact]
    public void MaxColumn_InsideRow_FillsTheBound_TheTrapThisGuards()
    {
        var theme = ThemeData.Dark;
        // The broken shape: a default (Max) column nested in a Row under bounded height.
        var inner = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            Children = {
                new Label(text: "label", fontSize: theme.FontSizeBody, color: theme.OnSurface),
            },
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
            condition: size.Height >= 1000f,
            userMessage:
            $"expected the Max column to expand under a bounded height, got {size.Height}"
        );
    }
}
