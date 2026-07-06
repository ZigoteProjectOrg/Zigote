using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     A wrapping strip of selectable pill tabs. Used for both the category row and the panel row of the
///     devtools panel. Tabs/selection are set via <see cref="Set" />; the strip only rebuilds when they
///     actually change, and recolours the pills to reflect the active one.
/// </summary>
public sealed class DevTabStrip : StatefulWidget
{
    private int _selected;
    private IReadOnlyList<string> _tabs;

    public DevTabStrip(IReadOnlyList<string> tabs, int selected, Action<int> onSelect,
        bool emphasize = false)
    {
        _tabs = tabs;
        _selected = selected;
        OnSelect = onSelect;
        Emphasize = emphasize;
    }

    public Action<int> OnSelect { get; }

    /// <summary>Larger, accent-tinted pills for the top-level category row.</summary>
    public bool Emphasize { get; }

    public IReadOnlyList<string> Tabs => _tabs;
    public int Selected => _selected;

    /// <summary>Update the tab labels and/or the selected index, rebuilding only when changed.</summary>
    public void Set(IReadOnlyList<string> tabs, int selected)
    {
        var changed = selected != _selected || tabs.Count != _tabs.Count;
        if (!changed)
            for (var i = 0; i < tabs.Count; i++)
                if (tabs[i] != _tabs[i])
                {
                    changed = true;
                    break;
                }

        if (!changed) return;
        _tabs = tabs;
        _selected = selected;
        (InternalState as DevTabStripState)?.Rebuild();
    }

    protected override WidgetState CreateState()
    {
        return new DevTabStripState();
    }

    private sealed class DevTabStripState : WidgetState<DevTabStrip>
    {
        public void Rebuild()
        {
            SetStateRebuild(() => { });
        }

        public override Widget Build(BuildContext context)
        {
            var t = ThemeProvider.Of(context);
            var wrap = new Wrap { Spacing = Spacing.Xs, RunSpacing = Spacing.Xs };
            for (var i = 0; i < Widget.Tabs.Count; i++)
            {
                var idx = i;
                var selected = i == Widget.Selected;
                wrap.Children.Add(Chip(Widget.Tabs[i], selected, () => Widget.OnSelect(idx), t,
                    Widget.Emphasize));
            }

            return wrap;
        }

        private static Pressable Chip(string label, bool selected, Action onTap, ThemeData t,
            bool emphasize)
        {
            var fs = emphasize ? DevKit.CaptionSize + 0.5f : DevKit.CaptionSize;
            var box = new DecoratedBox {
                Radius = 5f,
                Child = new Padding(
                    EdgeInsets.Symmetric(Spacing.Sm, emphasize ? 4f : 3f),
                    new Label(label, fs, selected ? t.Primary : t.Hint) {
                        MaxLines = 1,
                        FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                    }
                ),
            };

            void Recolor(bool hovered)
            {
                box.Fill = selected
                    ? t.Primary.WithAlpha(emphasize ? 0.22f : 0.18f)
                    : hovered
                        ? t.ControlHover.WithAlpha(0.5f)
                        : Color.Transparent;
                box.BorderColor = selected ? t.Primary.WithAlpha(0.45f) : Color.Transparent;
            }

            Recolor(false);
            var press = new Pressable { Child = box, FocusRadius = 5f, OnPressed = onTap };
            press.OnStateChanged = () => Recolor(press.Hovered);
            return press;
        }
    }
}
