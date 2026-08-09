namespace AdwaitaGallery;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest();
        new GalleryApp().Run();
        return 0;
    }

    /// <summary>
    ///     Headless smoke check: the catalogue is the index space the sidebar selects in, so a
    ///     mismatch there points the shell at the wrong page. Also constructs every page, which is
    ///     what catches a registry entry wired to a type that throws in its constructor.
    /// </summary>
    private static int SelfTest()
    {
        var failures = new List<string>();

        var flattened = GalleryRegistry.Sections.SelectMany(s => s.Entries).ToArray();
        if (flattened.Length != GalleryRegistry.Entries.Length)
            failures.Add("Entries is not the flattening of Sections");

        var sidebarTitles = GalleryRegistry.SidebarSections()
            .SelectMany(s => s.Items)
            .Select(i => i.Title)
            .ToArray();
        if (!sidebarTitles.SequenceEqual(GalleryRegistry.Entries.Select(e => e.Title)))
            failures.Add("Sidebar row order does not match the Entries index space");

        var seen = new HashSet<string>();
        for (var i = 0; i < GalleryRegistry.Entries.Length; i++)
        {
            var entry = GalleryRegistry.Entries[i];
            if (!seen.Add(entry.Title)) failures.Add($"duplicate page title: {entry.Title}");
            if (entry.Subtitle.Length == 0) failures.Add($"{entry.Title}: no subtitle");
            if (entry.IconName.Length == 0) failures.Add($"{entry.Title}: no icon");
            if (GalleryRegistry.IndexOf(entry.Title) != i)
                failures.Add($"{entry.Title}: IndexOf does not round-trip");

            try
            {
                if (entry.Build() is null) failures.Add($"{entry.Title}: builder returned null");
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Title}: builder threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The one page whose point is not visual: concurrent signal writes have to be exact.
        if (Pages.ConcurrencyPage.SelfCheck() is { } concurrency)
            failures.Add($"Concurrency: {concurrency}");

        foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
        Console.WriteLine(
            failures.Count == 0
                ? $"Adwaita gallery self-test: {GalleryRegistry.Entries.Length} pages, all checks passed"
                : $"Adwaita gallery self-test: {failures.Count} failure(s)"
        );
        return failures.Count == 0 ? 0 : 1;
    }
}

/// <summary>
///     The gallery application. Appearance is signal-backed — follow-the-system, light/dark and the
///     nine GNOME accents — and every open window re-themes from it live, which is the whole point
///     of the Adwaita palette living in one <see cref="ThemeData" />. Windows are peers: each hosts
///     its own <see cref="Shell" /> with its own navigation, search and toasts.
/// </summary>
internal sealed class GalleryApp : AdwaitaApp
{
    // Shortcut action ids — bound to chords in InstallShortcuts, dispatched through App.OnShortcut.
    public const string ActionSearch = "gallery.search";
    public const string ActionNewWindow = "gallery.window.new";
    public const string ActionPreferences = "gallery.preferences";
    public const string ActionAbout = "gallery.about";
    public const string ActionToggleDark = "gallery.style.dark";
    public const string ActionCloseWindow = "gallery.window.close";

    private readonly Shell _shell;
    private bool _applying;

    public GalleryApp() : base(title: "Adwaita Demo", theme: AdwTheme.Light)
    {
        Width = 1100;
        Height = 760;

        _shell = new Shell(this);
        Home = new SafeArea(_shell);

        FollowSystem.Changed += _ => Apply();
        Dark.Changed += _ => Apply();
        Accent.Changed += _ => Apply();
        // Fired once at startup after the initial system values are read, then on every GNOME
        // appearance/accent change.
        SystemStyleChanged += Apply;
    }

    /// <summary>Track the GNOME appearance and accent instead of the manual choice below.</summary>
    public Signal<bool> FollowSystem { get; } = new(true);

    /// <summary>The live appearance — mirrors the system while <see cref="FollowSystem" /> is on.</summary>
    public Signal<bool> Dark { get; } = new(false);

    /// <summary>The live accent — likewise.</summary>
    public Signal<AdwAccent> Accent { get; } = new(AdwAccent.Blue);

    protected override void OnInit()
    {
        base.OnInit();
        if (App is { } app) InstallShortcuts(app, _shell);
    }

    /// <summary>Open another gallery window — an independent Shell on its own OS window.</summary>
    public void NewWindow()
    {
        var shell = new Shell(this);
        if (OpenWindow(
                new SafeArea(shell),
                "Adwaita Demo",
                1100,
                760
            ) is { } win)
            InstallShortcuts(win, shell);
    }

    /// <summary>
    ///     Bind the gallery's chords on a window and route them to that window's shell — a shortcut
    ///     acts on the window it was pressed in, which is what makes several windows behave like a
    ///     real app rather than like one app with copies.
    /// </summary>
    private void InstallShortcuts(App window, Shell shell)
    {
        window.Keymap.Bind(ActionSearch, KeyChord.Command(KeyCode.F));
        window.Keymap.Bind(ActionNewWindow, KeyChord.Command(KeyCode.N));
        window.Keymap.Bind(ActionPreferences, KeyChord.Command(KeyCode.Comma));
        window.Keymap.Bind(ActionAbout, KeyChord.Command(KeyCode.Slash, true));
        window.Keymap.Bind(ActionToggleDark, KeyChord.Command(KeyCode.D));
        window.Keymap.Bind(ActionCloseWindow, KeyChord.Command(KeyCode.W));

        window.OnShortcut = action =>
        {
            switch (action)
            {
                case ActionSearch:
                    shell.FocusSearch();
                    return true;
                case ActionNewWindow:
                    NewWindow();
                    return true;
                case ActionPreferences:
                    shell.ShowPreferences();
                    return true;
                case ActionAbout:
                    GalleryAbout.Show();
                    return true;
                case ActionToggleDark:
                    FollowSystem.Value = false;
                    Dark.Value = !Dark.Value;
                    return true;
                case ActionCloseWindow:
                    window.RequestClose();
                    return true;
                default:
                    return false;
            }
        };
    }

    /// <summary>
    ///     Rebuild the theme from the current appearance state. While following the system, the
    ///     signals are pushed back to the system values so the preferences UI shows what is actually
    ///     in force (the re-entrant Apply that causes is what <c>_applying</c> swallows).
    /// </summary>
    private void Apply()
    {
        if (_applying) return;
        _applying = true;
        try
        {
            if (FollowSystem.Value)
            {
                Dark.Value = SystemPrefersDark;
                Accent.Value = SystemAccent;
            }

            Theme = AdwTheme.Create(Accent.Value, Dark.Value);
        }
        finally
        {
            _applying = false;
        }
    }
}