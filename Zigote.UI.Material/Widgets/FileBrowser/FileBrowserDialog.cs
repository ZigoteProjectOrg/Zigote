using System.Runtime.CompilerServices;
using Zigote.Core.Engine;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>Configuration for <see cref="FileBrowserDialog.ShowAsync" />.</summary>
public sealed class FileBrowserOptions
{
    public FileDialogKind Kind { get; init; } = FileDialogKind.OpenFile;
    public string? Title { get; init; }
    public string? StartDirectory { get; init; }
    public string? SuggestedName { get; init; }
    public FileDialogFilter[]? Filters { get; init; }
    public string? AcceptLabel { get; init; }
    public bool AllowMany { get; init; }
    public bool ShowHidden { get; init; }
    public bool CanCreateDirectories { get; init; } = true;

    /// <summary>Clamp navigation to this subtree and hide the places sidebar — for pickers that
    ///     must stay inside a project.</summary>
    public string? LockRoot { get; init; }

    public static FileBrowserOptions From(FileDialogRequest request)
    {
        return new FileBrowserOptions {
            Kind = request.Kind,
            Title = request.Title,
            StartDirectory = request.Directory,
            SuggestedName = request.FileName,
            Filters = request.Filters,
            AcceptLabel = request.AcceptLabel,
            AllowMany = request.AllowMany,
            ShowHidden = request.ShowHidden,
            CanCreateDirectories = request.CanCreateDirectories,
        };
    }
}

/// <summary>
///     The in-app, cross-platform file/folder dialog: places sidebar, back/forward/up history,
///     clickable breadcrumbs, sortable Name/Size/Modified columns, search-as-you-type, filter
///     dropdown, hidden-files toggle, New Folder, multi-select, a save mode with name prefill +
///     overwrite confirmation, and full keyboard control. Serves as
///     <see cref="FileDialog.ManagedBackend" /> (registered automatically when this assembly
///     loads), so every FileDialog call falls back here when the native OS dialog is unavailable,
///     disabled, or fails.
/// </summary>
public sealed class FileBrowserDialog : StatefulWidget, IDismissableOverlay
{
    private const uint WindowWidth = 780;
    private const uint WindowHeight = 540;

    internal Dialog? Host;
    internal App? HostWindow;
    internal TaskCompletionSource<string[]> Tcs = new();

    /// <summary>
    ///     The browser is also embeddable as a plain widget (no modal host): construct with
    ///     <see cref="Options" /> and await <see cref="Result" /> — cancel/confirm complete it;
    ///     without a host there is just no dialog to dismiss.
    /// </summary>
    public FileBrowserOptions Options { get; set; } = new();

    /// <summary>Chosen absolute paths; empty = cancelled.</summary>
    public Task<string[]> Result => Tcs.Task;

    /// <summary>Esc (via App.HandleEscape when window-rooted, or a future host) = cancel.</summary>
    public bool RequestDismiss()
    {
        CompleteAndClose([]);
        return true;
    }

    /// <summary>
    ///     Show the browser as its own OS window — movable/resizable and it never covers the
    ///     content the user is picking for — centered over <paramref name="app" />'s window.
    ///     Falls back to an in-window modal overlay when a secondary window can't be created.
    ///     Resolves with the chosen absolute paths, or an empty array on cancel (Cancel button,
    ///     Esc, titlebar ✕ / scrim click).
    /// </summary>
    public static Task<string[]> ShowAsync(App app, FileBrowserOptions options)
    {
        var picker = new FileBrowserDialog { Options = options };
        try
        {
            var win = app.CreateWindow(DefaultTitle(options), WindowWidth, WindowHeight);
            win.Theme = app.Theme;
            // Titlebar ✕ destroys the window App after this fires; a confirm/cancel completed
            // the task first and makes it a no-op.
            win.CloseRequested += () => picker.Tcs.TrySetResult([]);
            picker.HostWindow = win;
            // Window chrome (macOS unified / Adwaita CSD) is app-wide: the window inherited it
            // from its parent App at CreateWindow, and the App wraps this root in the titlebar
            // strip automatically.
            win.Root = picker;
            CenterOverParent(app, win);
            return picker.Tcs.Task;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[FileBrowser] Separate dialog window unavailable ({ex.Message}) — " +
                "showing the in-window dialog instead."
            );
        }

