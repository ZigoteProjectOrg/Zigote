using System.Globalization;
using System.Text.Json;
using Xunit;
using Zigote.Core;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     The wire format the IDE panels read. Hand-written JSON is fast and AOT-safe and one unescaped
///     quote away from a reply no panel can parse — so the check is simply that a real tree, including
///     text nobody sanitised, comes back as JSON a parser accepts.
/// </summary>
public class InspectServerTests
{
    [Fact]
    public void WidgetTreeIsParseableJsonWithTypesAndGeometry()
    {
        var root = new Center(child: new Column()) {
            Bounds = new Rect(
                x: 0,
                y: 0,
                width: 640,
                height: 480
            ),
        };

        using var doc = JsonDocument.Parse(InspectServer.WidgetTreeJson(root));
        var tree = doc.RootElement.GetProperty("tree");

        Assert.Equal(expected: "Center", actual: tree.GetProperty("type").GetString());
        Assert.Equal(expected: 640, actual: tree.GetProperty("w").GetDouble());
        Assert.Equal(
            expected: "Column",
            actual: tree.GetProperty("children")[0].GetProperty("type").GetString()
        );
    }

    [Fact]
    public void TextThatWouldBreakTheReplyIsEscaped()
    {
        // A widget's own content ends up in "desc"; a label containing a quote, a backslash and a
        // newline is ordinary UI text and must not end the string it sits in.
        var root = new Text("say \"hi\"\\ \n now");

        using var doc = JsonDocument.Parse(InspectServer.WidgetTreeJson(root));
        Assert.Equal(
            expected: "Text",
            actual: doc.RootElement.GetProperty("tree").GetProperty("type").GetString()
        );
    }

    [Fact]
    public void PreviewsCarryTheirAnnotationAndTheirProperties()
    {
        Environment.SetEnvironmentVariable(
            variable: "ZIGOTE_PREVIEW_ASSEMBLY",
            value: "Zigote.Tests"
        );
        try
        {
            using var doc = JsonDocument.Parse(InspectServer.PreviewsJson());
            var card = doc.RootElement.GetProperty("previews").EnumerateArray()
                .Single(p =>
                    p.GetProperty("target").GetString() == typeof(PreviewParameterised).FullName
                );

            Assert.Equal(expected: "Sample card", actual: card.GetProperty("label").GetString());
            Assert.Equal(expected: 412, actual: card.GetProperty("w").GetDouble());
            Assert.True(card.GetProperty("annotated").GetBoolean());

            var title = card.GetProperty("params")[0];
            Assert.Equal(expected: "title", actual: title.GetProperty("name").GetString());
            Assert.Equal(expected: "string", actual: title.GetProperty("kind").GetString());
            Assert.Equal(expected: "Card", actual: title.GetProperty("value").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "ZIGOTE_PREVIEW_ASSEMBLY", value: null);
        }
    }

    [Fact]
    public void SemanticsTreeCarriesRoleLabelAndChildren()
    {
        var root = new SemanticsNode(
            id: 1,
            role: SemanticsRole.Group,
            bounds: new Rect(
                x: 0,
                y: 0,
                width: 100,
                height: 50
            )
        );
        root.Children.Add(
            new SemanticsNode(
                id: 2,
                role: SemanticsRole.Button,
                bounds: new Rect(
                    x: 4,
                    y: 4,
                    width: 90,
                    height: 20
                )
            ) {
                Label = "OK",
            }
        );

        using var doc = JsonDocument.Parse(InspectServer.SemanticsTreeJson(root));
        var tree = doc.RootElement.GetProperty("tree");

        Assert.Equal(expected: "Group", actual: tree.GetProperty("role").GetString());
        var child = tree.GetProperty("children")[0];
        Assert.Equal(expected: "Button", actual: child.GetProperty("role").GetString());
        Assert.Equal(expected: "OK", actual: child.GetProperty("label").GetString());
        Assert.Equal(expected: 90, actual: child.GetProperty("w").GetDouble());
    }

    [Fact]
    public void NumbersUseAnInvariantDecimalPoint()
    {
        // On a comma-decimal machine an uncultured ToString() emits "12,5", which is a second array
        // element to every JSON parser alive rather than a syntax error you would notice.
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            string json = InspectServer.WidgetTreeJson(
                new Center {
                    Bounds = new Rect(
                        x: 0,
                        y: 0,
                        width: 12.5f,
                        height: 3.25f
                    ),
                }
            );
            Assert.Contains(expectedSubstring: "12.5", actualString: json);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(
                expected: 12.5,
                actual: doc.RootElement.GetProperty("tree").GetProperty("w").GetDouble()
            );
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void LocalesReplyCarriesCurrentAndSupported()
    {
        using var doc =
            JsonDocument.Parse(InspectServer.LocalesJson(("en-US", ["en-US", "es", "ar"])));
        Assert.Equal(expected: "en-US", actual: doc.RootElement.GetProperty("current").GetString());
        Assert.Equal(expected: 3, actual: doc.RootElement.GetProperty("locales").GetArrayLength());

        // No LocalizationsScope → an empty list, not an error: the panel hides the combo.
        using var none = JsonDocument.Parse(InspectServer.LocalesJson(null));
        Assert.Equal(
            expected: JsonValueKind.Null,
            actual: none.RootElement.GetProperty("current").ValueKind
        );
        Assert.Equal(expected: 0, actual: none.RootElement.GetProperty("locales").GetArrayLength());
    }

    [Fact]
    public void InputCommandsParseIntoRealEvents()
    {
        var down =
            Assert.IsType<MouseDownEvent>(InspectServer.ParseInput("down 12.5 40 right"));
        Assert.Equal(expected: 12.5f, actual: down.X);
        Assert.Equal(expected: MouseButton.Right, actual: down.Button);

        var scroll =
            Assert.IsType<ScrollEvent>(InspectServer.ParseInput("scroll 10 20 0 -3"));
        Assert.Equal(expected: -3f, actual: scroll.ScrollY);

        var key = Assert.IsType<KeyEvent>(InspectServer.ParseInput("keydown Backspace shift+ctrl"));
        Assert.True(key.Down);
        Assert.Equal(expected: KeyCode.Backspace, actual: key.Key);
        Assert.Equal(
            expected: Modifiers.Shift | Modifiers.Ctrl,
            actual: key.Modifiers
        );

        // Text is verbatim — user spaces survive.
        var text =
            Assert.IsType<TextInputEvent>(InspectServer.ParseInput("text hello there"));
        Assert.Equal(expected: "hello there", actual: text.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("down")]
    [InlineData("down x y")]
    [InlineData("keydown NotAKey")]
    [InlineData("scroll 1 2 3")]
    [InlineData("text")]
    public void MalformedInputParsesToNullNotAThrow(string argument) =>
        Assert.Null(InspectServer.ParseInput(argument));

    [Fact]
    public void AnEmptyTreeIsStillValid()
    {
        using var doc = JsonDocument.Parse(InspectServer.WidgetTreeJson(null));
        Assert.Equal(
            expected: JsonValueKind.Null,
            actual: doc.RootElement.GetProperty("tree").ValueKind
        );
    }
}
