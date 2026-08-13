using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools;

/// <summary>Where a <see cref="DevToolsView" /> is being shown, which decides its header actions.</summary>
public enum DevToolsChrome
{
    /// <summary>The resizable column docked to the right of the host window.</summary>
    Docked,

    /// <summary>Covering the whole host window (desktop) or the whole screen (phone).</summary>
    Fullscreen,

    /// <summary>Its own OS window, which brings its own titlebar and close button.</summary>
    Window,
}

/// <summary>
///     The devtools UI itself — header bar, category switcher, panel strip and the active
///     <see cref="IDevPanel" />'s widget — with no opinion about where it lives. The docked/fullscreen
///     overlay (<see cref="DevToolsPanel" />) and the torn-off OS window both host one of these, so the
///     three presentations never drift apart.
///     <para>
///         Layout follows the width it is actually given, not the window's: the category switcher is an
///         icon AdwViewSwitcher in the header (so the chrome is one bar shorter and fits a phone as well
///         as a 408px dock), the panel strip below it scrolls horizontally when it does not fit, and a
///         wide pane clamps its content instead of stretching readouts across a 2000px window.
///     </para>
/// </summary>
public sealed class DevToolsView(DevToolsController controller, DevToolsChrome chrome)
    : ComposedWidget
{
    /// <summary>Content wider than this reads badly, so a wide pane centres a clamped column.</summary>
    private const float ClampWidth = 720f;

    // Through an AdaptiveBuilder so the arm is re-picked whenever the width this view is given
    // crosses a breakpoint — a torn-off window being resized, or the host window shrinking to
    // phone width. The ambient MediaQuery is not an inherited widget, so a plain Build() would
    // keep whatever layout it chose on the first frame.
    protected override Widget Build(BuildContext context)
    {
        return new AdaptiveBuilder(BuildArm, 0f);
    }

    private Widget BuildArm(BuildContext context, WindowSizeClass cls)
    {
        var t = ThemeProvider.Of(context);
        var mq = MediaQuery.Of(context);
        // Whether the surface is a real phone (no windows to open, safe-area insets to honour);
        // `cls` is the class of the width this view actually got, which drives the content clamp.
        var phone = mq.Width < 400f;

        var panels = controller.PanelsIn(controller.Category);
        var panelStrip = new DevTabStrip(
            panels.ConvertAll(p => p.Title),
            controller.SelectedIndex(controller.Category),
            i =>
            {
                controller.SetSelected(controller.Category, i);
                MarkNeedsBuild();
            }
        );

        var active = controller.ActivePanel;
        var body = active is not null
            ? controller.WidgetFor(active, context)
            : new DevNote("No panels in this category.");

        var view = new AdwToolbarView(
            new ScrollView(
                new Padding(
                    EdgeInsets.Only(
                        Spacing.Md,
                        right: Spacing.Md,
                        top: Spacing.Md,
                        // Clears the home indicator on a phone as well as the last row.
                        bottom: Spacing.Xl + mq.Padding.Bottom
                    ),
                    // Only a wide pane needs clamping; narrower ones already read fine.
                    cls == WindowSizeClass.Expanded ? new AdwClamp(body, ClampWidth) : body
                )
            ) { ScrollVertical = true }
        ) {
            RaisedTopBar = true,
            TopBars = {
                Header(mq, phone),
                Bar(panelStrip),
            },
        };

        return new DecoratedBox {
            Fill = t.Window,
            Child = view,
        };
    }

    /// <summary>The standard inset around a toolbar strip — aligned with the content below it.</summary>
    private static Widget Bar(Widget child)
    {
        return new Padding(EdgeInsets.Symmetric(Spacing.Md, Spacing.Xs), child);
    }

    // An Adwaita header bar's shape and type, hand-rolled rather than an AdwHeaderBar: that one
    // registers itself as a CSD drag surface, and dragging a docked in-app panel's title should
    // not move the host window.
    private Widget Header(MediaQueryData mq, bool phone)
    {
        const float buttonSize = 34f;
        var actions = new Row(
            spacing: Spacing.Xxs,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        );

        // Windows and fullscreen are desktop ideas; a phone gets the close button only. A narrow
        // desktop window still keeps its actions — dropping the dock button would strand it.
        if (!phone)
            switch (chrome)
            {
                case DevToolsChrome.Docked:
                    actions.Children.Add(
                        Action(
                            MaterialIcons.OpenInFull,
                            "Fullscreen",
                            controller.ToggleFullscreen
                        )
                    );
                    actions.Children.Add(
                        Action(
                            MaterialIcons.OpenInNew,
                            "Open in a window",
                            controller.OpenWindow
                        )
                    );
                    break;
                case DevToolsChrome.Fullscreen:
                    actions.Children.Add(
                        Action(
                            MaterialIcons.CloseFullscreen,
                            "Leave fullscreen",
                            controller.ToggleFullscreen
                        )
                    );
                    actions.Children.Add(
                        Action(
                            MaterialIcons.OpenInNew,
                            "Open in a window",
                            controller.OpenWindow
                        )
                    );
                    break;
                case DevToolsChrome.Window:
                    actions.Children.Add(
                        Action(
                            MaterialIcons.Dock,
                            "Dock back into the app",
                            controller.DockWindow
                        )
                    );
                    break;
            }

        if (chrome != DevToolsChrome.Window)
            actions.Children.Add(Action(Icons.Close, "Close devtools", controller.TogglePanel));

        // Torn-off window: the real thing, so it carries the window buttons on whichever side
        // the system's button-layout puts them and drags the window like any GNOME headerbar.
        if (chrome == DevToolsChrome.Window)
            return new AdwHeaderBar {
                Flat = true,
                TitleWidget = CategorySwitcher(),
                End = { actions },
            };

        var header = new SizedBox(
            height: AdwMetrics.HeaderBarHeight,
            child: new Padding(
                EdgeInsets.Symmetric(6f),
                new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        // Balances the actions so the switcher stays centred on the bar.
                        new SizedBox(buttonSize * actions.Children.Count),
                        new Expanded(new Center(CategorySwitcher())),
                        actions,
                    },
                }
            )
        );

        // Under a status bar / notch the header has to start below it.
        return mq.Padding.Top > 0f
            ? new Padding(EdgeInsets.Only(top: mq.Padding.Top), header)
            : header;
    }

    /// <summary>
    ///     The category view switcher, in the header where libadwaita puts an AdwViewSwitcher. Icons
    ///     rather than labels: three text segments plus the window actions do not fit a 408px docked
    ///     column, and the panel strip right below already spells out where you are.
    /// </summary>
    private Widget CategorySwitcher()
    {
        var cats = controller.VisibleCategories();
        return new AdwToggleGroup(
            cats.ConvertAll(c => new AdwToggle(null, c.Icon(), c.Label())),
            Math.Max(0, cats.IndexOf(controller.Category)),
            i =>
            {
                controller.SetCategory(cats[i]);
                MarkNeedsBuild();
            }
        );
    }

    private static AdwButton Action(string icon, string label, Action onPressed)
    {
        return new AdwButton(label, onPressed) {
            IconName = icon,
            Style = AdwButtonStyle.Flat,
            Circular = true,
        };
    }
}
