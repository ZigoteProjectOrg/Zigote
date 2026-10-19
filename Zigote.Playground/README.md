# Zigote.Playground

The documentation as an app. Thirty-two pages, from the introduction through widgets, layout,
text input and forms, keyboard and focus, theming, internationalization, signals and reactivity in
depth, scheduling and parallelism, Adwaita, motion (implicit and explicit animations, transitions)
to the frame, custom painting, custom WGSL shaders, the renderer, platform channels and plugins,
native interop, the 3D stack and a best-practices guide. Each page mixes prose with **live examples**: editable C# that is compiled in-process by
Roslyn and runs in a preview under the editor. Edit an example, press **Run** or `⌘S`, and the
preview swaps.

```sh
dotnet run --project Zigote.Playground
dotnet run --project Zigote.Playground -- --self-test    # compile + construct every example
ZIGOTE_PLAYGROUND_PAGE="Layout" dotnet run --project Zigote.Playground
```

## The widget catalog

The **Catalog** switch in the sidebar header opens the generated reference, kept separate from the
hand-written Docs: one page for each of the ~190 public widgets in `Zigote.UI` and
`Zigote.UI.Adwaita`, built at startup by reflection plus the XML doc comments those libraries now
ship (`GenerateDocumentationFile`). Each page has the summary, a live sample, constructors with
parameter docs, every property with its default value (read from a freshly constructed instance),
methods, events and subclasses.

Every Zigote type name is a link. Following one opens a generated **type page**: enum values with
their docs, presets (static values such as `Alignment.Center`), constructors, properties, and a
*Used by* list of the widget properties that take the type. Back in the header walks the history.

- Code: `Catalog/WidgetCatalog.cs` (discovery, categories, pages, samples), `Catalog/XmlDocs.cs`
  (doc-comment → Markdown, `<see cref>` → links). Pages render through the same `DocPage` as Docs.
- Samples: a curated `Catalog/Samples/{TypeName}.cs` (a `class Demo`; optional first line
  `// height: 300`, stripped from the displayed source) wins; otherwise the generator writes the
  simplest construction it can, filling required parameters and visible optional ones (`label`,
  `child`, `onPressed`, …). 47 widgets are curated today — the kernel's layout, text, list, gesture
  and animation basics, and Adwaita's buttons, entries, rows, dialogs, toasts and navigation.
  Widgets that act on the real window (`AdwWindowControls`, `WindowTitleBar`, `WindowChromeHost`)
  get no live sample. An embedded `AdwHeaderBar`/`AdwNavigationView` sample must turn its window
  controls off.
- `--self-test` also generates every widget and type page, compiles every sample, and fails on a
  curated sample whose file name matches no widget.

## Adding a page

1. Write `Docs/<slug>.md`. The supported Markdown is headings, paragraphs, lists, `>` notes,
   pipe tables, fenced code, and inline `` `code` ``, `**bold**` and `*italic*`.
2. Add one `DocEntry` to `Toc.cs`.

A fence opened with ` ```csharp run ` (optionally `height=300`) is a live example: a C# file
with a `class Demo` that is any `Widget` with a parameterless constructor. The usings listed in
`Compiler.Usings` are implicit. Run `--self-test` after editing; it fails on any example that no
longer compiles or throws while it is constructed.

## How it works

- `Compiler` compiles the editor text against every assembly the app loaded (the trusted platform
  list), emits it with an embedded PDB, and loads it into its own collectible `AssemblyLoadContext`.
  Compiling happens on a worker thread; the result lands in a signal, and the preview's `Watch`
  swaps in the new widget.
- An example that throws during a frame (in `Build`, layout, paint or an input handler) reaches
  `ZigoteApp.OnFrameError`. `ExampleView.Blame` finds the example whose assembly is on the stack
  and replaces its preview with the exception; the rest of the app keeps running. An infinite loop
  still freezes the window, because examples run on the UI thread.
- JIT only: Roslyn emits IL at runtime, so this app has no Native AOT publish.
