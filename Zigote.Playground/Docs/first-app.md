# Your First App

A Zigote app is an ordinary .NET console project with two references: the widget kernel and a design system.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Zigote.UI\Zigote.UI.csproj" />
    <ProjectReference Include="..\Zigote.UI.Adwaita\Zigote.UI.Adwaita.csproj" />
  </ItemGroup>
</Project>
```

The entry point creates an app object and calls `Run()`. `AdwaitaApp` boots the native engine, opens the window, installs the theme (following the system's light/dark appearance) and drives the frame loop until the window closes.

```csharp
public static class Program
{
    public static void Main() => new HelloApp().Run();
}

sealed class HelloApp : AdwaitaApp
{
    public HelloApp() : base(home: new CounterPage(), title: "Hello")
    {
        Width = 420;
        Height = 520;
    }
}
```

`zigote create MyApp` (from `Zigote.Cli`) scaffolds exactly this.

## The counter

Here is the whole counter page. It has one piece of state, a `Signal<int>`, and one `Watch` that reads it. Press the button and only the text inside the `Watch` is rebuilt. The rest of the page is built once and never touched again.

```csharp run height=300
class Demo : ComposedWidget
{
    private readonly Signal<int> _count = new(0);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new Center(new Column(
            mainAxisSize: MainAxisSize.Min,
            spacing: 12,
            children: [
                new Label(text: "You have pushed the button this many times:",
                          style: AdwTypography.Body, color: theme.TextSecondary),
                new Watch(() => new Label(text: _count.Value.ToString(), style: AdwTypography.Title1)),
                new AdwButton(label: "Increment", onPressed: () => _count.Value++) {
                    Style = AdwButtonStyle.Suggested,
                    Pill = true,
                },
            ]));
    }
}
```

Try it:

- Change the step to `_count.Value += 10`.
- Add a second button that resets the count to zero.
- Move the `Watch` so that it wraps the whole `Column`. The page still works, but now every press rebuilds the whole column. Scope a `Watch` as tightly as the thing that changes.

## Run, hot reload, inspect

- `dotnet run` starts the app. `dotnet watch` adds **hot reload**: edit a `Build` method, save, and the live window updates while every widget keeps its state.
- **Shift+D** opens DevTools if you reference `Zigote.UI.DevTools`. It shows the widget tree, layout bounds, frame timings and rebuild counters.
- Set `ZIGOTE_INSPECT=1` and tools such as the Rider plugin or the MCP server can drive the running app (see *Tooling*).
