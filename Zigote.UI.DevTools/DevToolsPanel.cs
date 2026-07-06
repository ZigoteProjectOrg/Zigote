using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Semantics;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Focus;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools;

/// <summary>
///     The docked devtools panel — a full retained widget tree pushed as its own overlay while open.
///     Header + category tab strip + panel tab strip + a scrollable body hosting the active
///     <see cref="IDevPanel" />'s widget. Non-modal: it opts out of auto-focus (<see cref="INoAutoFocus" />)
///     so opening it never steals focus, and closes on Escape (<see cref="IDismissableOverlay" />).
///     Everything outside the docked column falls through to the app (see <see cref="HitTest" />).
/// </summary>
public sealed class DevToolsPanel : StatefulWidget, IDismissableOverlay, INoAutoFocus
{
    public const float PanelWidth = 408f;

    private readonly DevToolsController _controller;

    public DevToolsPanel(DevToolsController controller)
    {
        _controller = controller;
    }

    public bool RequestDismiss()
    {
        // Esc exits select-widget mode first; a second Esc closes the panel.
        if (_controller.InspectMode)
        {
            _controller.InspectMode = false;
            _controller.HoverHighlight = null;
            return true;
        }

        if (!_controller.PanelOpen) return false;
        _controller.TogglePanel();
        return true;
    }

    // Only the docked column on the right is interactive; clicks elsewhere fall through to the app so
    // the panel never blocks the scene/UI behind it. (StatefulWidget.HitTest otherwise returns `this`
    // on a child-miss, which would swallow every click over the full-screen overlay.)
    public override Widget? HitTest(Offset point)
    {
        if (point.X < Bounds.Right - PanelWidth) return null;
        return base.HitTest(point);
    }

    protected override WidgetState CreateState()
    {
        return new DevToolsPanelState(_controller);
    }

    private sealed class DevToolsPanelState(DevToolsController controller) : WidgetState<DevToolsPanel>
    {
        public override Widget Build(BuildContext context)
        {
            var t = ThemeProvider.Of(context);

            var cats = controller.VisibleCategories();
            var catSel = Math.Max(0, cats.IndexOf(controller.Category));
            var catStrip = new DevTabStrip(
                cats.ConvertAll(c => c.Label()),
                catSel,
                i => SetStateRebuild(() => controller.SetCategory(cats[i])),
                emphasize: true);

            var panels = controller.PanelsIn(controller.Category);
            var panelSel = controller.SelectedIndex(controller.Category);
            var panelStrip = new DevTabStrip(
                panels.ConvertAll(p => p.Title),
                panelSel,
                i => SetStateRebuild(() => controller.SetSelected(controller.Category, i)));

            var active = controller.ActivePanel;
            Widget body = active is not null
                ? controller.WidgetFor(active, context)
                : new DevNote("No panels in this category.");

            var column = new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Max) {
                Children = {
                    Header(t),
                    new Padding(EdgeInsets.Only(left: Spacing.Md, right: Spacing.Md, top: Spacing.Xs),
                        catStrip),
                    new Padding(
                        EdgeInsets.Only(left: Spacing.Md, right: Spacing.Md, top: Spacing.Xs,
                            bottom: Spacing.Xs),
                        panelStrip),
                    new SizedBox(height: 1f, child: new DevFillBox(t.Separator)),
                    new Expanded(new ScrollView(new Padding(
                        EdgeInsets.All(Spacing.Md), body)) { ScrollVertical = true }),
                },
            };

            var chrome = new DecoratedBox {
                Fill = t.Panel,
                Child = column,
            };

            return new Stack {
                Children = {
                    new Positioned(chrome, top: 0, bottom: 0, right: 0, width: PanelWidth),
                },
            };
        }

        private Padding Header(ThemeData t)
        {
            var close = new Label("✕", DevKit.CaptionSize + 3f, t.Hint) { MaxLines = 1 };
            var closeBtn = new Pressable {
                Child = new Padding(EdgeInsets.All(Spacing.Xs), close),
                FocusRadius = 5f,
                Role = SemanticsRole.Button,
                SemanticsLabel = "Close devtools",
                OnPressed = () => controller.TogglePanel(),
            };

            return new Padding(
                EdgeInsets.Only(left: Spacing.Md, right: Spacing.Sm, top: Spacing.Sm),
                new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        new Label("DevTools", t.FontSizeBody, t.Primary)
                            { FontWeight = FontWeight.Bold, MaxLines = 1 },
                        new SizedBox(width: Spacing.Sm),
                        new Label(controller.Profile.Resolve() == DevToolsProfile.ThreeD
                            ? "3D" : "2D", DevKit.CaptionSize - 1f, t.Hint) { MaxLines = 1 },
                        new Spacer(),
                        closeBtn,
                    },
                }
            );
        }
    }
}