        var host = new BrowserHost(picker, app) {
            Dismissible = true,
            WidthFraction = 0.62f,
            HeightFraction = 0.7f,
        };
        // Every close path (Cancel button, Esc, scrim click) funnels through Dismiss →
        // OnClosed; a confirm completed the task first, making this a no-op.
        host.OnClosed = () => picker.Tcs.TrySetResult([]);
        picker.Host = host;
        host.Show();
        return picker.Tcs.Task;
    }

    internal static string DefaultTitle(FileBrowserOptions options)
    {
        return options.Title ?? options.Kind switch {
            FileDialogKind.PickFolder => "Choose Folder",
            FileDialogKind.SaveFile => "Save As",
            _ => "Open",
        };
    }

    /// <summary>Complete the task and tear down whichever host is presenting the browser.</summary>
    internal void CompleteAndClose(string[] paths)
    {
        Tcs.TrySetResult(paths);
        if (HostWindow is { } win)
        {
            HostWindow = null;
            win.Close();
            return;
        }

        Host?.Dismiss();
    }

    private static void CenterOverParent(App parent, App win)
    {
        try
        {
            var (px, py) = parent.NativeWindow is { } parentWindow
                ? parentWindow.GetPosition()
                : parent.Engine.MainWindowPosition();
            var x = px + (int)((parent.HostLogicalWidth - WindowWidth) / 2f);
            var y = py + (int)((parent.HostLogicalHeight - WindowHeight) / 2f);
            win.NativeWindow?.SetPosition(Math.Max(0, x), Math.Max(0, y));
        }
        catch
        {
            // Positioning is cosmetic — the OS default placement is an acceptable fallback.
        }
    }

    protected override WidgetState CreateState()
    {
        return new FileBrowserDialogState();
    }

    /// <summary>INoAutoFocus: the state focuses the list (or the save-name field) itself instead
    ///     of letting the overlay auto-focus the first toolbar button.</summary>
    private sealed class BrowserHost(Widget content, App app) : Dialog(content, app), INoAutoFocus;
}

internal sealed class FileBrowserDialogState : WidgetState<FileBrowserDialog>
{
    private int _activeFilter;
    private FileDialogFilter[] _filters = [];
    private string[] _filterLabels = [];
    private FileBrowserHeader _header = null!;
    private FileBrowserList _list = null!;
    private FileBrowserModel _model = null!;
    private TextField _nameField = null!;
    private List<FileBrowserPlaces.Place> _places = [];
    private ScrollView _scroll = null!;
    private SearchField _search = null!;
    private ScrollView _sidebarScroll = null!;

    private FileBrowserOptions Options => Widget.Options;

    public override void InitState()
    {
        var o = Options;
        _model = new FileBrowserModel { LockRoot = o.LockRoot };
        _model.ShowHidden = o.ShowHidden;
        _model.DirectoriesOnly = o.Kind == FileDialogKind.PickFolder;
        _model.AllowMultiSelect = o.AllowMany && o.Kind == FileDialogKind.OpenFile;

        SetUpFilters();

        _list = new FileBrowserList(_model) {
            OnActivate = OnActivate,
            OnSelectionChanged = () => SetStateRebuild(() => { }),
            OnNavigateUp = GoUp,
        };
        _scroll = new ScrollView { Child = _list };
        _list.Scroll = _scroll;
        _header = new FileBrowserHeader(_model) {
            OnSort = column => SetStateRebuild(() => _model.SortBy(column)),
        };
        _search = new SearchField("Search", OnSearchChanged);
        _sidebarScroll = new ScrollView();
        _nameField = new TextField(
            onChanged: _ => SetStateRebuild(() => { }),
            onSubmitted: _ => Confirm(),
            decoration: new InputDecoration("File name")
        ) { Text = o.SuggestedName ?? "" };

        _places = o.LockRoot is null ? FileBrowserPlaces.Build() : [];

        _model.NavigateTo(ResolveStartDirectory());

        // Owner is the hosting window's App when the browser runs as a separate OS window.
        (Widget.Owner ?? App.Active)?.RequestFocus(
            o.Kind == FileDialogKind.SaveFile ? _nameField : _list
        );
    }

