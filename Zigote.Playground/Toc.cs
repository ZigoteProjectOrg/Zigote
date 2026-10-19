namespace Playground;

internal sealed record DocEntry(string Title, string Icon, string File);

internal sealed record DocSection(string Title, DocEntry[] Entries);

/// <summary>The table of contents. A page is one entry here and one Markdown file in Docs/.</summary>
internal static class Toc
{
    public static readonly DocSection[] Sections = [
        new("Getting Started", [
            new("Introduction", MaterialIcons.MenuBook, "introduction.md"),
            new("Your First App", MaterialIcons.RocketLaunch, "first-app.md"),
            new("Using the Playground", MaterialIcons.School, "playground.md"),
        ]),
        new("Building UIs", [
            new("Widgets", MaterialIcons.Widgets, "widgets.md"),
            new("Layout", MaterialIcons.ViewQuilt, "layout.md"),
            new("Text", MaterialIcons.TextFields, "text.md"),
            new("Input & Gestures", MaterialIcons.TouchApp, "input.md"),
            new("Text Input & Forms", MaterialIcons.EditNote, "forms.md"),
            new("Keyboard & Focus", MaterialIcons.Keyboard, "keyboard.md"),
            new("Theming", MaterialIcons.Palette, "theming.md"),
            new("Internationalization", MaterialIcons.Translate, "i18n.md"),
        ]),
        new("State", [
            new("Signals & Watch", MaterialIcons.Bolt, "signals.md"),
            new("Reactivity in Depth", MaterialIcons.AccountTree, "reactivity.md"),
            new("Widget Lifecycle", MaterialIcons.Sync, "lifecycle.md"),
            new("Async & Background Work", MaterialIcons.Speed, "async.md"),
            new("Scheduling", MaterialIcons.Schedule, "scheduling.md"),
            new("Parallelism", MaterialIcons.CallSplit, "parallelism.md"),
        ]),
        new("Adwaita", [
            new("Controls", MaterialIcons.Dashboard, "adwaita.md"),
            new("Lists & Scrolling", MaterialIcons.Layers, "lists.md"),
        ]),
        new("Motion", [
            new("Implicit Animations", MaterialIcons.AutoFixHigh, "animation-implicit.md"),
            new("Explicit Animations", MaterialIcons.Animation, "animation-explicit.md"),
            new("Transitions", MaterialIcons.SwapHoriz, "transitions.md"),
        ]),
        new("Under the Hood", [
            new("The Frame", MaterialIcons.Memory, "frame.md"),
            new("Custom Widgets & Painting", MaterialIcons.Brush, "custom-widgets.md"),
            new("Custom Shaders", MaterialIcons.Gradient, "shaders.md"),
            new("The Renderer", MaterialIcons.AutoAwesome, "renderer.md"),
        ]),
        new("Plugins", [
            new("Platform Channels", MaterialIcons.Hub, "channels.md"),
            new("Writing Plugins", MaterialIcons.Extension, "plugins.md"),
        ]),
        new("Native & Engine", [
            new("Native Interop", MaterialIcons.Cable, "native.md"),
            new("Games & 3D", MaterialIcons.ViewInAr, "engine.md"),
            new("Tooling", MaterialIcons.Terminal, "tooling.md"),
        ]),
        new("Guides", [
            new("Best Practices", MaterialIcons.TipsAndUpdates, "best-practices.md"),
        ]),
    ];

    public static readonly DocEntry[] Entries = [.. Sections.SelectMany(s => s.Entries)];

    public static string Read(string file)
    {
        using var stream = typeof(Toc).Assembly.GetManifestResourceStream($"Docs/{file}") ??
                           throw new FileNotFoundException($"Docs/{file} is not embedded");
        return new StreamReader(stream).ReadToEnd();
    }

    public static AdwSidebarSection[] SidebarSections() => [
        .. Sections.Select(s => new AdwSidebarSection(
            title: s.Title,
            items: [.. s.Entries.Select(e => new AdwSidebarItem(title: e.Title, iconName: e.Icon))]
        )),
    ];
}
