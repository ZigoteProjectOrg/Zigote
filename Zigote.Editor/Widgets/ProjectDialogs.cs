using Zigote.Core.Engine;
using Zigote.Runtime.Scene;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
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
            app: app,
            flow: async () =>
            {
                string? path = await FileDialog.OpenFileAsync(
                    title: "Open Project",
                    startDirectory: Directory.GetCurrentDirectory(),
                    filters: [new FileDialogFilter(name: "Zigote Project", "zigoteproj")]
                );
                if (path is not null) onOpen(path);
            }
        );
    }

    /// <summary>Prompt for a target folder, scaffold a new project there, and open it.</summary>
    public static void ShowNew(App app, Action<string> onOpen)
    {
        string defaultDir = Path.Combine(
            path1: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            path2: "ZigoteProjects",
            path3: "MyProject"
        );
        var pathField = new AdwEntry {
            Placeholder = "Project folder",
            Text = defaultDir,
        };

        // The project folder itself doesn't exist yet, so the native picker chooses the PARENT
        // directory and the field keeps the (editable) new folder name.
        async void BrowseLocation()
        {
            try
            {
                string current = pathField.Text.Trim();
                string? startDir = null;
                try
                {
                    string? parent = Path.GetDirectoryName(current.TrimEnd('/', '\\'));
                    if (Directory.Exists(parent)) startDir = parent;
                }
                catch
                {
                    // Malformed text in the field — let the OS pick its default start location.
                }

                string? picked = await FileDialog.PickFolderAsync(
                    title: "Choose Project Location",
                    startDirectory: startDir
                );
                if (picked is null) return;
                string name = Path.GetFileName(current.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(name)) name = "MyProject";
                pathField.Text = Path.Combine(path1: picked, path2: name);
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
                    new AdwButton(label: "Browse…", onPressed: BrowseLocation),
                },
            }
            : pathField;

        var dialog = new AdwAlertDialog(
            heading: "New Project",
            body: "A .zigoteproj, assets/ folder and starter scene are created here."
        ) {
            ExtraChild = pathRow,
            DefaultResponse = "create",
            CloseResponse = "cancel",
        };
        dialog.AddResponse(id: "cancel", label: "Cancel");
        dialog.AddResponse(
            id: "create",
            label: "Create",
            appearance: AdwResponseAppearance.Suggested
        );
        dialog.OnResponse = id =>
        {
            if (id != "create") return;
            string dirPath = pathField.Text.Trim();
            if (string.IsNullOrWhiteSpace(dirPath)) return;
            try
            {
                string name = Path.GetFileName(dirPath.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(name)) name = "MyProject";
                onOpen(ZigoteProject.Scaffold(projectDir: dirPath, name: name));
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
            app: app,
            flow: async () =>
            {
                string full = Path.GetFullPath(
                    string.IsNullOrWhiteSpace(currentPath) ? "assets/main.scene" : currentPath
                );
                string? picked = await FileDialog.SaveFileAsync(
                    title: "Save Scene As",
                    startDirectory: Path.GetDirectoryName(full),
                    suggestedName: Path.GetFileName(full),
                    filters: [new FileDialogFilter(name: "Zigote Scene", "scene")]
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
        string rel = Path.GetRelativePath(relativeTo: Directory.GetCurrentDirectory(), path: path);
        return rel.StartsWith(value: "..", comparisonType: StringComparison.Ordinal) ||
               Path.IsPathRooted(rel)
            ? path
            : rel.Replace(oldChar: '\\', newChar: '/');
    }
}
