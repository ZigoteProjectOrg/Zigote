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
            "basics",
            MaterialIcons.Widgets,
            Colors.Blue,
            l => l.DemoBasicsTitle,
            l => l.DemoBasicsDesc,
            () => new BasicsPage()
        ),
        new(
            "selection",
            MaterialIcons.ToggleOn,
            Colors.Green,
            l => l.DemoSelectionTitle,
            l => l.DemoSelectionDesc,
            () => new SelectionPage()
        ),
        new(
            "inputs",
            MaterialIcons.Keyboard,
            Colors.Indigo,
            l => l.DemoInputsTitle,
            l => l.DemoInputsDesc,
            () => new InputsPage()
        ),
        new(
            "display",
            MaterialIcons.Style,
            Colors.Teal,
            l => l.DemoDisplayTitle,
            l => l.DemoDisplayDesc,
            () => new DisplayPage()
        ),
        new(
            "progress",
            MaterialIcons.Downloading,
            Colors.Cyan,
            l => l.DemoProgressTitle,
            l => l.DemoProgressDesc,
            () => new ProgressPage()
        ),
        new(
            "layout",
            MaterialIcons.Dashboard,
            Colors.Orange,
            l => l.DemoLayoutTitle,
            l => l.DemoLayoutDesc,
            () => new LayoutPage()
        ),
        new(
            "overlays",
            MaterialIcons.Layers,
            Colors.Purple,
            l => l.DemoOverlaysTitle,
            l => l.DemoOverlaysDesc,
            () => new OverlaysPage()
        ),
        new(
            "editors",
            MaterialIcons.Palette,
            Colors.Pink,
            l => l.DemoEditorsTitle,
            l => l.DemoEditorsDesc,
            () => new EditorsPage()
        ),
        new(
            "data",
            MaterialIcons.AccountTree,
            Colors.Brown,
            l => l.DemoDataTitle,
            l => l.DemoDataDesc,
            () => new DataPage()
        ),
        new(
            "charts",
            MaterialIcons.ShowChart,
            Colors.Red,
            l => l.DemoChartsTitle,
            l => l.DemoChartsDesc,
            () => new ChartsPage()
        ),
        new(
            "animate",
            MaterialIcons.AutoAwesome,
            Colors.Amber,
            l => l.DemoAnimateTitle,
            l => l.DemoAnimateDesc,
            () => new AnimatePage()
        ),
        new(
            "drag-drop",
            MaterialIcons.PanTool,
            Colors.BlueGrey,
            l => l.DemoDragDropTitle,
            l => l.DemoDragDropDesc,
            () => new DragDropPage()
        ),
        new(
            "video",
            MaterialIcons.Movie,
            Colors.DeepOrange,
            l => l.DemoVideoTitle,
            l => l.DemoVideoDesc,
            () => new VideoPage()
        ),
        new(
            "localization",
            MaterialIcons.Translate,
            Colors.DeepPurple,
            l => l.DemoLocalizationTitle,
            l => l.DemoLocalizationDesc,
            () => new LocalizationPage()
        ),
    ];

    public static DemoInfo? Find(string id)
    {
        foreach (var demo in All)
            if (demo.Id == id)
                return demo;
        return null;
    }
}