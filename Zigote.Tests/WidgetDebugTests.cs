using Xunit;
using Zigote.Core;
using Zigote.UI.Debug;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Covers the debug widget-tree property dump (`WidgetDebug.Properties`): that values are
///     formatted
///     readably — strings quoted + newline-escaped, booleans lowercased, delegates as `ƒ …`, floats
///     trimmed, colours as hex — instead of raw `ToString()` noise, and that the widget's own
///     properties
///     are listed before inherited ones. Headless.
/// </summary>
public class WidgetDebugTests
{
    private static string Val(List<(string Name, string Value)> p, string name) =>
        p.First(x => x.Name == name).Value;

    private static bool Has(List<(string Name, string Value)> p, string name) =>
        p.Any(x => x.Name == name);

    [Fact]
    public void Strings_AreQuotedAndNewlineEscaped()
    {
        var props = WidgetDebug.Properties(new TextField { Text = "hi\nthere" });
        Assert.Equal(expected: "\"hi⏎there\"", actual: Val(p: props, name: "Text"));
    }

    [Fact]
    public void EmptyString_ShowsAsQuotes_NotBlank()
    {
        var props = WidgetDebug.Properties(new TextField { Text = "" });
        Assert.Equal(expected: "\"\"", actual: Val(p: props, name: "Text"));
    }

    [Fact]
    public void Delegate_ShowsAsFunctionGlyph_NotTypeName()
    {
        var props = WidgetDebug.Properties(new TextField { OnChanged = _ => { } });
        string v = Val(p: props, name: "OnChanged");
        Assert.StartsWith(expectedStartString: "ƒ", actualString: v);
        Assert.DoesNotContain(expectedSubstring: "System.Action", actualString: v);
    }

    [Fact]
    public void Booleans_AreLowercased()
    {
        var props = WidgetDebug.Properties(
            new TextField {
                Multiline = true,
                ReadOnly = false,
            }
        );
        Assert.Equal(expected: "true", actual: Val(p: props, name: "Multiline"));
        Assert.Equal(expected: "false", actual: Val(p: props, name: "ReadOnly"));
    }

    [Fact]
    public void WholeNumberFloats_RenderWithoutDecimals()
    {
        var props = WidgetDebug.Properties(new TextField { MinWidth = 140f });
        Assert.Equal(expected: "140", actual: Val(p: props, name: "MinWidth"));
    }

    [Fact]
    public void Color_RendersAsHex()
    {
        var props =
            WidgetDebug.Properties(new Label("x") { Color = new Color(r: 1f, g: 0f, b: 0f) });
        Assert.Equal(expected: "#FF0000", actual: Val(p: props, name: "Color"));
    }

    [Fact]
    public void NullProperties_AreOmitted()
    {
        // OnChanged defaults to null → no row for it.
        var props = WidgetDebug.Properties(new TextField());
        Assert.False(Has(p: props, name: "OnChanged"));
    }

    [Fact]
    public void HeaderRows_ArePresent()
    {
        var props = WidgetDebug.Properties(new TextField());
        Assert.Equal(expected: "TextField", actual: Val(p: props, name: "Type"));
        Assert.True(Has(p: props, name: "Bounds"));
        Assert.True(Has(p: props, name: "Dirty"));
    }

    [Fact]
    public void LongValues_AreTruncated()
    {
        var props = WidgetDebug.Properties(new TextField { Text = new string(c: 'x', count: 500) });
        Assert.True(Val(p: props, name: "Text").Length <= 161); // 160 cap + ellipsis
        Assert.EndsWith(expectedEndString: "…", actualString: Val(p: props, name: "Text"));
    }

    // ── Inspector tree helpers (Describe / DeepestAt / PathTo / FormatConstraints) ──

    [Fact]
    public void Describe_Label_ReturnsQuotedText() => Assert.Equal(
        expected: "\"Save\"",
        actual: WidgetDebug.Describe(new Label("Save"))
    );

    [Fact]
    public void Describe_LongText_IsTruncated()
    {
        string? s = WidgetDebug.Describe(new Label(new string(c: 'x', count: 100)));
        Assert.NotNull(s);
        Assert.EndsWith(expectedEndString: "…", actualString: s);
        Assert.True(s.Length <= 40);
    }

    [Fact]
    public void Describe_PlainContainer_IsNull() => Assert.Null(WidgetDebug.Describe(new Column()));

    [Fact]
    public void FormatConstraints_TightAndRanged()
    {
        Assert.Equal(
            expected: "tight 200×100",
            actual: WidgetDebug.FormatConstraints(Constraints.Tight(width: 200f, height: 100f))
        );
        Assert.Equal(
            expected: "0≤w≤400 · 0≤h≤∞",
            actual: WidgetDebug.FormatConstraints(
                new Constraints(
                    minWidth: 0f,
                    maxWidth: 400f
                )
            )
        );
    }

    private static Column LaidOutTree(out SizedBox first, out SizedBox second, out Label inner)
    {
        inner = new Label("hi");
        first = new SizedBox(width: 200f, height: 40f, child: inner);
        second = new SizedBox(width: 200f, height: 40f);
        var root = new Column {
            Children = {
                first,
                second,
            },
        };
        root.Attach(
            owner: null!,
            parent: null
        ); // populate Parent links, as App does when a root is installed
        root.Measure(Constraints.Tight(width: 200f, height: 100f));
        root.Layout(Offset.Zero);
        return root;
    }

