# Zigote.UI.Adwaita.Gallery

The reference app for [`Zigote.UI.Adwaita`](../Zigote.UI.Adwaita/README.md) — **51 pages**, one per widget family, every
one of them live. No screenshots, no mock rows: what you see is the widget doing the thing.

```sh
dotnet run --project Zigote.UI.Adwaita.Gallery
```

<img src="../docs/images/adwaita-buttons.png" alt="The Buttons page" width="820">

---

## The shell

One window is an adaptive `AdwNavigationSplitView` under an `AdwToastOverlay`: a searchable, sectioned `AdwSidebar` on
the left, the selected page under a header bar that names it on the right. Below **620 px** the two panes fold into a
single navigable page and the content header grows a back button — the same tree, no second layout.

<img src="../docs/images/adwaita-narrow.png" alt="The gallery folded to one pane at phone width" width="300">

Windows are peers. <kbd>⌘/Ctrl</kbd>+<kbd>N</kbd> opens another one with its own navigation, search and toasts;
shortcuts act on the window they were pressed in.

## Shortcuts

All use the platform command modifier — ⌘ on macOS, Ctrl elsewhere.

| Chord                                        | Action                              |
|----------------------------------------------|-------------------------------------|
| <kbd>Cmd</kbd>+<kbd>F</kbd>                  | Reveal the search bar and focus it  |
| <kbd>Cmd</kbd>+<kbd>N</kbd>                  | New window                          |
| <kbd>Cmd</kbd>+<kbd>,</kbd>                  | Preferences                         |
| <kbd>Cmd</kbd>+<kbd>D</kbd>                  | Toggle dark (drops "follow system") |
| <kbd>Cmd</kbd>+<kbd>W</kbd>                  | Close window                        |
| <kbd>Cmd</kbd>+<kbd>Shift</kbd>+<kbd>/</kbd> | About                               |

## Pages

| Section        | Pages                                                                                                                                                                  |
|----------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Navigation** | Navigation View · Split Views · Paned · Breakpoints · View Switcher · Tab View · Toolbar & Panel · Bottom Sheet · Carousel · Image Grid                                |
| **Controls**   | Buttons · Toggles · Checks & Radios · Sliders & Progress · Entries · Colour & Completion · Value Editors · Date & Time · Chips & Badges · Shortcuts · Menus & Popovers |
| **Lists**      | Boxed Lists · Preferences · Large Lists (2 000 rows, recycled while you scroll) · Data Table (100 000 rows) · Trees & Reordering                                       |
| **Feedback**   | Banners · Toasts · Alert Dialogs · File Chooser · Spinner · Status Pages · Avatar                                                                                      |
| **Layout**     | Clamp · Wrap Box · Masonry Grid · Adaptive                                                                                                                             |
| **Style**      | Liquid Glass · Style Classes · Typography · Colors · Animations                                                                                                        |
| **Zigote**     | Graphs · Code Editor · Reactivity · Concurrency · Drag and Drop                                                                                                        |

The last section is the framework rather than the design system: signals and computed values driving a live order total,
background threads writing signals under a per-frame delivery budget, and draggable payloads with drop targets.

## Graphs

Open **Zigote → Graphs**, or launch directly:

```sh
ZIGOTE_GALLERY_PAGE=Graphs dotnet run --project Zigote.UI.Adwaita.Gallery
```

<img src="Screenshots/graphs.png" alt="Graphs playground with typed cables and live preview" width="820">

The full-height editor uses `Zigote.Graphs.Editor` with a standalone demo domain; it does not depend on Palco or audio
hardware.

- **Playground:** numeric, text, boolean, integer and enum controls feed a live Preview widget. Colored sockets
  distinguish numbers, strings and booleans. The Switch starts collapsed; Math's optional B input demonstrates a default
  value.
- **Repair wiring:** a deliberately invalid string-to-number cable and a missing required input. Follow the on-screen
  instructions to restore the preview. Invalid drag targets also reject duplicates, occupied inputs and feedback loops
  with an explanation.
- **Large graph:** 29 nodes for zoom, pan, frame-all, minimap, resizing and group selection.

Drop an input or output cable onto empty canvas to search compatible nodes and add-and-connect in one undo step.
**Shift+A / Space** opens general search; type, select with **↑/↓**, press **Enter**, or cancel with **Escape**. The
**How to use** panel covers inline editing, fine adjustments, collapsing, context actions, deletion, selection,
navigation and undo/redo. Inspector and Minimap are available in the editor toolbar.

Switching or resetting examples discards their edits. Theme changes preserve the graph document; editor history restarts
when the themed editor is rebuilt. The gallery's `--self-test` also checks evaluation, invalid wiring, repair/undo/redo
and feedback rejection.

## Appearance

<img src="../docs/images/adwaita-preferences.png" alt="The preferences dialog" width="820">

The preferences dialog is an `AdwPreferencesDialog` over three signals — follow-system, dark, and one of the nine GNOME
accents. Each change rebuilds a `ThemeData` and pushes it to **every open window**, which is the point of keeping the
whole palette in one theme object. On GNOME, follow-system tracks the desktop's appearance and accent live.

## Adding a page

[`GalleryRegistry`](GalleryRegistry.cs) is the single table of contents — the sidebar, the search index, the header
titles and the welcome page's cross-links are all derived from it. A new page is one entry:

```csharp
new GalleryEntry("Widget Name", "One line under it in search",
                 MaterialIcons.SomeIcon, () => new MyPage()),
```

Pages return a `GalleryPage` — the shared scaffold that gives every one of them the same GNOME preferences-page rhythm:
a scrolling, clamped column under a dim hero icon, title and one-line description. They are built once and kept, so
leaving a page and coming back finds its animation, carousel position and counters where they were left.

## Self-test

```sh
dotnet run --project Zigote.UI.Adwaita.Gallery -- --self-test
```

Headless, no window, exit code is the result. It constructs **every** page — which is what catches a registry entry
wired to a type that throws in its constructor — checks that the sidebar's row order matches the index space the shell
selects in, that titles are unique and round-trip through
`IndexOf`, and runs the concurrency page's own deterministic check on concurrent signal writes.
