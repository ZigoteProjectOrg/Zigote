# Migrating to Zigote UI

Guides for developers arriving from another declarative UI toolkit. Every code sample here compiles
against the types that ship today — no proposals, no pseudocode.

| If you are coming from | Read |
|---|---|
| Flutter / Dart | [`from-flutter.md`](from-flutter.md) |
| Jetpack Compose / Compose Multiplatform | [`from-compose.md`](from-compose.md) |
| SwiftUI | [`from-swiftui.md`](from-swiftui.md) |
| WPF / Avalonia / WinUI (XAML + MVVM) | [`from-wpf-avalonia.md`](from-wpf-avalonia.md) |

**Read [`concepts.md`](concepts.md) first, whichever you came from.** It covers the one design
decision that makes Zigote different from all of them — *retained mode* — and the four rules that
follow from it. Nearly every bug a newcomer files traces back to that page.

Then [`cookbook.md`](cookbook.md) has worked solutions to the problems every real app hits: async
load with error and retry, virtualized lists, debounced search, forms, master/detail, background
work, dialogs with results, theming, and headless tests.

---

## Orientation in 60 seconds

```csharp
using Zigote.Core.State;          // Signal<T>, Computed, Effect
using Zigote.UI.Host;             // ZigoteApp, App
using Zigote.UI.Theme;            // ThemeData, Spacing, Typography, Radii
using Zigote.UI.Widgets;          // Widget, ComposedWidget, Watch, BuildContext
using Zigote.UI.Widgets.Controls; // Label, Button, Icon, GestureDetector
using Zigote.UI.Widgets.Layout;   // Column, Row, Stack, Padding, ListView, ScrollView

new ZigoteApp
{
    Title  = "Hello",
    Theme  = ThemeData.Dark,
    Home   = new CounterPage(),
}.Run();

public sealed class CounterPage : ComposedWidget
{
    private readonly Signal<int> _count = new(0);

    protected override Widget Build(BuildContext ctx) => new Center(
        new Column(
            mainAxisSize: MainAxisSize.Min,
            spacing: Spacing.Md,
            children:
            [
                new Watch(() => new Label($"Count: {_count.Value}")),
                new Button("Increment", () => _count.Value++),
            ]));
}
```

Three things to notice, because they all differ from where you came from:

1. `Build` runs **once**, not per state change. The tree it returns is retained and mutated.
2. `Watch` is the bridge from a signal to the tree. Only the subtree inside it re-runs.
3. `_count` is a field on the widget, not a local in `Build`. Locals in `Build` never re-evaluate.

---

## Which package do I reference?

| Package | You want it when |
|---|---|
| `Zigote.UI` | Always. The kernel: layout, controls, focus, navigation, animation, semantics. Terse, positional constructors, Zigote-native names. |
| `Zigote.UI.Material` | You are porting Material or Flutter code. Material names (`Scaffold`, `AppBar`, `ElevatedButton`, `ListTile`, `TextField`) with named-argument constructors, over the same kernel. Also the home of `TextField`, `Dropdown`, `Slider`, `Switch`, `TabBar` — the kernel does not duplicate them. |
| `Zigote.UI.Adwaita` | Linux-first apps that must look native on GNOME: `AdwHeaderBar`, `AdwNavigationSplitView`, `AdwPreferencesPage`, live system light/dark + accent. |
| `Zigote.Bloc` | Any app with more than trivial state. Events in, ordered; state out as signals. |
| `Zigote.UI.Charts` | Declarative charts. |
| `Zigote.UI.Localizations` | Locales, plural rules, typed message codegen. |

Mixing is normal and supported — they are surfaces over one kernel, not forks. Timbre uses
`Zigote.UI` + `Zigote.UI.Material` + `Zigote.UI.Adwaita` + `Zigote.Bloc` together.

---

## Project setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="$(ZigoteRoot)/Zigote.UI/Zigote.UI.csproj" />
    <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.Material/Zigote.UI.Material.csproj" />
    <ProjectReference Include="$(ZigoteRoot)/Zigote.Bloc/Zigote.Bloc.csproj" />
  </ItemGroup>
</Project>
```

A `GlobalUsings.cs` pays for itself immediately — the widget types live in several namespaces:

```csharp
global using Zigote.Bloc;
global using Zigote.Core;                    // Color, Size, Offset, Rect, EdgeInsets, Constraints
global using Zigote.Core.Paint;
global using Zigote.Core.State;
global using Zigote.UI.Host;
global using Zigote.UI.Theme;
global using Zigote.UI.Widgets;
global using Zigote.UI.Widgets.Controls;
global using Zigote.UI.Widgets.Layout;
global using Zigote.UI.Widgets.Transitions;
```

Prerequisites: .NET 10 SDK and Zig 0.16 on `PATH` (the solution builds the native engine).
The native engine rebuild dominates a cold build; `dotnet build --no-restore` after the first
run, and see the engine README for skipping the Zig step when it has not changed.

---

## What you will not find

Stated up front so you do not go looking:

- **No XAML, no markup language.** Trees are C# (or F#, via `Zigote.UI.FSharp`). No designer.
- **No accessibility bridge yet.** `Zigote.UI/Semantics/` builds a complete platform-neutral tree and
  `ISemanticsBridge` is the seam, but no AT-SPI / UIA / VoiceOver implementation ships. Screen
  readers will not see your app. If you are under an accessibility mandate, that is a blocker today.
- **No web target.**
- **Mobile is in bring-up.** Touch, lifecycle, safe-area and the Android/iOS native builds work; see
  `docs/mobile-port.md` for what is still open.
- **No third-party widget ecosystem.** What ships is what exists.

---

## Where to look in the source

The framework is small enough to read, and the XML doc comments on the types are the reference
manual — most of them explain *why*, not just *what*.

| Question | File |
|---|---|
| What is the widget contract? | `Zigote.UI/Widgets/Widget.cs` |
| How does state work? | `Zigote.UI/Widgets/ComposedWidget.cs`, `Zigote.Core/State/Signal.cs` |
| What does the frame loop do? | `Zigote.UI/App/App.cs` |
| What controls exist? | `Zigote.UI/Widgets/Controls/`, `Zigote.UI.Material/Widgets/` |
| How do I test this? | `Zigote.Tests/` — every test is headless |
| A real app end to end | [Timbre](https://github.com/zigote) — music player, ~30k lines |
