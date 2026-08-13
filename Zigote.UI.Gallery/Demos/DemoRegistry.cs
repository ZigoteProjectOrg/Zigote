using Zigote.Core;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Gallery;

/// <summary>
///     One entry in the gallery: identity, home-card presentation, and the page factory. Title and
///     description are compile-safe selectors over the generated <see cref="GalleryL10n" /> — a
///     renamed or deleted ARB key fails the build here instead of falling back to a raw key at
///     runtime.
/// </summary>
internal sealed record DemoInfo(
    string Id,
    string Icon,
    Color Accent,
    Func<GalleryL10n, string> Title,
    Func<GalleryL10n, string> Description,
    Func<Widget> BuildPage);

/// <summary>The declarative catalog every screen is generated from (home cards + routed pages).</summary>
internal static class DemoRegistry
{
    public static readonly IReadOnlyList<DemoInfo> All = [
        new(
            Id: "basics",
            Icon: MaterialIcons.Widgets,
            Accent: Colors.Blue,
            Title: l => l.DemoBasicsTitle,
            Description: l => l.DemoBasicsDesc,
            BuildPage: () => new BasicsPage()
        ),
        new(
            Id: "selection",
            Icon: MaterialIcons.ToggleOn,
            Accent: Colors.Green,
            Title: l => l.DemoSelectionTitle,
            Description: l => l.DemoSelectionDesc,
            BuildPage: () => new SelectionPage()
        ),
        new(
            Id: "inputs",
            Icon: MaterialIcons.Keyboard,
            Accent: Colors.Indigo,
            Title: l => l.DemoInputsTitle,
            Description: l => l.DemoInputsDesc,
            BuildPage: () => new InputsPage()
        ),
        new(
            Id: "display",
            Icon: MaterialIcons.Style,
            Accent: Colors.Teal,
            Title: l => l.DemoDisplayTitle,
            Description: l => l.DemoDisplayDesc,
            BuildPage: () => new DisplayPage()
        ),
        new(
            Id: "progress",
            Icon: MaterialIcons.Downloading,
            Accent: Colors.Cyan,
            Title: l => l.DemoProgressTitle,
            Description: l => l.DemoProgressDesc,
            BuildPage: () => new ProgressPage()
        ),
        new(
            Id: "layout",
            Icon: MaterialIcons.Dashboard,
            Accent: Colors.Orange,
            Title: l => l.DemoLayoutTitle,
            Description: l => l.DemoLayoutDesc,
            BuildPage: () => new LayoutPage()
        ),
        new(
            Id: "overlays",
            Icon: MaterialIcons.Layers,
            Accent: Colors.Purple,
            Title: l => l.DemoOverlaysTitle,
            Description: l => l.DemoOverlaysDesc,
            BuildPage: () => new OverlaysPage()
        ),
        new(
            Id: "editors",
            Icon: MaterialIcons.Palette,
            Accent: Colors.Pink,
            Title: l => l.DemoEditorsTitle,
            Description: l => l.DemoEditorsDesc,
            BuildPage: () => new EditorsPage()
        ),
        new(
            Id: "data",
            Icon: MaterialIcons.AccountTree,
            Accent: Colors.Brown,
            Title: l => l.DemoDataTitle,
            Description: l => l.DemoDataDesc,
            BuildPage: () => new DataPage()
        ),
        new(
            Id: "charts",
            Icon: MaterialIcons.ShowChart,
            Accent: Colors.Red,
            Title: l => l.DemoChartsTitle,
            Description: l => l.DemoChartsDesc,
            BuildPage: () => new ChartsPage()
        ),
        new(
            Id: "animate",
            Icon: MaterialIcons.AutoAwesome,
            Accent: Colors.Amber,
            Title: l => l.DemoAnimateTitle,
            Description: l => l.DemoAnimateDesc,
            BuildPage: () => new AnimatePage()
        ),
        new(
            Id: "drag-drop",
            Icon: MaterialIcons.PanTool,
            Accent: Colors.BlueGrey,
            Title: l => l.DemoDragDropTitle,
            Description: l => l.DemoDragDropDesc,
            BuildPage: () => new DragDropPage()
        ),
        new(
            Id: "video",
            Icon: MaterialIcons.Movie,
            Accent: Colors.DeepOrange,
            Title: l => l.DemoVideoTitle,
            Description: l => l.DemoVideoDesc,
            BuildPage: () => new VideoPage()
        ),
        new(
            Id: "localization",
            Icon: MaterialIcons.Translate,
            Accent: Colors.DeepPurple,
            Title: l => l.DemoLocalizationTitle,
            Description: l => l.DemoLocalizationDesc,
            BuildPage: () => new LocalizationPage()
        ),
    ];

    public static DemoInfo? Find(string id)
    {
        foreach (var demo in All)
        {
            if (demo.Id == id)
                return demo;
        }

        return null;
    }
}
