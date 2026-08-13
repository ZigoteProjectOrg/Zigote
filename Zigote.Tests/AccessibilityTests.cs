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
        w.Measure(Constraints.Loose(width: width, height: height));
        w.Layout(Offset.Zero);
        return SemanticsBuilder.Build(
            root: w,
            overlays: [],
            screen: new Size(width: width, height: height)
        );
    }

    private static SemanticsNode? First(SemanticsNode root, SemanticsRole role) =>
        root.Flatten().FirstOrDefault(n => n.Role == role);

    private static int CountRole(SemanticsNode root, SemanticsRole role) =>
        root.Flatten().Count(n => n.Role == role);

    [Fact]
    public void Button_ProducesButtonNodeWithLabelAndActions()
    {
        var tree = Tree(new Button(label: "Save", onPressed: () => { }));
        var node = First(root: tree, role: SemanticsRole.Button);
        Assert.NotNull(node);
        Assert.Equal(expected: "Save", actual: node!.Label);
        Assert.True(node.HasAction(SemanticsAction.Tap));
        Assert.True(node.HasAction(SemanticsAction.Focus));
        Assert.True(node.HasFlag(SemanticsFlags.Focusable));
        Assert.False(node.HasFlag(SemanticsFlags.Disabled));
    }

    [Fact]
    public void Button_LabelIsMergedNotDuplicatedAsTextNode()
    {
        // The Pressable is a semantic leaf, so the inner Label must NOT emit its own Text node.
        var tree = Tree(new Button(label: "Save", onPressed: () => { }));
        Assert.Equal(expected: 0, actual: CountRole(root: tree, role: SemanticsRole.Text));
        Assert.Equal(expected: 1, actual: CountRole(root: tree, role: SemanticsRole.Button));
    }

    [Fact]
    public void DisabledButton_IsNotFocusableAndMarkedDisabled()
    {
        var tree = Tree(new Button(label: "Off", onPressed: null) { Enabled = false });
        var node = First(root: tree, role: SemanticsRole.Button)!;
        Assert.True(node.HasFlag(SemanticsFlags.Disabled));
        Assert.False(node.HasFlag(SemanticsFlags.Focusable));
    }

    [Fact]
    public void Checkbox_ExposesCheckableAndCheckedState()
    {
        var on = First(root: Tree(new Checkbox(true)), role: SemanticsRole.Checkbox)!;
        Assert.True(on.HasFlag(SemanticsFlags.Checkable));
        Assert.True(on.HasFlag(SemanticsFlags.Checked));

        var off = First(root: Tree(new Checkbox(false)), role: SemanticsRole.Checkbox)!;
        Assert.True(off.HasFlag(SemanticsFlags.Checkable));
        Assert.False(off.HasFlag(SemanticsFlags.Checked));
    }

    [Fact]
    public void Switch_HasSwitchRoleAndCheckedState()
    {
        var node = First(
            root: Tree(new Switch(true) { SemanticsLabel = "Wi-Fi" }),
            role: SemanticsRole.Switch
        )!;
        Assert.Equal(expected: "Wi-Fi", actual: node.Label);
        Assert.True(node.HasFlag(SemanticsFlags.Checked));
    }

    [Fact]
    public void Slider_ReportsValueAndIncrementActions()
    {
        var node = First(root: Tree(new Slider(0.5f)), role: SemanticsRole.Slider)!;
        Assert.Equal(expected: "50%", actual: node.Value);
        Assert.True(node.HasAction(SemanticsAction.Increase));
        Assert.True(node.HasAction(SemanticsAction.Decrease));
    }

    [Fact]
    public void TextField_ReportsValueReadOnlyAndMultilineFlags()
    {
        var ro = First(
            root: Tree(
                new TextField {
                    Text = "hi",
                    ReadOnly = true,
                }
            ),
            role: SemanticsRole.TextField
        )!;
        Assert.Equal(expected: "hi", actual: ro.Value);
        Assert.True(ro.HasFlag(SemanticsFlags.ReadOnly));
        Assert.False(ro.HasFlag(SemanticsFlags.Multiline));

        var ml = First(
            root: Tree(
                new TextField {
                    Text = "a\nb",
                    Multiline = true,
                }
            ),
            role: SemanticsRole.TextField
        )!;
        Assert.True(ml.HasFlag(SemanticsFlags.Multiline));
    }

    [Fact]
    public void Label_IsTextOrHeaderByStyle()
    {
        Assert.Equal(
            expected: SemanticsRole.Text,
            actual: First(root: Tree(new Label("Body")), role: SemanticsRole.Text)!.Role
        );
        var header = First(
            root: Tree(new Label("Title") { Style = Label.LabelStyle.Title }),
            role: SemanticsRole.Header
        );
        Assert.NotNull(header);
        Assert.Equal(expected: "Title", actual: header!.Label);
    }

    [Fact]
    public void ExcludeSemantics_DropsTheSubtree()
    {
        // A decorative label opts its whole node out of the accessibility tree.
        Assert.Equal(
            expected: 1,
            actual: CountRole(root: Tree(new Label("read me")), role: SemanticsRole.Text)
        );
        Assert.Equal(
            expected: 0,
            actual: CountRole(
                root: Tree(new Label("ignore me") { Decorative = true }),
                role: SemanticsRole.Text
            )
        );
    }

    [Fact]
    public void TransparentContainers_HoistChildrenInsteadOfNesting()
    {
        // A Column has no semantics of its own, so its two buttons are siblings under the synthetic root.
        var tree = Tree(
            new Column {
                Children = {
                    new Button(label: "A", onPressed: () => { }),
                    new Button(label: "B", onPressed: () => { }),
                },
            }
        );
        Assert.Equal(
            expected: 0,
            actual: CountRole(root: tree, role: SemanticsRole.Group) - 1
        ); // only the synthetic root is a Group
        Assert.Equal(expected: 2, actual: CountRole(root: tree, role: SemanticsRole.Button));
    }

    [Fact]
    public void Describe_ReadsLikeAnAnnouncement()
    {
        var button = First(
            root: Tree(new Button(label: "Save", onPressed: () => { })),
            role: SemanticsRole.Button
        )!;
        Assert.Equal(expected: "Button: Save", actual: button.Describe());

        var check = First(root: Tree(new Checkbox(true)), role: SemanticsRole.Checkbox)!;
        Assert.Contains(expectedSubstring: "checked", actualString: check.Describe());
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
