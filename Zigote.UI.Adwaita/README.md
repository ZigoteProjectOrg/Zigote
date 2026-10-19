# Zigote.UI.Adwaita

The **GNOME Adwaita** design system, implemented on Zigote's widget kernel: 148 `Adw*` types, both appearances, the nine
system accents, the boxed-list row vocabulary, adaptive navigation, and client-side decorations the app draws itself.
Tokens and metrics track **libadwaita 1.10 (GNOME 51)** — every colour, radius and state fill here has a line of
`src/stylesheet/` behind it.

```csharp
new AdwaitaApp(new Shell(), title: "My App").Run();
```

<img src="../docs/images/adwaita-welcome.png" alt="The Adwaita gallery" width="820">

> This is a **design system, not a GTK binding.** Nothing links against GTK, GLib or libadwaita — the
> widgets are Zigote widgets, laid out and painted by Zigote's own renderer. An Adwaita app therefore
> builds and runs unchanged on macOS and Windows; the screenshot above is macOS.

**See it all:** from the repo root, `dotnet run --project Zigote.UI.Adwaita.Gallery` — 51 pages, one per widget family
([gallery README](../Zigote.UI.Adwaita.Gallery/README.md)).

---

## Contents

- [The application shell](#the-application-shell)
- [Theming](#theming)
- [Chrome: header bars and window controls](#chrome-header-bars-and-window-controls)
- [Boxed lists and preferences](#boxed-lists-and-preferences)
- [Dates, times and data tables](#dates-times-and-data-tables)
- [Navigation](#navigation)
- [Adaptive layout](#adaptive-layout)
- [Feedback: toasts, banners, dialogs](#feedback-toasts-banners-dialogs)
- [A complete app](#a-complete-app)
- [Style, type and metrics](#style-type-and-metrics)
- [Motion](#motion)
- [Liquid Glass](#liquid-glass)
- [Beyond libadwaita: tool widgets](#beyond-libadwaita-tool-widgets)
- [Differences from libadwaita](#differences-from-libadwaita)

---

## The application shell

`AdwaitaApp` is a `ZigoteApp` preconfigured for the Adwaita look. It resolves window chrome from the desktop it finds
itself on, sets the corner radius, registers the header bar as the drag band, and — unless you hand it an explicit
theme — follows the system appearance and accent for as long as it runs.

```csharp
public AdwaitaApp(
    Widget? home = null,
    string title = "Zigote App",
    ThemeData? theme = null,           // null → follow the system (light off GNOME)
    Dictionary<string, WidgetBuilder>? routes = null,
    string initialRoute = "/",
    RouteFactory? onGenerateRoute = null,
    List<Page>? pages = null,          // Navigator 2.0 declarative pages
    Func<Route, object?, bool>? onPopPage = null,
    bool followSystem = true)
```

| Member                             | What it gives you                                                                                                          |
|------------------------------------|----------------------------------------------------------------------------------------------------------------------------|
| `SystemAccent`                     | The desktop accent hue (GNOME 47+); `Blue` where unavailable.                                                              |
| `SystemPrefersDark`                | The desktop's dark-appearance preference.                                                                                  |
| `SystemStyleChanged`               | Fires once at startup and on every appearance/accent change, after `Theme` has been rebuilt. Rebuild retained chrome here. |
| `OpenWindow(content, title, w, h)` | Another OS window: same chrome, its own widget tree, re-themed live with the app.                                          |

Windows are peers — each can host its own navigation, search, shortcuts and toasts. The gallery opens as many as you
like and every one of them repaints when you change the accent.

## Theming

An Adwaita appearance is a whole `ThemeData`, built by `AdwTheme`:

```csharp
AdwTheme.Light                                  // the GNOME default
AdwTheme.Dark
AdwTheme.Create(AdwAccent.Purple, dark: true)   // cache the result — theme switching is by reference
```

`AdwAccent` is the GNOME set: `Blue`, `Teal`, `Green`, `Yellow`, `Orange`, `Red`, `Pink`, `Purple`,
`Slate`. Because an accent produces a complete theme rather than a late tint, switching one repaints every open window
in a frame. The *standalone* form of each hue — links, alert responses, menu check marks — is derived exactly as the
stylesheet derives it, `oklab(from @accent_bg_color min(l, 0.5) a
b)` in light and `max(l, 0.85)` in dark, so all nine (and the destructive/success/warning trio) land on libadwaita's
published values.

Tokens with no `ThemeData` slot live in `AdwPalette` — headerbar, sidebar, popover and thumbnail surfaces, dim labels,
the scrim, the status colours, and the **state fills**.
`AdwPalette.For(theme)` picks the light or dark set from whatever theme is in scope.

Adwaita 1.10 states every neutral fill as `color-mix(in srgb, currentColor N%, transparent)`, so one ladder serves both
appearances — only currentColor flips. `AdwPalette` resolves those percentages once per appearance, and `AdwStyle` picks
the right rung:

| Ladder                        | Rest / hover / active                 | Where                                                                            |
|-------------------------------|---------------------------------------|----------------------------------------------------------------------------------|
| `ButtonFill*`                 | 10 / 15 / 30 % (checked 30 / 35 / 40) | raised buttons, entries, spin buttons, window controls, toggle-group backgrounds |
| `HoverFill` … `SelectedFill*` | 0 / 7 / 16 %, selected 10 / 13 / 19   | flat buttons, menu items, sidebar rows, tabs                                     |
| `ViewHoverFill`               | 0 / 4 / 8 %                           | rows inside a list view                                                          |
| `CardHoverFill`               | 0 / 3 / 8 %                           | boxed-list rows, `.card.activatable`                                             |
| `TroughFill*`                 | 15 / 20 / 25 %                        | switch, scale, check and progress troughs                                        |

`AdwStyle.ButtonFill`, `RowFill`, `MenuRowFill`, `SidebarRowFill`, `ViewRowFill` and `TroughFill` are the entry points;
use them instead of reaching for a fill directly, so a control lands on the ladder its stylesheet counterpart uses.

**Following the desktop.** On GNOME, `GnomeDesktop` reads `color-scheme`, `accent-color` and
`button-layout`, and keeps them fresh with `gsettings monitor` — or, inside a Flatpak or Snap sandbox where the host's
dconf is unreachable, through the `org.freedesktop.portal.Settings` D-Bus interface, matching the portal's sRGB accent
triple back to the nearest named hue. Off GNOME it is a safe no-op with stock defaults.

## Chrome: header bars and window controls

An Adwaita header bar *is* the titlebar. `AdwaitaApp` therefore asks for client-side decorations wherever a desktop can
host them:

| Host                    | Result                                                                                                                                                                                     |
|-------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **GNOME**               | CSD. `AdwWindowControls` draws the libadwaita ✕/─/□ circles inside your header bars, honouring the system `button-layout` — which buttons exist, their order, and which side they sit on. |
| **macOS**               | CSD, with the traffic lights drawn in the bar and vertically centred as macOS centres them.                                                                                                |
| **Windows, KDE, other** | System decorations; Adwaita content inside them.                                                                                                                                           |

`WindowChrome.Preference` overrides the lot when you need to force a look for testing.

Under CSD the window frame follows the stylesheet down to the pixel: a 15px corner (`--window-radius`, squared while
maximized), a 1px `rgb(255 255 255 / 7%)` outline just inside the edge (`App.CsdOutlineColor`, without which a dark
window bleeds into a dark desktop), a **46px**
titlebar rather than the 47px of an inline header bar — the missing pixel is the outline's —
`6px 7px 7px 7px` of bar padding, and frame buttons whose 24px circles sit in 34px hit targets 3px apart, exactly as
`windowcontrols > button { min-width: 24px; padding: 5px }` lays them out. The drag band matches the bar's own height,
so the first row of content takes clicks instead of moving the window.

```csharp
new AdwToolbarView(content) {
    TopBars = {
        new AdwHeaderBar {
            TitleWidget = new AdwWindowTitle("Inbox", "3 unread"),
            Start = { new AdwButton { IconName = Icons.MoreVert, Style = AdwButtonStyle.Flat } },
            End   = { new AdwMenuButton() },
        },
    },
    BottomBars = { new AdwViewSwitcherBar(stack) },
}
```

`AdwToolbarView` is the standard container: bars above, bars below, content between, with the raised/flat handling
libadwaita does. `AdwWindowControls` renders nothing when the window has no CSD or when the layout puts no buttons on
its side, so it is always safe to mount.

## Boxed lists and preferences

The row vocabulary every GNOME app is built from:

```csharp
new AdwPreferencesGroup("Account", "Signed in as you@example.com") {
    Rows = {
        new AdwActionRow("Profile", "Name, avatar and bio") { ShowChevron = true,
                                                              OnActivated = OpenProfile },
        new AdwSwitchRow("Sync", "Keep this device up to date", value: true, on => Sync(on)),
        new AdwComboRow("Quality", options, selectedIndex: 1),
        new AdwSpinRow("Cache", min: 1, max: 64),
        new AdwEntryRow("Display name"),
        new AdwPasswordEntryRow("Password"),
        new AdwExpanderRow("Advanced") { Rows = { … } },
        new AdwButtonRow("Sign Out") { Destructive = true },
    },
}
```

Groups stack into an `AdwPreferencesPage`, and pages into an `AdwPreferencesDialog` — the adaptive settings window with
its own view switcher, exactly as GNOME presents it.

Every row is activatable, focusable and semantics-described; a disabled row dims as a whole and stops activating, as
Adwaita does.

## Dates, times and data tables

```csharp
new AdwCalendar(value: today, onChanged: d => …) { MinDate = today, IsMarked = HasEvent };
new AdwTimePicker(new TimeOnly(9, 30)) { MinuteStep = 5 };         // 24h or AM/PM from the locale
new AdwDateButton(onChanged: d => …);                               // the value in a button, a calendar on click
new AdwDateRow("Starts"); new AdwTimeRow("At") { MinuteStep = 15 }; // the same, as boxed-list rows

new AdwColumnView<File>(files,
    new AdwColumn<File>("Name", f => f.Name) { Expand = true, Icon = f => f.Glyph }.SortBy(f => f.Name),
    new AdwColumn<File>("Size", f => f.SizeText) { Width = 90, Align = AdwColumnAlign.End }.SortBy(f => f.Bytes)
) { SelectionMode = AdwSelectionMode.Multiple, OnActivated = Open };
```

| Widget                            | What it is                                                                                                                                                                                                                                                                                                                   |
|-----------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `AdwCalendar`                     | GtkCalendar: `‹ Month ›  ‹ Year ›` steppers over a six-week grid, weekday order and names from the culture, today in accent, an accent disc for the selection, marked days, `MinDate`/`MaxDate`. Arrows, Page Up/Down (Shift: a year), Home/End; months slide on `AdwMotion.Reveal`.                                         |
| `AdwTimePicker`                   | GNOME Clocks' vertical spinners — hours, minutes, AM/PM when the locale is 12-hour. Both wrap without carrying; digits type straight in and move on; the wheel spins it once it has focus.                                                                                                                                   |
| `AdwDateButton` / `AdwTimeButton` | The compact form (SwiftUI's compact `DatePicker`): the value in a button, the picker in a popover.                                                                                                                                                                                                                           |
| `AdwDateRow` / `AdwTimeRow`       | The same as boxed-list rows, laid out like `AdwComboRow`.                                                                                                                                                                                                                                                                    |
| `AdwColumnView<T>`                | GtkColumnView: a sticky header over painted, clip-virtualized rows — a hundred thousand rows cost what is on screen. Click a title to sort (stable; on a worker thread past 20,000 rows), drag its edge to resize, ellipsized cells, single or multiple selection, keyboard navigation, `.data-table` density via `Compact`. |
| `AdwPopover`                      | GtkPopover with any child: the popover card, arrow and motion every Adwaita popover uses; the child takes focus.                                                                                                                                                                                                             |

Inside a scrolling page neither picker steals the wheel: the calendar pages months on a sideways swipe, and on the wheel
only where nothing scrolls (a popover).

## Navigation

| Widget                                        | Shape                                                                                                                          |
|-----------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------|
| `AdwNavigationView`                           | A page stack. `Push`/`Pop`, `Depth`, and (with `AutoHeaderBar`) a header that follows the stack and grows its own back button. |
| `AdwNavigationSplitView`                      | Sidebar + content, folding into a single navigable page below a breakpoint.                                                    |
| `AdwOverlaySplitView`                         | Sidebar that becomes an overlay instead of a column.                                                                           |
| `AdwViewStack` + `AdwViewSwitcher`            | One stack, three presentations: header switcher, `AdwViewSwitcherBar` at the bottom, `AdwViewSwitcherSidebar` at the side.     |
| `AdwTabView` / `AdwTabBar` / `AdwTabOverview` | Pinned and closable tabs with a tab menu and a grid overview.                                                                  |
| `AdwCarousel`                                 | Swipeable pages with dot or line indicators.                                                                                   |
| `AdwSidebar`                                  | The searchable, sectioned sidebar the gallery uses; `Filter` re-filters rows in place.                                         |
| `AdwPaned`                                    | Two panes and a draggable handle.                                                                                              |

## Adaptive layout

```csharp
new AdwNavigationSplitView { Sidebar = list, Content = detail, AutoCollapseBelow = 620f }
```

For anything more conditional than a fold, `AdwBreakpointBin` evaluates conditions against its own size and swaps child,
or runs `Apply`/`Unapply` callbacks:

```csharp
new AdwBreakpointBin(wideLayout) {
    Breakpoints = {
        new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(500)) { Child = narrowLayout },
    },
}
```

Conditions compose: `MinWidth`, `MaxWidth`, `MinHeight`, `MaxHeight`, `MinAspectRatio`,
`MaxAspectRatio`, combined with `.And(…)` / `.Or(…)`. Breakpoints are declared narrowest-first and the last match wins.

`AdwMultiLayoutView` goes further — name slots once, and let each layout place them differently.
`AdwClamp` keeps content at a readable width in a wide window; `AdwWrapBox` flows children onto new lines.

## Feedback: toasts, banners, dialogs

```csharp
var toasts = new AdwToastOverlay(content);          // mount once, near the root
toasts.AddToast(new AdwToast("File deleted") { ButtonLabel = "Undo",
                                               OnButtonClicked = Undo,
                                               Timeout = 5f });
```

- `AdwBanner` — the inline bar for something that needs an answer.
- `AdwAlertDialog` — adaptive alerts with suggested and destructive responses.
- `AdwDialog` / `AdwDialog.Show(child)` — the generic adaptive dialog, dismissable, size-constrained.
- `AdwAboutDialog`, `AdwShortcutsDialog` (with `AdwShortcutLabel` key caps).
- `AdwStatusPage` — empty, error and welcome states; `Compact` for embedding in a pane.
- `AdwBottomSheet` — a sheet dragged up over the content.
- `AdwSpinner` — the indeterminate Adwaita spinner.

<img src="../docs/images/adwaita-bottom-sheet.png" alt="A draggable bottom sheet over the page content" width="820">

## A complete app

This compiles as written — an app, a header bar, a boxed list, and one signal on screen:

```csharp
using Zigote.Core.State;
using Zigote.UI.Adwaita;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

new AdwaitaApp(new CounterPage(), title: "Counter").Run();

sealed class CounterPage : ComposedWidget
{
    private readonly Signal<int> _count = new(0);
    private readonly Signal<bool> _big = new(false);

    protected override Widget Build(BuildContext context) =>
        new AdwToolbarView(
            new AdwClamp(
                new AdwPreferencesGroup("Counter", "One signal, two rows and a header") {
                    Rows = {
                        new AdwActionRow("Count", "Activate the row to increment") {
                            OnActivated = () => _count.Value += _big.Value ? 10 : 1,
                            Suffixes    = { new Watch(() => new Label($"{_count.Value}")) },
                            ShowChevron = true,
                        },
                        new AdwSwitchRow("Big steps", "Add ten at a time", false,
                                         on => _big.Value = on),
                    },
                })) {
            TopBars = {
                new AdwHeaderBar {
                    TitleWidget = new Watch(() =>
                        new AdwWindowTitle("Counter", $"{_count.Value} so far")),
                },
            },
        };
}
```

## Style, type and metrics

| Type             | Holds                                                                                                                                                                                                                                                                      |
|------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `AdwTypography`  | The libadwaita type scale (`.title-1` 181 % … `.caption` 82 % of the 11pt document font, 140 % line height on the body classes): `Title1`…`Title4`, `Heading`, `Body`, `CaptionHeading`, `Caption`, `Monospace`. Use these inside Adwaita widgets instead of `Typography`. |
| `AdwMetrics`     | Every constant the kit lays out with — control radius 9, card 12, popover/dialog/window 15, alert 18, check 6; button height 34 (28 compact, 44 pill), header bar 47 (46 as a titlebar), row minimum 50 (36 sidebar, 40 button row, 32 menu), sidebar 260, clamp 600.      |
| `AdwStyle`       | State → colour resolution: `ButtonFill`, `ButtonForeground`, the five row ladders, `TroughFill`, `SliderKnob`, the disabled/dim opacities, and the ~100 ms fill transitions every button-like control fades with.                                                          |
| `AdwButtonStyle` | `Regular`, `Suggested`, `Destructive`, `Flat`, `Opaque` — plus `Pill`, `Circular` and `Compact` as shape flags on `AdwButton`.                                                                                                                                             |
| `AdwPalette`     | The tokens with no `ThemeData` slot, plus `Fill(theme, percent)` / `Mix` for one-off `color-mix` values.                                                                                                                                                                   |
| `AdwMotion`      | The four springs the kit animates with — `Control`, `Overlay`, `Reveal`, `Value`. Retune the whole library's feel in one place, the way `AdwMetrics` retunes its shape.                                                                                                    |

## Motion

Adwaita motion is **interruptible by default**. Nearly every animation here is one the user can change their mind
about halfway through — a switch flipped twice, a sidebar folded while it is still unfolding, a sheet flicked, caught
and flicked again. A duration-and-curve animation restarts from a standstill each time that happens, which is what
makes a double-tapped switch look broken; a spring keeps the speed it already had and bends toward the new target.

So the kit's widgets set a `MotionSpec` rather than a duration:

| Token               | Response · damping | Used by                                                                                                                                                                                   |
|---------------------|--------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `AdwMotion.Control` | 0.24 s · 0.9       | `AdwSwitch`'s knob, `AdwExpanderRow`'s chevron — small things that flip                                                                                                                   |
| `AdwMotion.Overlay` | 0.30 s · 0.95      | `AdwDialog`, `AdwPopover`, `AdwToast` — things that appear over the content                                                                                                               |
| `AdwMotion.Reveal`  | 0.36 s · 1.0       | `AdwBanner`, `AdwExpanderRow`'s body, `AdwOverlaySplitView`, `AdwNavigationView` — surfaces that change size or slide. Critically damped: the thing that would overshoot is a layout edge |
| `AdwMotion.Value`   | 0.45 s · 1.0       | `AdwProgressBar` — a value settling, so it reads as the number moving rather than the widget animating                                                                                    |

Five consequences worth knowing:

- **`AdwSwitch` slides however it is flipped.** Setting `Value` on a mounted switch — which is what
  an `AdwSwitchRow` click does — animates like a direct tap, and the track colour mixes from trough
  to accent along the knob's travel instead of flipping ahead of it. Only an unmounted switch snaps.

- **`AdwToggleGroup`'s selection slides between segments** instead of jumping — one surface with a
  moving marker, the way libadwaita 1.7 draws it. It reads the laid-out segment bounds rather than
  assuming equal widths, so the marker grows and shrinks as it crosses, and the hairlines it passes
  fade out. `AdwInlineViewSwitcher` keeps its group across page changes so the slide is visible
  there too.

- **`AdwProgressBar` eases to each new `Value`** instead of jumping. A download that reports every few hundred
  milliseconds now reads as one continuous fill. It stops itself the moment it arrives, so an idle bar costs nothing.
- **`AdwBottomSheet` hands the finger's velocity to the settle.** The flick speed no longer only *chooses* which
  detent to snap to — it carries into the motion, so a thrown sheet keeps moving at the speed it was thrown.
- **`AdwStatusPage` fades up on first appearance** (`Animated`, default on). It plays once per mount, never on a
  rebuild.

Colour is the exception: hover and press fills still cross-fade on a ~100 ms curve (`AdwStyle`), because a colour has
no velocity to preserve. Timings that are not springs live in `Durations` (`Fast`/`Standard`/`Slow`).

Everything here honours `AnimationController.ReduceMotion`, which jumps transitions to their end state — except
spinners and indeterminate progress bars, which keep moving, because a frozen spinner reads as a hung app.

## Liquid Glass

Adwaita in structure, Apple in material. GNOME decides where things go; the chrome that *floats over content* — a
player bar, a toolbar riding a picture, a lock-screen card — is made of `LiquidPane`, the engine's refractive glass,
instead of another flat fill. The rules, in the order they matter:

- **Glass is the navigation layer, never the content layer.** The list scrolling underneath a pane stays opaque, so
  there is something to refract.
- **Glass never stacks on glass.** A pane's children are drawn flat; a glass button on a glass bar reads as a smudge.
- **Two variants, and the dial between them.** `LiquidPane.Regular` for adaptive chrome over any content,
  `LiquidPane.Clear` for transparent chrome over dimmed media, and `Tinted` (0 = clear, 1 = regular) to blend a pane
  between them live —
  a slider can drive it without rebuilding anything.
- **One capture per cluster.** Every pane inside a `LiquidGroup` lays its lens down before any pane's content, so a
  toolbar of three panes is one backdrop copy and one render-pass break, not three.
- **Content takes its colour from the glass** — `LiquidPane.OnGlass(theme)` / `OnGlassMuted` — and interactive glass
  is a gel: `LiquidPane.Interactive(pane, onPressed)` wires hover and press into a velocity-preserving
  spring. Reduced Motion snaps directly to the final optical state.

```csharp
var toolbar = new LiquidGroup(
    new Row(spacing: 12f) {
        Children = {
            LiquidPane.Interactive(pane: menu, onPressed: OpenMenu, semantics: "Menu"),
            new Expanded(LiquidPane.Interactive(pane: search, onPressed: Search, semantics: "Search")),
        },
    }
);
```

Not free: each run of lenses costs a scene copy and a render-pass break, and a lens never repaints a patch of itself
— anything changing under one redraws the whole lens. An app that uses it should offer a preference that turns it off.
The gallery's *Liquid Glass* page is the material study: a toolbar over an article with a lab that dials every
property of the glass live (clear↔tinted, float, rim, family, shape — and a switch to the flat Adwaita chrome), an app
window whose header bar and view switcher are GNOME's structure in this material, and a control centre and lock screen
over local wallpapers where controls latch, drag or fold.

The Clear endpoint disables adaptive luminance and uses minimal scattering. Put a dimming layer **outside and before**
`LiquidGroup` so the shared capture includes it, and use bold, bright content.
Regular increases scattering and compresses contrast according to the foreground ink. `Adapt`
controls both effects; `Bevel` controls optical thickness independently. The shader keeps dispersion
subtle, concentrates refraction near the rim, and separates the thin specular lip from inner shading.
The gallery provides Clear/Regular presets, local gradient wallpapers, and a control centre with
connectivity capsules and horizontal Display/Sound sliders.

## Beyond libadwaita: tool widgets

libadwaita stops at document apps. An editor needs more, so the kit carries a set of widgets that have no libadwaita
counterpart and are drawn to its rules instead — the same fill ladders, radii, knob and focus ring as the widgets that
do. Each has a live page in the gallery.

| Widget                 | What it is                                                                                                                                                                                                           | Drawn as                                                                  |
|------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------|
| `AdwTextField`         | The editing engine under `AdwEntry` — single or multi-line, IME, undo                                                                                                                                                | Use `AdwEntry` unless you are building a control around it                |
| `AdwNumberInput`       | A float field you can type in, step, or drag sideways to scrub                                                                                                                                                       | GTK's spin button: one entry box, flat −/+ buttons, plus a grip           |
| `AdwColorPicker`       | Saturation/value plane, hue and alpha scales, hex entry                                                                                                                                                              | GtkColorPlane and GtkColorScale: rounded troughs, scale knobs             |
| `AdwGradientEditor`    | A colour ramp with movable, recolourable stops                                                                                                                                                                       | A control-shaped swatch; each stop is a scale knob                        |
| `AdwCurveEditor`       | Keys and tangents over a grid                                                                                                                                                                                        | A `.view` in a card frame; keys are scale knobs                           |
| `AdwCodeEditor`        | A source view: gutter, highlighting, undo, soft wrap, multi-cursor (⌘D / ⌘⇧L / ⌘⌥↑↓ / Alt+click), bracket pairs + matching, line commands (⌘/, Alt+↑↓, ⇧Alt+↑↓, ⌘⇧K, Tab/⇧Tab), word movement, incremental find (⌘F) | GNOME Text Editor: bare view, dim gutter, accent-palette syntax colours   |
| `AdwTreeView<T>`       | An expandable hierarchy over any data                                                                                                                                                                                | Navigation-sidebar rows with GtkTreeExpander indents                      |
| `AdwReorderableList`   | Rows put in order by dragging                                                                                                                                                                                        | A boxed list with a drag-handle prefix; the held row lifts onto a card    |
| `AdwChip`              | A pill toggle for tags and filters                                                                                                                                                                                   | A small `.pill` toggle button — neutral when checked, not accent          |
| `AdwBadge`             | A count pinned to a corner of its child                                                                                                                                                                              | The view switcher's needs-attention indicator                             |
| `AdwToolbar`           | A strip of leading and trailing controls                                                                                                                                                                             | GTK's `.toolbar` box: 6px padding and spacing, no background              |
| `AdwPanel`             | A floating, movable, resizable utility window                                                                                                                                                                        | A dialog surface under a flat header with a circular close button         |
| `AdwFileBrowserDialog` | The in-app file chooser `FileDialog` falls back to                                                                                                                                                                   | GtkFileChooser: places sidebar, path bar, sortable list, suggested accept |
| `AdwResponsiveGrid`    | A masonry grid whose column count follows the width                                                                                                                                                                  | Layout only — it paints nothing                                           |

## Differences from libadwaita

Stated up front so nothing surprises you:

- **Names, not ABI.** The API mirrors libadwaita so the GNOME HIG and its documentation transfer, but this is not
  API-compatible with the C library, and not every libadwaita widget exists.
- **Bundled type and icons.** Text renders in **Inter**, icons come from the bundled **Material Icons** font. Adwaita
  Sans / Cantarell and the system icon theme are not read.
- **`button-layout` is honoured; the compositor's full gesture policy is not.** Window-manager behaviours beyond the
  framework's own drag and double-click handling are outside this kit.
- **GTK settings beyond appearance, accent and button layout are not consulted** — font scaling, cursor themes and
  animation preferences follow Zigote's own settings.
- **Two colours are held deliberately off-spec, for this renderer.** The renderer composites in linear space, so
  translucent values land lighter than CSS means them to; `AdwPalette` solves for the alpha that reproduces the
  stylesheet's result, but the light-mode foreground is carried at
  `alpha(#000006, .85)` against the stylesheet's `.8`, and `DimLabel` at `.65` against `.55`. At the authored values the
  12px captions this UI leans on sit at the AA floor or under it.
- **Shadows are one soft layer each, not three.** `%card`, `popover > contents` and the window frame each stack three
  shadows in CSS; `AdwMetrics.CardShadow` / `PopoverShadow` collapse them to the single layer this renderer paints, with
  the hairline ring drawn separately as a border.
- **Gradients are flattened.** An `AdwAvatar` uses libadwaita's fourteen avatar colours but fills with the gradient's
  darker tone instead of interpolating between the two.
- **High-contrast and backdrop variants are not implemented.** The stylesheet's
  `prefers-contrast: more` block and its `:backdrop` fades have no counterpart here.
- Properties on `ComposedWidget`-derived widgets use `AdwStyle.Set(ref field, value)`, which invalidates on real change.
  A plain auto-property would silently do nothing after the first build, because the `Build` result is retained.

---

## Dependencies

`Zigote.UI` (the kernel) and `Zigote.UI.BottomSheet`. Internals that are pure logic — accelerator parsing, layout
maths — are unit-tested directly
from `Zigote.Tests` rather than pushed into the public surface: this kit mirrors libadwaita's API and should not grow
extras.

## Related

- [`../Zigote.UI.Adwaita.Gallery`](../Zigote.UI.Adwaita.Gallery/README.md) — the 49-page gallery.
- [`../docs/migration/`](../docs/migration/README.md) — retained mode, and per-framework guides.
- [`../docs/architecture.md`](../docs/architecture.md) — how the kernel underneath works.
