using Zigote.Core;
using Zigote.Runtime.Scene;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Widgets;

/// <summary>Shared modal dialogs for project lifecycle (used by the welcome screen and the menu bar).</summary>
public static class ProjectDialogs
{
    /// <summary>Pick an existing .zigoteproj and open it.</summary>
    public static void ShowOpen(App app, Action<string> onOpen)
    {
        FilePickerDialog.Show(
            app,
            "Open Project",
            Directory.GetCurrentDirectory(),
            [".zigoteproj"],
            onOpen
        );
    }

    /// <summary>Prompt for a target folder, scaffold a new project there, and open it.</summary>
    public static void ShowNew(App app, ThemeData theme, Action<string> onOpen)
    {
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "ZigoteProjects",
            "MyProject"
        );
        var pathField =
            new TextField(decoration: new InputDecoration("Project folder")) { Text = defaultDir };

        Dialog? dialog = null;
        var body = new SizedBox(
            460f,
            child: new Padding(
                EdgeInsets.All(20f),
                new Column {
                    CrossAxisAlignment = CrossAxisAlignment.Stretch,
                    Children = {
                        new Label("New Project", theme.FontSizeTitle, theme.OnSurface),
                        new SizedBox(height: 6f),
                        new Label(
                            "A .zigoteproj, assets/ folder and starter scene are created here.",
                            theme.FontSizeCaption,
                            theme.Hint
                        ),
                        new SizedBox(height: 12f),
                        pathField,
                        new SizedBox(height: 16f),
                        new Row {
                            MainAxisAlignment = MainAxisAlignment.End,
                            Children = {
                                new Button("Cancel", () => dialog?.Dismiss()) {
                                    Style = ButtonStyle.Outlined,
                                },
                                new SizedBox(10f),
                                new Button(
                                    "Create",
                                    () =>
                                    {
                                        var dirPath = pathField.Text.Trim();
                                        if (string.IsNullOrWhiteSpace(dirPath)) return;
                                        try
                                        {
                                            var name = Path.GetFileName(dirPath.TrimEnd('/', '\\'));
                                            if (string.IsNullOrWhiteSpace(name)) name = "MyProject";
                                            var projPath = ZigoteProject.Scaffold(dirPath, name);
                                            dialog?.Dismiss();
                                            onOpen(projPath);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.Error.WriteLine(
                                                $"[Zigote] New project failed: {ex.Message}"
                                            );
                                            app.ShowSnackbar(
                                                $"Could not create project: {ex.Message}"
                                            );
                                        }
                                    }
                                ) { BackgroundColor = theme.Primary },
                            },
                        },
                    },
                }
            )
        );

        dialog = new Dialog(body, app) { Dismissible = true };
        dialog.Show();
    }

    /// <summary>Prompt for a path and save the current scene there (Save As).</summary>
    public static void ShowSaveSceneAs(App app, ThemeData theme, string currentPath,
        Action<string> onSave)
    {
        var pathField =
            new TextField(decoration: new InputDecoration("Scene path")) { Text = currentPath };

        Dialog? dialog = null;
        var body = new SizedBox(
            460f,
            child: new Padding(
                EdgeInsets.All(20f),
                new Column {
                    CrossAxisAlignment = CrossAxisAlignment.Stretch,
                    Children = {
                        new Label("Save Scene As", theme.FontSizeTitle, theme.OnSurface),
                        new SizedBox(height: 12f),
                        pathField,
                        new SizedBox(height: 16f),
                        new Row {
                            MainAxisAlignment = MainAxisAlignment.End,
                            Children = {
                                new Button("Cancel", () => dialog?.Dismiss()) {
                                    Style = ButtonStyle.Outlined,
                                },
                                new SizedBox(10f),
                                new Button(
                                    "Save",
                                    () =>
                                    {
                                        var p = pathField.Text.Trim();
                                        if (string.IsNullOrWhiteSpace(p)) return;
                                        dialog?.Dismiss();
                                        onSave(p);
                                    }
                                ) { BackgroundColor = theme.Primary },
                            },
                        },
                    },
                }
            )
        );

        dialog = new Dialog(body, app) { Dismissible = true };
        dialog.Show();
    }
}