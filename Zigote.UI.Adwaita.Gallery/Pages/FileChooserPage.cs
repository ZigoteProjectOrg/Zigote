using Zigote.Core.Engine;

namespace AdwaitaGallery.Pages;

/// <summary>
///     The in-app file chooser — what <c>FileDialog</c> falls back to where there is no native
///     dialog to call, and the same browser on every platform when an app wants one look.
/// </summary>
public sealed class FileChooserPage : ComposedWidget
{
    private readonly Signal<string> _result = new("(nothing chosen yet)");

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "File Chooser",
            description: "Open, save and pick a folder, in a browser the app draws itself.",
            iconName: MaterialIcons.FolderOpen
        ) {
            Children = {
                Demo.Group(
                    title: "Choosers",
                    description:
                    "Each opens in its own window with places, a breadcrumb path, search, " +
                    "sortable columns and a preview of the selected file.",
                    new AdwButtonRow(
                        title: "Open a File…",
                        onPressed: () => Choose(new AdwFileBrowserOptions { Kind = FileDialogKind.OpenFile })
                    ),
                    new AdwButtonRow(
                        title: "Open Several Images…",
                        onPressed: () => Choose(
                            new AdwFileBrowserOptions {
                                Kind = FileDialogKind.OpenFile,
                                AllowMany = true,
                                Filters = [
                                    new FileDialogFilter(name: "Images", extensions: ["png", "jpg", "jpeg", "webp"]),
                                    new FileDialogFilter(name: "All files", extensions: ["*"]),
                                ],
                            }
                        )
                    ),
                    new AdwButtonRow(
                        title: "Save As…",
                        onPressed: () => Choose(
                            new AdwFileBrowserOptions {
                                Kind = FileDialogKind.SaveFile,
                                SuggestedName = "untitled.txt",
                            }
                        )
                    ),
                    new AdwButtonRow(
                        title: "Choose a Folder…",
                        onPressed: () => Choose(new AdwFileBrowserOptions { Kind = FileDialogKind.PickFolder })
                    )
                ),
                new Watch(() => Demo.Group(
                        title: "Result",
                        description: null,
                        new AdwActionRow(title: "Chosen", subtitle: _result.Value)
                    )
                ),
            },
        };
    }

    private async void Choose(AdwFileBrowserOptions options)
    {
        if (App.Active is not { } app) return;
        string[] paths = await AdwFileBrowserDialog.ShowAsync(app: app, options: options);
        app.Post(() => _result.Value = paths.Length == 0 ? "(cancelled)" : string.Join(separator: "\n", values: paths));
    }
}