    private string ResolveStartDirectory()
    {
        foreach (var candidate in (string?[]) [Options.StartDirectory, Options.LockRoot])
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (Directory.Exists(candidate)) return candidate;
            // A file path (e.g. a previous selection) starts in its directory.
            var parent = Path.GetDirectoryName(candidate);
            if (parent is not null && Directory.Exists(parent)) return parent;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void SetUpFilters()
    {
        var list = (Options.Filters ?? [])
            .Where(f => f.Extensions is { Count: > 0 })
            .ToList();
        // Open dialogs offer an explicit escape hatch to everything (save keeps its format list).
        if (list.Count > 0 && Options.Kind == FileDialogKind.OpenFile &&
            !list.Any(f => f.Extensions.Contains("*")))
            list.Add(new FileDialogFilter("All Files", "*"));

        _filters = list.ToArray();
        _filterLabels = _filters.Select(LabelOf).ToArray();
        _activeFilter = 0;
        _model.ExtensionFilter = _filters.Length > 0 ? NormalizedExts(_filters[0]) : null;
        return;

        static string LabelOf(FileDialogFilter f)
        {
            var exts = NormalizedExts(f);
            return exts is null ? f.Name : $"{f.Name} ({string.Join(", ", exts.Select(e => "." + e))})";
        }
    }

    /// <summary>Extensions without dots, or null when the filter admits everything.</summary>
    private static string[]? NormalizedExts(FileDialogFilter filter)
    {
        var exts = filter.Extensions
            .Select(e => e.TrimStart('*', '.'))
            .Select(e => e.Length == 0 ? "*" : e)
            .ToArray();
        return exts.Contains("*") ? null : exts;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    public override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var body = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children = {
                BuildToolbar(theme),
                new Divider(),
                new Expanded(BuildBody()),
                new Divider(),
                BuildBottomBar(theme),
            },
        };
        // As a separate OS window the titlebar names the dialog — either the OS's own (System
        // chrome) or the app-injected WindowTitleBar strip (unified/CSD). The in-content title
        // row only earns its place in the overlay/embedded presentations.
        if (Widget.HostWindow is null)
            body.Children.Insert(
                0,
                new Padding(
                    new EdgeInsets(16f, 12f, 16f, 8f),
                    new Label(TitleText(), theme.FontSizeTitle, theme.OnSurface)
                )
            );
        else
            body.Children.Insert(0, new SizedBox(height: 6f));
        return body;
    }

    private string TitleText()
    {
        return FileBrowserDialog.DefaultTitle(Options);
    }

    private Widget BuildToolbar(ThemeData theme)
    {
        var crumbs = new Row { CrossAxisAlignment = CrossAxisAlignment.Center };
        var segments = BreadcrumbSegments();
        var start = 0;
        if (segments.Count > 5)
        {
            crumbs.Children.Add(CrumbButton(segments[0], theme));
            crumbs.Children.Add(new Label("…", theme.FontSizeCaption, theme.TextMuted));
            start = segments.Count - 3;
        }

        for (var i = start; i < segments.Count; i++)
        {
            if (crumbs.Children.Count > 0)
                crumbs.Children.Add(new IconGlyph(Icons.ChevronRight, 12f, theme.TextMuted));
            crumbs.Children.Add(CrumbButton(segments[i], theme));
        }

        return new Padding(
            new EdgeInsets(10f, 0f, 10f, 6f),
            new Row {
                CrossAxisAlignment = CrossAxisAlignment.Center,
                Children = {
                    new IconButton(
                        new IconGlyph(Icons.ArrowBack, 18f),
                        _model.CanGoBack ? () => Navigate(_model.GoBack) : null,
                        tooltip: "Back"
                    ),
                    new IconButton(
                        new IconGlyph(Icons.ArrowForward, 18f),
                        _model.CanGoForward ? () => Navigate(_model.GoForward) : null,
                        tooltip: "Forward"
                    ),
                    new IconButton(
                        new IconGlyph(Icons.ArrowUpward, 18f),
                        _model.CanGoUp ? GoUp : null,
                        tooltip: "Up one level"
                    ),
                    new SizedBox(6f),
                    // Height-capped: a bare ScrollView measures to its max constraints and would
                    // otherwise stretch the toolbar row over the whole dialog.
                    new Expanded(
                        new SizedBox(
                            height: 28f,
                            child: new ScrollView {
                                Child = crumbs,
                                ScrollHorizontal = true,
                                ScrollVertical = false,
                            }
                        )
                    ),
                    new SizedBox(8f),
                    new SizedBox(190f, child: _search),
                },
            }
        );
    }