    [Fact]
    public void DeepestAt_PicksTheDeepestWidgetUnderThePoint()
    {
        var root = LaidOutTree(first: out _, second: out var second, inner: out var inner);
        // Inside the first box → the Label leaf, not its SizedBox wrapper.
        Assert.Same(
            expected: inner,
            actual: WidgetDebug.DeepestAt(root: root, point: new Offset(x: 5f, y: 5f))
        );
        // Inside the second (empty) box → the box itself.
        Assert.Same(
            expected: second,
            actual: WidgetDebug.DeepestAt(root: root, point: new Offset(x: 5f, y: 60f))
        );
    }

    [Fact]
    public void DeepestAt_IgnoresAnEmptyFullScreenOverlayLayer()
    {
        // The AdwToastOverlay shape: content plus a topmost Align that fills the window and shows
        // nothing until a toast arrives. Picking by bounds alone selected that Align everywhere.
        var inner = new Label("hi");
        var content = new SizedBox(width: 200f, height: 100f, child: inner);
        var empty = new Align(Alignment.BottomCenter);
        var root = new Stack {
            Children = {
                content,
                empty,
            },
        };
        root.Attach(owner: null!, parent: null);
        root.Measure(Constraints.Tight(width: 200f, height: 100f));
        root.Layout(Offset.Zero);

        Assert.True(
            condition: empty.Bounds.Contains(px: 5f, py: 5f),
            userMessage: "the empty layer must cover the point"
        );
        Assert.Same(
            expected: inner,
            actual: WidgetDebug.DeepestAt(root: root, point: new Offset(x: 5f, y: 5f))
        );
    }

    [Fact]
    public void DeepestAt_OutsideEverything_ReturnsNull()
    {
        var root = LaidOutTree(first: out _, second: out _, inner: out _);
        Assert.Null(WidgetDebug.DeepestAt(root: root, point: new Offset(x: 500f, y: 500f)));
    }

    [Fact]
    public void PathTo_WalksRootToWidget()
    {
        var root = LaidOutTree(first: out var first, second: out _, inner: out var inner);
        var path = WidgetDebug.PathTo(inner);
        Assert.Same(expected: inner, actual: path[^1]);
        Assert.Contains(expected: first, collection: path);
        Assert.Same(expected: root, actual: path[0]);
    }

    [Fact]
    public void Members_ObjectValue_IsExpandableAndShownAsItsType()
    {
        var w = new Styled {
            Sheet = new Style(Background: new Color(r: 1f, g: 0f, b: 0f), Radius: 12f),
        };
        var m = WidgetDebug.Members(w).First(x => x.Name == "Sheet");
        Assert.True(m.Expandable);
        Assert.Equal(expected: "{Style}", actual: m.Value); // not the record's whole ToString dump
        Assert.Equal(
            expected: "#FF0000",
            actual: WidgetDebug.Members(m.Raw!).First(x => x.Name == "Background").Value
        );
    }

    [Fact]
    public void Members_NestedNulls_AreKept_ButWidgetNullsAreNot()
    {
        var style = new Style(Background: null, Radius: 12f);
        Assert.Equal(
            expected: "null",
            actual: WidgetDebug.Members(style).First(x => x.Name == "Background").Value
        );
        Assert.DoesNotContain(
            collection: WidgetDebug.Members(new Styled()),
            filter: x => x.Name == "Sheet"
        );
    }

    [Fact]
    public void CanExpand_LeafValuesAreNotExpandable()
    {
        Assert.False(WidgetDebug.CanExpand(null));
        Assert.False(WidgetDebug.CanExpand("text"));
        Assert.False(WidgetDebug.CanExpand(12f));
        Assert.False(WidgetDebug.CanExpand(new Color(r: 1f, g: 0f, b: 0f)));
        Assert.True(WidgetDebug.CanExpand(new Style(Background: null, Radius: 1f)));
        Assert.True(WidgetDebug.CanExpand(new List<int> { 1 }));
    }

    [Fact]
    public void ToJson_NestsObjectsAndQuotesFormattedLeaves()
    {
        string json = WidgetDebug.ToJson(
            new Style(
                Background: new Color(r: 1f, g: 0f, b: 0f),
                Radius: 12f,
                Inner: new Nested("deep")
            )
        );
        Assert.Contains(expectedSubstring: "\"Background\": \"#FF0000\"", actualString: json);
        Assert.Contains(expectedSubstring: "\"Radius\": 12", actualString: json);
        Assert.Contains(expectedSubstring: "\"Name\": \"deep\"", actualString: json);
    }

    [Fact]
    public void ToJson_DepthCap_StopsDescending()
    {
        string json = WidgetDebug.ToJson(
            root: new Style(Background: null, Radius: 1f, Inner: new Nested("deep")),
            maxDepth: 1
        );
        Assert.Contains(expectedSubstring: "\"Inner\": \"{Nested}\"", actualString: json);
        Assert.DoesNotContain(expectedSubstring: "deep", actualString: json);
    }

    [Fact]
    public void ToJson_Cycle_DoesNotRecurseForever()
    {
        var a = new Node();
        a.Next = a;
        string json = WidgetDebug.ToJson(a);
        Assert.Contains(expectedSubstring: "↻", actualString: json);
    }

    // ── Property tree / JSON view (Members / CanExpand / ToJson) ──

    private sealed record Style(Color? Background, float Radius, Nested? Inner = null);

    private sealed record Nested(string Name);

    private sealed class Styled : Label
    {
        public Styled() : base("x") { }

        public Style? Sheet { get; set; }
    }

    private sealed class Node
    {
        public Node? Next { get; set; }
    }
}
