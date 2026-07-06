using Zigote.Core;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Widgets;

/// <summary>
///     First screen shown when the editor launches without a project to reopen.
///     Lists recent projects and offers New / Open actions. Selecting a project
///     invokes <c>onOpen</c>, which swaps the app root to the editor shell.
/// </summary>
public sealed class WelcomeScreen : StatelessWidget
{
    private readonly App _app;
    private readonly EditorConfig _config;
    private readonly Action<string> _onOpen;
    private readonly ThemeData _theme;

    public WelcomeScreen(App app, ThemeData theme, EditorConfig config, Action<string> onOpen)
    {
        _app = app;
        _theme = theme;
        _config = config;
        _onOpen = onOpen;
    }

    protected override Widget Build(BuildContext context)
    {
        var recents = new Column { CrossAxisAlign = CrossAxisAlignment.Stretch };
        if (_config.RecentProjects.Count == 0)
            recents.Children.Add(
                new Padding(
                    EdgeInsets.All(10f),
                    new Label("No recent projects yet.", _theme.FontSizeBody, _theme.Hint)
                )
            );
        else
            foreach (var path in _config.RecentProjects)
                recents.Children.Add(RecentRow(path));

        var content = new Column {
            CrossAxisAlign = CrossAxisAlignment.Stretch,
            Children = {
                new Label("Zigote Editor", 28f, _theme.OnBackground),
                new SizedBox(height: 4f),
                new Label("Open a project to begin.", _theme.FontSizeBody, _theme.Hint),
                new SizedBox(height: 18f),
                new Row {
                    Children = {
                        new Button(
                            "New Project",
                            () => ProjectDialogs.ShowNew(_app, _theme, _onOpen)
                        ) { BackgroundColor = _theme.Primary },
                        new SizedBox(10f),
                        new Button("Open Project…", () => ProjectDialogs.ShowOpen(_app, _onOpen)) {
                            Style = ButtonStyle.Outlined,
                        },
                    },
                },
                new SizedBox(height: 18f),
                new Label("Recent", _theme.FontSizeCaption, _theme.Hint),
                new SizedBox(height: 4f),
                new Expanded(new ScrollView(recents)),
            },
        };

        return new ColoredBox(
            _theme.Background,
            new Center(
                new SizedBox(
                    560f,
                    520f,
                    new Card(new Padding(EdgeInsets.All(24f), content)) { Color = _theme.Surface }
                )
            )
        );
    }

    private Widget RecentRow(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dir = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        var exists = File.Exists(path);
        var label = exists ? $"{name}   ·   {dir}" : $"{name}   ·   {dir}   (missing)";

        return new Padding(
            EdgeInsets.Only(bottom: 4f),
            new Button(
                label,
                () =>
                {
                    if (exists)
                    {
                        _onOpen(path);
                    }
                    else
                    {
                        _config.Forget(path);
                        Invalidate();
                        _app.RequestLayout();
                    }
                }
            ) {
                Style = ButtonStyle.Flat,
                FontSize = _theme.FontSizeBody,
                TextColor = exists ? _theme.OnSurface : _theme.Hint,
            }
        );
    }
}