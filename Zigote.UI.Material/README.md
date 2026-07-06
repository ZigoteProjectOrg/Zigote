# Zigote.UI.Material

A **Material-style** widget set on `Zigote.UI`. Where `Zigote.UI` gives you the retained widget
kernel (layout, controls, transitions, navigation) with terse Zigote-native constructors, this package
layers the **Material vocabulary and a named-argument constructor style** on top — `Scaffold`,
`AppBar`, `ElevatedButton`, `ListTile`, `FloatingActionButton`, `CheckboxListTile`, and friends — so a
Material widget tree ports across almost line-for-line.

It references only `Zigote.UI` (which references only `Zigote.Core`) — no scene, scripting, or editor
coupling. It's a second surface over the same kernel, not a fork: every widget composes the underlying
`Zigote.UI` primitives, so it shares the theme, focus, semantics, and hot-reload machinery.

## The idea

`Zigote.UI` names things its own way and constructs them positionally. `Zigote.UI.Material` gives you the
Material names and **named-argument, all-optional** constructors:

```csharp
// Zigote.UI.Material — reads declaratively
new Scaffold(
    appBar: new AppBar(title: new Label("Home")),
    body: new Center(child: new ElevatedButton("Tap me", onPressed: () => count++)),
    floatingActionButton: new FloatingActionButton(icon: Icons.Add, onPressed: AddItem));
```

Every constructor argument is optional, so the object-initialiser form still works
(`new Scaffold { Body = … }`). Many widgets are thin **aliases** over a `Zigote.UI` control
(`FilterChip`/`ChoiceChip` → `Chip`, `ReorderableListView` → `ReorderableList`,
`VerticalDivider` → `Divider`), translating the Material API surface onto the existing implementation.

## Quick start

```csharp
using Zigote.UI.Material;

class HomePage : StatefulWidget
{
    protected override WidgetState CreateState() => new HomeState();
}

class HomeState : WidgetState<HomePage>
{
    private int _count;

    public override Widget Build(BuildContext ctx) => new Scaffold(
        appBar: new AppBar(title: new Label("Counter")),
        body: new Center(child: new Label($"Count: {_count}")),
        floatingActionButton: new FloatingActionButton(
            icon: Icons.Add,
            onPressed: () => SetState(() => _count++)));
}

new MaterialApp(title: "Demo", theme: ThemeData.Dark, home: new HomePage()).Run();
```

`MaterialApp` is a named-argument constructor over `ZigoteApp` — it boots the engine, injects the theme +
`MediaQuery`, and wraps `Home` in a root `Navigator`, so `context.Push`/`Pop`, named `Routes`,
`InitialRoute`, `OnGenerateRoute`, and declarative `Pages`/`OnPopPage` all work exactly as on `ZigoteApp`.

## What's included

Scaffolding & navigation
: `MaterialApp`, `Scaffold` (app bar + body + FAB slots), `AppBar`, `Toolbar` / `ToolbarButton`,
`TabBar` / `Tab` / `TabBarView` / `TabView`, `NavigationSplitView`, `SplitPane`, `Panel`.

Buttons & actions
: `ElevatedButton`, `FilledButton`, `OutlinedButton`, `TextButton`, `IconButton`,
`FloatingActionButton`, `DropdownButton` / `DropdownMenuItem`, `InkWell` (ripple/hover primitive).

Selection & input
: `Checkbox` / `CheckboxListTile`, `Radio` / `RadioListTile`, `Switch` / `SwitchListTile`, `Slider`,
`Stepper`, `NumberInput`, `SegmentedControl`, `Chip` / `FilterChip` / `ChoiceChip`,
`TextField` (+ `TextEditingController`, `InputDecoration`), `SearchField`, `AutoSuggestField`,
`Dropdown`.

Display & layout
: `ListTile`, `Divider` / `VerticalDivider`, `Badge`, `CircleAvatar`, `AsyncImage`, `ResponsiveGrid`,
`ReorderableList` / `ReorderableListView`, `TreeView`, `CodeEditor`.

Progress & feedback
: `LinearProgressIndicator`, `CircularProgressIndicator`, `ProgressBar`, `Spinner`.

Pickers & editors
: `ColorPicker` / `ColorSwatchField`, `GradientEditor`, `CurveEditor`, `FilePickerDialog`,
`TexturePanel`.

## Gotcha — `Text` / `Theme` sub-namespace shadowing

`Zigote.UI.Material` pulls in several `Zigote.UI.*` sub-namespaces via `GlobalUsings.cs`
(`Zigote.UI.Text`, `Zigote.UI.Theme`, …). Those sub-namespaces **shadow the bare `Text` and `Theme`
types** under enclosing-namespace lookup. If you need the bare `Text`/`Theme`, alias it explicitly
(a file-scoped `using Text = …;`) or fully-qualify. This is why the widgets label things with `Label`
rather than a bare `Text`.

## Relationship to the other UI packages

`Zigote.UI.Material` is one of several design-language layers over the same `Zigote.UI` kernel — alongside
`Zigote.UI.Cupertino` (iOS) and `Zigote.UI.AppKit` (macOS desktop). Pick one per app; they don't mix in a
single tree. The default flat-macOS `ThemeData` controls live in `Zigote.UI` itself.
