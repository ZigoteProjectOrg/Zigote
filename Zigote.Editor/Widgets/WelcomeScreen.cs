using Zigote.Editor.Settings;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Widgets;

/// <summary>
///     First screen shown when the editor launches without a project to reopen: an Adwaita status
///     page offering New / Open, with the recent projects as a boxed list under it. Selecting a
///     project invokes <c>onOpen</c>, which swaps the app root to the editor shell. It carries its
///     own header bar because it IS the window root — under GNOME CSD nothing else would host the
///     window buttons.
/// </summary>
public sealed class WelcomeScreen : ComposedWidget
{
    private readonly App _app;
    private readonly ProjectHistory _history;
    private readonly Action<string> _onOpen;
    private readonly ThemeData _theme;

    public WelcomeScreen(App app, ThemeData theme, ProjectHistory history, Action<string> onOpen)
    {
        _app = app;
        _theme = theme;
        _history = history;
        _onOpen = onOpen;
    }

    protected override Widget Build(BuildContext context)
    {
        var actions = new Row(spacing: 12f, mainAxisSize: MainAxisSize.Min) {
            Children = {
                new AdwButton(
                    label: "New Project",
                    onPressed: () => ProjectDialogs.ShowNew(app: _app, onOpen: _onOpen)
                ) {
                    Style = AdwButtonStyle.Suggested,
                    Pill = true,
                },
                new AdwButton(
                    label: "Open Project…",
                    onPressed: () => ProjectDialogs.ShowOpen(app: _app, onOpen: _onOpen)
                ) {
                    Pill = true,
                },
            },
        };

        var recent = new AdwPreferencesGroup("Recent");
        string[] recentProjects = _history.Recent.Value;
        if (recentProjects.Length == 0)
        {
            recent.Rows.Add(
                new AdwActionRow(
                    title: "No recent projects yet",
                    subtitle: "Create or open one to begin"
                ) {
                    Enabled = false,
                }
            );
        }
        else
        {
            foreach (string path in recentProjects)
                recent.Rows.Add(RecentRow(path));
        }

        var page = new AdwStatusPage {
            IconName = Icons.Cube,
            Title = "Zigote Editor",
            Description = "Open a project to begin.",
            Child = new Column(spacing: 24f, crossAxisAlignment: CrossAxisAlignment.Stretch) {
                Children = {
                    new Center(actions),
                    recent,
                },
            },
        };

        return new AdwToolbarView(new ScrollView(new AdwClamp(page))) {
            TopBars = { new AdwHeaderBar { Flat = true } },
        };
    }

    private Widget RecentRow(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string dir = Path.GetDirectoryName(path) ?? "";
        bool exists = File.Exists(path);
        return new AdwActionRow(title: name, subtitle: exists ? dir : $"{dir}   (missing)") {
            IconName = exists ? Icons.Folder : Icons.Warning,
            ShowChevron = true,
            OnActivated = () =>
            {
                if (exists)
                {
                    _onOpen(path);
                    return;
                }

                // A project that moved or was deleted drops off the list on the click that
                // discovers it, rather than sitting there un-openable.
                _history.Forget(path);
                Invalidate();
                _app.RequestLayout();
            },
        };
    }
}
