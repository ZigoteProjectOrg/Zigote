using Zigote.Core.State;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwNavigationSplitView — sidebar + content side by side; collapsed it shows one pane at a
///     time (<see cref="ShowContent" /> picks which). The host wires the collapsed back navigation
///     via its own header bar, reading <see cref="IsCollapsed" /> to know when there is anywhere to
///     go back to.
///     ponytail: collapsed, the pane swap is a single-frame cut. libadwaita slides the panes past
///     each other the way <see cref="AdwNavigationView" /> does; doing it here means the same
///     AnimationController + SlideTransition-over-a-ClipRect'd-Stack recipe, active only on the
///     collapsed branch.
/// </summary>
public sealed class AdwNavigationSplitView : ComposedWidget
{
    private Widget? _sidebar;
    private Widget? _content;
    private bool _collapsed;
    private bool _showContent;
    private float _sidebarWidth = AdwMetrics.SidebarWidth;
    private float _autoCollapseBelow;

    /// <summary>
    ///     The observable form of the collapsed state — including the <see cref="AutoCollapseBelow" />
    ///     breakpoint, which only the layout pass can decide. Hosts that need to follow the fold (a
    ///     header bar growing a back button once the panes have become one page) should watch this
    ///     instead of measuring the window and re-deriving the breakpoint themselves.
    /// </summary>
    public readonly Signal<bool> IsCollapsed = new(false);

    public Widget? Sidebar
    {
        get => _sidebar;
        set => this.Set(ref _sidebar, value);
    }

    public Widget? Content
    {
        get => _content;
        set => this.Set(ref _content, value);
    }

    public float SidebarWidth
    {
        get => _sidebarWidth;
        set => this.Set(ref _sidebarWidth, value);
    }

    public bool Collapsed
    {
        get => _collapsed;
        set => this.Set(ref _collapsed, value);
    }

    /// <summary>Collapsed only: show the content pane instead of the sidebar.</summary>
    public bool ShowContent
    {
        get => _showContent;
        set => this.Set(ref _showContent, value);
    }

    /// <summary>
    ///     Auto-collapse when the available width drops below this (e.g. 720). 0 disables — the
    ///     default, so a split view in a narrow demo frame stays side-by-side unless opted in.
    /// </summary>
    public float AutoCollapseBelow
    {
        get => _autoCollapseBelow;
        set => this.Set(ref _autoCollapseBelow, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        // One instance per branch, built on first use and kept: the builder runs on every
        // constraint change, i.e. every frame of a window-resize drag, and handing back a fresh
        // tree there detaches and re-attaches the whole sidebar + content each of those frames.
        Widget? sideBySide = null;
        Widget? collapsedSidebar = null;
        Widget? collapsedContent = null;

        return new LayoutBuilder((_, c) =>
            {
                var collapsed = Collapsed ||
                                (AutoCollapseBelow > 0f && c.MaxWidth < AutoCollapseBelow);
                // Peek, not Value: this runs during Measure, potentially inside a Watch's evaluation,
                // and reading the signal there would subscribe that Watch to a value it is writing.
                if (IsCollapsed.Peek() != collapsed) IsCollapsed.Value = collapsed;

                if (collapsed && ShowContent)
                    return collapsedContent ??= new Container {
                        Background = theme.Window,
                        Child = Content,
                    };

                if (collapsed)
                    return collapsedSidebar ??= new Container {
                        Background = p.SidebarBg,
                        Child = Sidebar,
                    };

                return sideBySide ??= new Row(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                    Children = {
                        new Container {
                            Width = SidebarWidth,
                            Background = p.SidebarBg,
                            Child = Sidebar,
                        },
                        new Container {
                            Width = 1f,
                            Background = p.SidebarShade,
                        },
                        new Expanded(
                            new Container {
                                Background = theme.Window,
                                Child = Content,
                            }
                        ),
                    },
                };
            }
        );
    }
}