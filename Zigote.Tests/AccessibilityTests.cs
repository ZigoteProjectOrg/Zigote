using Xunit;
using Zigote.Core;
using Zigote.UI.Semantics;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Covers the accessibility / semantics tree: that interactive controls and text/image leaves
///     contribute correctly-roled <see cref="SemanticsNode" />s, that decorative wrappers collapse,
///     and
///     that the collapsed tree reads the way a screen reader would announce it. All headless — build a
///     widget tree, lay it out, build the semantics, assert. No native window, no real screen reader.
/// </summary>
public class AccessibilityTests
{
    private static SemanticsNode Tree(Widget w, float width = 400f, float height = 300f)
    {
        w.Measure(Constraints.Loose(width, height));
        w.Layout(Offset.Zero);
        return SemanticsBuilder.Build(w, [], new Size(width, height));
    }

    private static SemanticsNode? First(SemanticsNode root, SemanticsRole role)
    {
        return root.Flatten().FirstOrDefault(n => n.Role == role);
    }

    private static int CountRole(SemanticsNode root, SemanticsRole role)
    {
        return root.Flatten().Count(n => n.Role == role);
    }

    [Fact]
    public void Button_ProducesButtonNodeWithLabelAndActions()
    {
        var tree = Tree(new Button("Save", () => { }));
        var node = First(tree, SemanticsRole.Button);
        Assert.NotNull(node);
        Assert.Equal("Save", node!.Label);
        Assert.True(node.HasAction(SemanticsAction.Tap));
        Assert.True(node.HasAction(SemanticsAction.Focus));
        Assert.True(node.HasFlag(SemanticsFlags.Focusable));
        Assert.False(node.HasFlag(SemanticsFlags.Disabled));
    }

    [Fact]
    public void Button_LabelIsMergedNotDuplicatedAsTextNode()
    {
        // The Pressable is a semantic leaf, so the inner Label must NOT emit its own Text node.
        var tree = Tree(new Button("Save", () => { }));
        Assert.Equal(0, CountRole(tree, SemanticsRole.Text));
        Assert.Equal(1, CountRole(tree, SemanticsRole.Button));
    }

    [Fact]
    public void DisabledButton_IsNotFocusableAndMarkedDisabled()
    {
        var tree = Tree(new Button("Off", null) { Enabled = false });
        var node = First(tree, SemanticsRole.Button)!;
        Assert.True(node.HasFlag(SemanticsFlags.Disabled));
        Assert.False(node.HasFlag(SemanticsFlags.Focusable));
    }

    [Fact]
    public void Checkbox_ExposesCheckableAndCheckedState()
    {
        var on = First(Tree(new Checkbox(true)), SemanticsRole.Checkbox)!;
        Assert.True(on.HasFlag(SemanticsFlags.Checkable));
        Assert.True(on.HasFlag(SemanticsFlags.Checked));

        var off = First(Tree(new Checkbox(false)), SemanticsRole.Checkbox)!;
        Assert.True(off.HasFlag(SemanticsFlags.Checkable));
        Assert.False(off.HasFlag(SemanticsFlags.Checked));
    }

    [Fact]
    public void Switch_HasSwitchRoleAndCheckedState()
    {
        var node = First(
            Tree(new Switch(true) { SemanticsLabel = "Wi-Fi" }),
            SemanticsRole.Switch
        )!;
        Assert.Equal("Wi-Fi", node.Label);
        Assert.True(node.HasFlag(SemanticsFlags.Checked));
    }

    [Fact]
    public void Slider_ReportsValueAndIncrementActions()
    {
        var node = First(Tree(new Slider(0.5f)), SemanticsRole.Slider)!;
        Assert.Equal("50%", node.Value);
        Assert.True(node.HasAction(SemanticsAction.Increase));
        Assert.True(node.HasAction(SemanticsAction.Decrease));
    }

    [Fact]
    public void TextField_ReportsValueReadOnlyAndMultilineFlags()
    {
        var ro = First(
            Tree(
                new TextField {
                    Text = "hi",
                    ReadOnly = true,
                }
            ),
            SemanticsRole.TextField
        )!;
        Assert.Equal("hi", ro.Value);
        Assert.True(ro.HasFlag(SemanticsFlags.ReadOnly));
        Assert.False(ro.HasFlag(SemanticsFlags.Multiline));

        var ml = First(
            Tree(
                new TextField {
                    Text = "a\nb",
                    Multiline = true,
                }
            ),
            SemanticsRole.TextField
        )!;
        Assert.True(ml.HasFlag(SemanticsFlags.Multiline));
    }

    [Fact]
    public void Label_IsTextOrHeaderByStyle()
    {
        Assert.Equal(SemanticsRole.Text, First(Tree(new Label("Body")), SemanticsRole.Text)!.Role);
        var header = First(
            Tree(new Label("Title") { Style = Label.LabelStyle.Title }),
            SemanticsRole.Header
        );
        Assert.NotNull(header);
        Assert.Equal("Title", header!.Label);
    }

    [Fact]
    public void ExcludeSemantics_DropsTheSubtree()
    {
        // A decorative label opts its whole node out of the accessibility tree.
        Assert.Equal(1, CountRole(Tree(new Label("read me")), SemanticsRole.Text));
        Assert.Equal(
            0,
            CountRole(Tree(new Label("ignore me") { Decorative = true }), SemanticsRole.Text)
        );
    }

    [Fact]
    public void TransparentContainers_HoistChildrenInsteadOfNesting()
    {
        // A Column has no semantics of its own, so its two buttons are siblings under the synthetic root.
        var tree = Tree(
            new Column {
                Children = {
                    new Button("A", () => { }),
                    new Button("B", () => { }),
                },
            }
        );
        Assert.Equal(
            0,
            CountRole(tree, SemanticsRole.Group) - 1
        ); // only the synthetic root is a Group
        Assert.Equal(2, CountRole(tree, SemanticsRole.Button));
    }

    [Fact]
    public void Describe_ReadsLikeAnAnnouncement()
    {
        var button = First(Tree(new Button("Save", () => { })), SemanticsRole.Button)!;
        Assert.Equal("Button: Save", button.Describe());

        var check = First(Tree(new Checkbox(true)), SemanticsRole.Checkbox)!;
        Assert.Contains("checked", check.Describe());
    }

    [Fact]
    public void DirectionalKeyOwnership_TextAndSliderKeepArrows_ButtonDoesNot()
    {
        Assert.True(new TextField().HandlesDirectionalKeys);
        Assert.True(new CodeEditor().HandlesDirectionalKeys);
        Assert.True(new Slider(0f).HandlesDirectionalKeys);
        Assert.False(new Pressable().HandlesDirectionalKeys);
    }
}