    private Widget CrumbButton((string Label, string Path) segment, ThemeData theme)
    {
        var isCurrent = string.Equals(
            segment.Path,
            _model.CurrentDirectory,
            StringComparison.OrdinalIgnoreCase
        );
        return new Button(segment.Label, isCurrent ? null : () => NavigateTo(segment.Path)) {
            Style = ButtonStyle.Flat,
            FontSize = theme.FontSizeCaption,
            TextColor = isCurrent ? theme.OnSurface : theme.TextSecondary,
            Padding = EdgeInsets.All(4f),
        };
    }

    private Widget BuildBody()
    {
        var listColumn = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children = { _header, new Expanded(_scroll) },
        };
        if (_places.Count == 0) return listColumn;

        _sidebarScroll.Child = BuildSidebar();
        return new Row {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children = {
                new SizedBox(156f, child: _sidebarScroll),
                new Divider { Vertical = true },
                new Expanded(listColumn),
            },
        };
    }

    private Widget BuildSidebar()
    {
        var col = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min, // size to content inside the (unbounded) scroll
        };
        foreach (var place in _places)
        {
            var isCurrent = string.Equals(
                Path.TrimEndingDirectorySeparator(place.Path),
                Path.TrimEndingDirectorySeparator(_model.CurrentDirectory),
                StringComparison.OrdinalIgnoreCase
            );
            var target = place.Path;
            col.Children.Add(
                new ListTile(
                    new IconGlyph(place.Icon, 16f),
                    new Label(place.Label) { Style = Label.LabelStyle.Caption },
                    onPressed: () => NavigateTo(target),
                    selected: isCurrent
                )
            );
        }

        return col;
    }

    private Widget BuildBottomBar(ThemeData theme)
    {
        // MainAxisSize.Min: the default (Max) makes this fixed child swallow the whole leftover
        // height during the flex measure pass and starve the Expanded body to zero.
        var col = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };
        if (Options.Kind == FileDialogKind.SaveFile)
            col.Children.Add(
                new Padding(
                    new EdgeInsets(16f, 10f, 16f, 0f),
                    new Row {
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new Label("Name:", theme.FontSizeCaption, theme.TextSecondary),
                            new SizedBox(8f),
                            new Expanded(_nameField),
                        },
                    }
                )
            );

        var actions = new Row { CrossAxisAlignment = CrossAxisAlignment.Center };
        actions.Children.Add(new Checkbox(_model.ShowHidden, ToggleHidden) { Size = 14f });
        actions.Children.Add(new SizedBox(6f));
        actions.Children.Add(new Label("Hidden", theme.FontSizeCaption, theme.TextSecondary));

        if (Options.CanCreateDirectories && Options.Kind != FileDialogKind.OpenFile)
        {
            actions.Children.Add(new SizedBox(14f));
            actions.Children.Add(
                new Button("New Folder", PromptNewFolder) {
                    Style = ButtonStyle.Outlined,
                    FontSize = theme.FontSizeCaption,
                }
            );
        }

        actions.Children.Add(new SizedBox(14f));
        actions.Children.Add(
            new Label(StatusText(), theme.FontSizeCaption, theme.TextMuted)
        );
        actions.Children.Add(new Spacer());

        if (_filterLabels.Length > 0)
        {
            actions.Children.Add(
                new SizedBox(
                    220f,
                    child: new Dropdown<string>(_filterLabels, _activeFilter, OnFilterChanged) {
                        Height = 26f,
                    }
                )
            );
            actions.Children.Add(new SizedBox(10f));
        }

        actions.Children.Add(new Button("Cancel", Cancel) { Style = ButtonStyle.Outlined });
        actions.Children.Add(new SizedBox(8f));
        actions.Children.Add(
            new Button(AcceptLabel(), CanAccept() ? Confirm : null) {
                BackgroundColor = theme.Primary,
            }
        );

        col.Children.Add(new Padding(new EdgeInsets(16f, 10f, 16f, 12f), actions));
        return col;
    }

    private string StatusText()
    {
        var count = _model.Visible.Count;
        var items = count == 1 ? "1 item" : $"{count} items";
        var selected = _model.SelectedPaths.Count;
        return selected > 1 ? $"{items} · {selected} selected" : items;
    }

    private string AcceptLabel()
    {
        return Options.AcceptLabel ?? Options.Kind switch {
            FileDialogKind.PickFolder => "Choose",
            FileDialogKind.SaveFile => "Save",
            _ => "Open",
        };
    }

    private bool CanAccept()
    {
        return Options.Kind switch {
            FileDialogKind.OpenFile => _model.SelectedPaths.Count > 0,
            FileDialogKind.SaveFile => _nameField.Text.Trim().Length > 0,
            _ => true, // folder mode accepts the current directory
        };
    }

    private List<(string Label, string Path)> BreadcrumbSegments()
    {
        var segments = new List<(string, string)>();
        var stopAt = Options.LockRoot is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Options.LockRoot));
        var dir = _model.CurrentDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(dir);
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = trimmed.Length > 0 ? trimmed : dir;
            segments.Insert(0, (name, dir));
            if (stopAt is not null &&
                string.Equals(trimmed, stopAt, StringComparison.OrdinalIgnoreCase)) break;
            var parent = Path.GetDirectoryName(trimmed);
            if (parent is null || parent == dir) break;
            dir = parent;
        }

        return segments;
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void Navigate(Action move)
    {
        SetStateRebuild(() =>
            {
                move();
                AfterNavigate();
            }
        );
    }

    private void NavigateTo(string path)
    {
        Navigate(() => _model.NavigateTo(path));
    }

    private void GoUp()
    {
        if (_model.CanGoUp) Navigate(_model.GoUp);
    }

    private void AfterNavigate()
    {
        _list.ResetCursor();
        if (_search.Text.Length > 0)
        {
            _search.Text = "";
            _model.SearchText = "";
            _model.ApplyView();
        }
    }

    private void OnSearchChanged(string text)
    {
        SetStateRebuild(() =>
            {
                _model.SearchText = text;
                _model.ApplyView();
            }
        );
    }

    private void ToggleHidden(bool value)
    {
        SetStateRebuild(() =>
            {
                _model.ShowHidden = value;
                _model.ApplyView();
            }
        );
    }

    private void OnFilterChanged(int index, string _)
    {
        SetStateRebuild(() =>
            {
                _activeFilter = index;
                var exts = NormalizedExts(_filters[index]);
                _model.ExtensionFilter = exts;
                _model.ApplyView();
                RetargetSaveExtension(exts);
            }
        );
    }

    /// <summary>Switching the save format swaps the name's extension, like native format pickers.</summary>
    private void RetargetSaveExtension(string[]? exts)
    {
        if (Options.Kind != FileDialogKind.SaveFile || exts is not { Length: > 0 }) return;
        var name = _nameField.Text.Trim();
        if (name.Length == 0) return;
        var current = Path.GetExtension(name).TrimStart('.');
        if (exts.Any(e => string.Equals(e, current, StringComparison.OrdinalIgnoreCase))) return;
        _nameField.Text = Path.GetFileNameWithoutExtension(name) + "." + exts[0];
    }

    private void OnActivate(FileBrowserEntry entry)
    {
        if (entry.IsDirectory)
        {
            NavigateTo(entry.FullPath);
            return;
        }

        if (Options.Kind == FileDialogKind.SaveFile)
        {
            SetStateRebuild(() => _nameField.Text = entry.Name);
            return;
        }

        Confirm();
    }

    private void Confirm()
    {
        switch (Options.Kind)
        {
            case FileDialogKind.OpenFile:
            {
                var selected = _model.SelectedEntries();
                if (selected.Count == 1 && selected[0].IsDirectory)
                {
                    NavigateTo(selected[0].FullPath); // "Open" on a folder enters it
                    return;
                }

                var files = selected.Where(e => !e.IsDirectory).Select(e => e.FullPath).ToArray();
                if (files.Length > 0) Complete(files);
                break;
            }
            case FileDialogKind.PickFolder:
            {
                var dir = _model.CurrentDirectory;
                foreach (var e in _model.SelectedEntries())
                    if (e.IsDirectory)
                    {
                        dir = e.FullPath;
                        break;
                    }

                Complete([dir]);
                break;
            }
            case FileDialogKind.SaveFile:
                ConfirmSave();
                break;
        }
    }

    private void ConfirmSave()
    {
        var name = _nameField.Text.Trim();
        if (name.Length == 0) return;

        var full = Path.IsPathRooted(name) ? name : Path.Combine(_model.CurrentDirectory, name);
        if (Directory.Exists(full))
        {
            NavigateTo(full); // they typed a folder — enter it instead of overwriting it
            return;
        }

        // Enforce the active format's extension, the way native save panels do.
        if (_filters.Length > 0 && NormalizedExts(_filters[_activeFilter]) is { Length: > 0 } exts)
        {
            var ext = Path.GetExtension(full).TrimStart('.');
            if (!exts.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                full += "." + exts[0];
        }

        if (File.Exists(full))
        {
            Dialog.Confirm(
                "Replace existing file?",
                $"\"{Path.GetFileName(full)}\" already exists in this location. " +
                "Replacing it overwrites its contents.",
                () => Complete([full]),
                confirmLabel: "Replace"
            );
            return;
        }

        Complete([full]);
    }

    private void PromptNewFolder()
    {
        var app = App.Active;
        if (app is null) return;
        var field = new TextField(decoration: new InputDecoration("Folder name")) {
            Text = "untitled folder",
        };
        Dialog? prompt = null;
        field.OnSubmitted = _ => Create();

        var body = new SizedBox(
            340f,
            child: new Padding(
                EdgeInsets.All(18f),
                new Column {
                    CrossAxisAlignment = CrossAxisAlignment.Stretch,
                    MainAxisSize = MainAxisSize.Min,
                    Children = {
                        new Label("New Folder") { Style = Label.LabelStyle.Title },
                        new SizedBox(height: 10f),
                        field,
                        new SizedBox(height: 14f),
                        new Row {
                            MainAxisAlignment = MainAxisAlignment.End,
                            Children = {
                                new Button("Cancel", () => prompt?.Dismiss()) {
                                    Style = ButtonStyle.Outlined,
                                },
                                new SizedBox(8f),
                                new Button("Create", Create),
                            },
                        },
                    },
                }
            )
        );
        prompt = new Dialog(body, app) { Dismissible = true };
        prompt.Show();
        return;

        void Create()
        {
            var name = field.Text.Trim();
            if (name.Length == 0) return;
            var path = Path.Combine(_model.CurrentDirectory, name);
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                app.ShowSnackbar($"Could not create folder: {ex.Message}");
                prompt?.Dismiss();
                return;
            }

            prompt?.Dismiss();
            SetStateRebuild(() =>
                {
                    _model.Refresh();
                    for (var i = 0; i < _model.Visible.Count; i++)
                        if (string.Equals(
                                _model.Visible[i].FullPath,
                                path,
                                StringComparison.OrdinalIgnoreCase
                            ))
                        {
                            _model.SelectIndex(i);
                            break;
                        }
                }
            );
        }
    }

    private void Cancel()
    {
        Complete([]);
    }

    private void Complete(string[] paths)
    {
        Widget.CompleteAndClose(paths);
    }
}

/// <summary>
///     Registers the file browser as <see cref="FileDialog.ManagedBackend" /> the moment this
///     assembly loads — any app that references Zigote.UI.Material gets the in-app fallback with
///     zero wiring (the same philosophy as DevTools auto-install).
/// </summary>
internal static class FileBrowserFallbackInstaller
{
    // CA2255 warns against library module initializers in general; this one is the point — the
    // fallback must exist the moment the assembly is present, with zero app wiring (the same
    // auto-install philosophy as DevTools).
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install()
    {
        FileDialog.ManagedBackend ??= request =>
        {
            var app = App.Active ??
                      throw new FileDialogException(
                          "No active app to host the in-app file dialog."
                      );
            return FileBrowserDialog.ShowAsync(app, FileBrowserOptions.From(request));
        };
    }
}
