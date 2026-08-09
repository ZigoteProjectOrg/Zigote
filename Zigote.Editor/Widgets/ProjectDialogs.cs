using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Runtime.Scene;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Widgets;

/// <summary>
///     Shared modal dialogs for project lifecycle (used by the welcome screen and the menu bar).
///     OS-level flows (open project, save scene, pick a folder) use the native OS file dialog and
///     fall back to the in-app picker/text field when the native backend is unavailable or fails
///     (see Zigote.Engine/docs/file-dialogs.md for the policy).
/// </summary>
public static class ProjectDialogs
{
    /// <summary>Pick an existing .zigoteproj and open it.</summary>
    public static void ShowOpen(App app, Action<string> onOpen)
    {
        Run(
            app,
            async () =>
            {
                var path = await FileDialog.OpenFileAsync(
                    "Open Project",
                    Directory.GetCurrentDirectory(),
                    [new FileDialogFilter("Zigote Project", "zigoteproj")]
                );
                if (path is not null) onOpen(path);
            }
        );
    }

    /// <summary>Prompt for a target folder, scaffold a new project there, and open it.</summary>
    public static void ShowNew(App app, Action<string> onOpen)
    {
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "ZigoteProjects",
            "MyProject"
        );
        var pathField = new AdwEntry { Placeholder = "Project folder", Text = defaultDir };

        // The project folder itself doesn't exist yet, so the native picker chooses the PARENT
        // directory and the field keeps the (editable) new folder name.
        async void BrowseLocation()
        {
            try
            {
                var current = pathField.Text.Trim();
                string? startDir = null;
                try
                {
                    var parent = Path.GetDirectoryName(current.TrimEnd('/', '\\'));
                    if (Directory.Exists(parent)) startDir = parent;
                }
                catch
                {
                    // Malformed text in the field — let the OS pick its default start location.
                }

                var picked = await FileDialog.PickFolderAsync("Choose Project Location", startDir);
                if (picked is null) return;
                var name = Path.GetFileName(current.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(name)) name = "MyProject";
                pathField.Text = Path.Combine(picked, name);
                app.RequestPaint();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Zigote] Folder picker failed: {ex.Message}");
            }
        }

        Widget pathRow = FileDialog.CanShowDialogs
            ? new Row {
                CrossAxisAlignment = CrossAxisAlignment.Center,
                Children = {
                    new Expanded(pathField),
                    new SizedBox(8f),
                    new AdwButton("Browse…", BrowseLocation),
                },
            }
            : pathField;

        var dialog = new AdwAlertDialog(
            "New Project",
            "A .zigoteproj, assets/ folder and starter scene are created here."
        ) {
            ExtraChild = pathRow,
            DefaultResponse = "create",
            CloseResponse = "cancel",
        };
        dialog.AddResponse("cancel", "Cancel");
        dialog.AddResponse("create", "Create", AdwResponseAppearance.Suggested);
        dialog.OnResponse = id =>
        {
            if (id != "create") return;
            var dirPath = pathField.Text.Trim();
            if (string.IsNullOrWhiteSpace(dirPath)) return;
            try
            {
                var name = Path.GetFileName(dirPath.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(name)) name = "MyProject";
                onOpen(ZigoteProject.Scaffold(dirPath, name));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Zigote] New project failed: {ex.Message}");
                app.ShowSnackbar($"Could not create project: {ex.Message}");
            }
        };
        dialog.Show();
    }

    /// <summary>Pick a path and save the current scene there (Save As).</summary>
    public static void ShowSaveSceneAs(App app, string currentPath, Action<string> onSave)
    {
        Run(
            app,
            async () =>
            {
                var full = Path.GetFullPath(
                    string.IsNullOrWhiteSpace(currentPath) ? "assets/main.scene" : currentPath
                );
                var picked = await FileDialog.SaveFileAsync(
                    "Save Scene As",
                    Path.GetDirectoryName(full),
                    Path.GetFileName(full),
                    [new FileDialogFilter("Zigote Scene", "scene")]
                );
                if (picked is not null) onSave(RelativizeToProject(picked));
            }
        );
    }

    /// <summary>
    ///     Run a dialog flow. FileDialog itself routes native → in-app browser, so failure here
    ///     means no dialog implementation at all — surface it instead of silently dropping it.
    /// </summary>
    private static async void Run(App app, Func<Task> flow)
    {
        try
        {
            await flow();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Zigote] File dialog failed: {ex.Message}");
            app.ShowSnackbar($"File dialog failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Paths inside the project are stored project-relative (the editor CWD is the project
    ///     root) so scenes stay portable; picks outside the project stay absolute.
    /// </summary>
    private static string RelativizeToProject(string path)
    {
        if (!Path.IsPathRooted(path)) return path;
        var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)
            ? path
            : rel.Replace('\\', '/');
    }
}