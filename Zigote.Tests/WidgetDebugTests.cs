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
    private static string Val(List<(string Name, string Value)> p, string name)
    {
        return p.First(x => x.Name == name).Value;
    }

    private static bool Has(List<(string Name, string Value)> p, string name)
    {
        return p.Any(x => x.Name == name);
    }

    [Fact]
    public void Strings_AreQuotedAndNewlineEscaped()
    {
        var props = WidgetDebug.Properties(new TextField { Text = "hi\nthere" });
        Assert.Equal("\"hi⏎there\"", Val(props, "Text"));
    }

    [Fact]
    public void EmptyString_ShowsAsQuotes_NotBlank()
    {
        var props = WidgetDebug.Properties(new TextField { Text = "" });
        Assert.Equal("\"\"", Val(props, "Text"));
    }

    [Fact]
    public void Delegate_ShowsAsFunctionGlyph_NotTypeName()
    {
        var props = WidgetDebug.Properties(new TextField { OnChanged = _ => { } });
        var v = Val(props, "OnChanged");
        Assert.StartsWith("ƒ", v);
        Assert.DoesNotContain("System.Action", v);
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
        Assert.Equal("true", Val(props, "Multiline"));
        Assert.Equal("false", Val(props, "ReadOnly"));
    }

    [Fact]
    public void WholeNumberFloats_RenderWithoutDecimals()
    {
        var props = WidgetDebug.Properties(new TextField { MinWidth = 140f });
        Assert.Equal("140", Val(props, "MinWidth"));
    }

    [Fact]
    public void Color_RendersAsHex()
    {
        var props = WidgetDebug.Properties(new Label("x") { Color = new Color(1f, 0f, 0f) });
        Assert.Equal("#FF0000", Val(props, "Color"));
    }

    [Fact]
    public void NullProperties_AreOmitted()
    {
        // OnChanged defaults to null → no row for it.
        var props = WidgetDebug.Properties(new TextField());
        Assert.False(Has(props, "OnChanged"));
    }

    [Fact]
    public void HeaderRows_ArePresent()
    {
        var props = WidgetDebug.Properties(new TextField());
        Assert.Equal("TextField", Val(props, "Type"));
        Assert.True(Has(props, "Bounds"));
        Assert.True(Has(props, "Dirty"));
    }

    [Fact]
    public void LongValues_AreTruncated()
    {
        var props = WidgetDebug.Properties(new TextField { Text = new string('x', 500) });
        Assert.True(Val(props, "Text").Length <= 161); // 160 cap + ellipsis
        Assert.EndsWith("…", Val(props, "Text"));
    }

    // ── Inspector tree helpers (Describe / DeepestAt / PathTo / FormatConstraints) ──

    [Fact]
    public void Describe_Label_ReturnsQuotedText()
    {
        Assert.Equal("\"Save\"", WidgetDebug.Describe(new Label("Save")));
    }

    [Fact]
    public void Describe_LongText_IsTruncated()
    {
        var s = WidgetDebug.Describe(new Label(new string('x', 100)));
        Assert.NotNull(s);
        Assert.EndsWith("…", s);
        Assert.True(s.Length <= 40);
    }

    [Fact]
    public void Describe_PlainContainer_IsNull()
    {
        Assert.Null(WidgetDebug.Describe(new Column()));
    }

    [Fact]
    public void FormatConstraints_TightAndRanged()
    {
        Assert.Equal("tight 200×100", WidgetDebug.FormatConstraints(Constraints.Tight(200f, 100f)));
        Assert.Equal(
            "0≤w≤400 · 0≤h≤∞",
            WidgetDebug.FormatConstraints(
                new Constraints(
                    0f,
                    400f
                )
            )
        );
    }

    private static Column LaidOutTree(out SizedBox first, out SizedBox second, out Label inner)
    {
        inner = new Label("hi");
        first = new SizedBox(200f, 40f, inner);
        second = new SizedBox(200f, 40f);
        var root = new Column {
            Children = {
                first,
                second,
            },
        };
        root.Attach(null!, null); // populate Parent links, as App does when a root is installed
        root.Measure(Constraints.Tight(200f, 100f));
        root.Layout(Offset.Zero);
        return root;
    }

    [Fact]
    public void DeepestAt_PicksTheDeepestWidgetUnderThePoint()
    {
        var root = LaidOutTree(out _, out var second, out var inner);
        // Inside the first box → the Label leaf, not its SizedBox wrapper.
        Assert.Same(inner, WidgetDebug.DeepestAt(root, new Offset(5f, 5f)));
        // Inside the second (empty) box → the box itself.
        Assert.Same(second, WidgetDebug.DeepestAt(root, new Offset(5f, 60f)));
    }

    [Fact]
    public void DeepestAt_IgnoresAnEmptyFullScreenOverlayLayer()
    {
        // The AdwToastOverlay shape: content plus a topmost Align that fills the window and shows
        // nothing until a toast arrives. Picking by bounds alone selected that Align everywhere.
        var inner = new Label("hi");
        var content = new SizedBox(200f, 100f, inner);
        var empty = new Align(Alignment.BottomCenter);
        var root = new Stack {
            Children = {
                content,
                empty,
            },
        };
        root.Attach(null!, null);
        root.Measure(Constraints.Tight(200f, 100f));
        root.Layout(Offset.Zero);

        Assert.True(empty.Bounds.Contains(5f, 5f), "the empty layer must cover the point");
        Assert.Same(inner, WidgetDebug.DeepestAt(root, new Offset(5f, 5f)));
    }

    [Fact]
    public void DeepestAt_OutsideEverything_ReturnsNull()
    {
        var root = LaidOutTree(out _, out _, out _);
        Assert.Null(WidgetDebug.DeepestAt(root, new Offset(500f, 500f)));
    }

    [Fact]
    public void PathTo_WalksRootToWidget()
    {
        var root = LaidOutTree(out var first, out _, out var inner);
        var path = WidgetDebug.PathTo(inner);
        Assert.Same(inner, path[^1]);
        Assert.Contains(first, path);
        Assert.Same(root, path[0]);
    }

    // ── Property tree / JSON view (Members / CanExpand / ToJson) ──

    private sealed record Style(Color? Background, float Radius, Nested? Inner = null);

    private sealed record Nested(string Name);

    private sealed class Styled : Label
    {
        public Styled() : base("x")
        {
        }

        public Style? Sheet { get; set; }
    }

    [Fact]
    public void Members_ObjectValue_IsExpandableAndShownAsItsType()
    {
        var w = new Styled { Sheet = new Style(new Color(1f, 0f, 0f), 12f) };
        var m = WidgetDebug.Members(w).First(x => x.Name == "Sheet");
        Assert.True(m.Expandable);
        Assert.Equal("{Style}", m.Value); // not the record's whole ToString dump
        Assert.Equal(
            "#FF0000",
            WidgetDebug.Members(m.Raw!).First(x => x.Name == "Background").Value
        );
    }

    [Fact]
    public void Members_NestedNulls_AreKept_ButWidgetNullsAreNot()
    {
        var style = new Style(null, 12f);
        Assert.Equal("null", WidgetDebug.Members(style).First(x => x.Name == "Background").Value);
        Assert.DoesNotContain(WidgetDebug.Members(new Styled()), x => x.Name == "Sheet");
    }

    [Fact]
    public void CanExpand_LeafValuesAreNotExpandable()
    {
        Assert.False(WidgetDebug.CanExpand(null));
        Assert.False(WidgetDebug.CanExpand("text"));
        Assert.False(WidgetDebug.CanExpand(12f));
        Assert.False(WidgetDebug.CanExpand(new Color(1f, 0f, 0f)));
        Assert.True(WidgetDebug.CanExpand(new Style(null, 1f)));
        Assert.True(WidgetDebug.CanExpand(new List<int> { 1 }));
    }

    [Fact]
    public void ToJson_NestsObjectsAndQuotesFormattedLeaves()
    {
        var json = WidgetDebug.ToJson(new Style(new Color(1f, 0f, 0f), 12f, new Nested("deep")));
        Assert.Contains("\"Background\": \"#FF0000\"", json);
        Assert.Contains("\"Radius\": 12", json);
        Assert.Contains("\"Name\": \"deep\"", json);
    }

    [Fact]
    public void ToJson_DepthCap_StopsDescending()
    {
        var json = WidgetDebug.ToJson(new Style(null, 1f, new Nested("deep")), 1);
        Assert.Contains("\"Inner\": \"{Nested}\"", json);
        Assert.DoesNotContain("deep", json);
    }

    [Fact]
    public void ToJson_Cycle_DoesNotRecurseForever()
    {
        var a = new Node();
        a.Next = a;
        var json = WidgetDebug.ToJson(a);
        Assert.Contains("↻", json);
    }

    private sealed class Node
    {
        public Node? Next { get; set; }
    }
}