using System.Globalization;
using System.Text.Json;
using Xunit;
using Zigote.Core;
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
        var root = new Center(child: new Column()) { Bounds = new Rect(0, 0, 640, 480) };

        using var doc = JsonDocument.Parse(InspectServer.WidgetTreeJson(root));
        var tree = doc.RootElement.GetProperty("tree");

        Assert.Equal("Center", tree.GetProperty("type").GetString());
        Assert.Equal(640, tree.GetProperty("w").GetDouble());
        Assert.Equal("Column", tree.GetProperty("children")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void TextThatWouldBreakTheReplyIsEscaped()
    {
        // A widget's own content ends up in "desc"; a label containing a quote, a backslash and a
        // newline is ordinary UI text and must not end the string it sits in.
        var root = new Text("say \"hi\"\\ \n now");

        using var doc = JsonDocument.Parse(InspectServer.WidgetTreeJson(root));
        Assert.Equal("Text", doc.RootElement.GetProperty("tree").GetProperty("type").GetString());
    }

    [Fact]
    public void SemanticsTreeCarriesRoleLabelAndChildren()
    {
        var root = new SemanticsNode(1, SemanticsRole.Group, new Rect(0, 0, 100, 50));
        root.Children.Add(new SemanticsNode(2, SemanticsRole.Button, new Rect(4, 4, 90, 20)) {
            Label = "OK",
        });

        using var doc = JsonDocument.Parse(InspectServer.SemanticsTreeJson(root));
        var tree = doc.RootElement.GetProperty("tree");

        Assert.Equal("Group", tree.GetProperty("role").GetString());
        var child = tree.GetProperty("children")[0];
        Assert.Equal("Button", child.GetProperty("role").GetString());
        Assert.Equal("OK", child.GetProperty("label").GetString());
        Assert.Equal(90, child.GetProperty("w").GetDouble());
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
            var json = InspectServer.WidgetTreeJson(new Center { Bounds = new Rect(0, 0, 12.5f, 3.25f) });
            Assert.Contains("12.5", json);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(12.5, doc.RootElement.GetProperty("tree").GetProperty("w").GetDouble());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void LocalesReplyCarriesCurrentAndSupported()
    {
        using var doc = JsonDocument.Parse(InspectServer.LocalesJson(("en-US", ["en-US", "es", "ar"])));
        Assert.Equal("en-US", doc.RootElement.GetProperty("current").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("locales").GetArrayLength());

        // No LocalizationsScope → an empty list, not an error: the panel hides the combo.
        using var none = JsonDocument.Parse(InspectServer.LocalesJson(null));
        Assert.Equal(JsonValueKind.Null, none.RootElement.GetProperty("current").ValueKind);
        Assert.Equal(0, none.RootElement.GetProperty("locales").GetArrayLength());
    }

    [Fact]
    public void InputCommandsParseIntoRealEvents()
    {
        var down = Assert.IsType<Core.Events.MouseDownEvent>(InspectServer.ParseInput("down 12.5 40 right"));
        Assert.Equal(12.5f, down.X);
        Assert.Equal(Core.Events.MouseButton.Right, down.Button);

        var scroll = Assert.IsType<Core.Events.ScrollEvent>(InspectServer.ParseInput("scroll 10 20 0 -3"));
        Assert.Equal(-3f, scroll.ScrollY);

        var key = Assert.IsType<Core.Events.KeyEvent>(InspectServer.ParseInput("keydown Backspace shift+ctrl"));
        Assert.True(key.Down);
        Assert.Equal(Core.Events.KeyCode.Backspace, key.Key);
        Assert.Equal(Core.Events.Modifiers.Shift | Core.Events.Modifiers.Ctrl, key.Modifiers);

        // Text is verbatim — user spaces survive.
        var text = Assert.IsType<Core.Events.TextInputEvent>(InspectServer.ParseInput("text hello there"));
        Assert.Equal("hello there", text.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("down")]
    [InlineData("down x y")]
    [InlineData("keydown NotAKey")]
    [InlineData("scroll 1 2 3")]
    [InlineData("text")]
    public void MalformedInputParsesToNullNotAThrow(string argument)
    {
        Assert.Null(InspectServer.ParseInput(argument));
    }

    [Fact]
    public void AnEmptyTreeIsStillValid()
    {
        using var doc = JsonDocument.Parse(InspectServer.WidgetTreeJson(null));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tree").ValueKind);
    }
}
