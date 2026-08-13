using Zigote.Core.Paint;
using Zigote.Core.State;
using Zigote.UI.DevTools;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace HelloWorld;

/// <summary>
///     The smallest complete Zigote UI app: a greeting and a counter.
///     <para>
///         <c>MaterialApp</c> boots the engine, opens the window and installs the theme; <c>Run()</c>
///         drives the frame loop until the window closes.
///     </para>
/// </summary>
public static class Program
{
    public static void Main() => new HelloWorldApp().Run();
}

internal sealed class HelloWorldApp : MaterialApp
{
    public HelloWorldApp() : base(
        home: new CounterPage(),
        title: "Zigote Hello World",
        theme: ThemeData.Dark
    )
    {
        Width = 420;
        Height = 520;
    }

    /// <summary>
    ///     Runs once the engine and the <c>App</c> exist, before the first frame — the hook for
    ///     anything that needs the live app rather than just the widget tree.
    /// </summary>
    protected override void OnInit()
    {
        base.OnInit();
        if (App is not { } app) return;

        // The devtools overlay: press Shift+D. TwoD is the profile for a pure UI app — General and
        // 2D·UI tabs, no 3D renderer tab. (Merely referencing Zigote.UI.DevTools would auto-install
        // it with the Auto profile; this line is the explicit form, and it picks the profile.)
        DevTools.Install(app: app, profile: DevToolsProfile.TwoD);
    }
}

/// <summary>
///     One screen, one piece of state.
///     <para>
///         The count is a <see cref="Signal{T}" /> — a reactive value. <see cref="Watch" /> runs its
///         builder under dependency tracking, so it rebuilds <b>only</b> the subtree that read the
///         signal: pressing the button re-runs the one <c>Text</c>, not the page. Open devtools and
///         watch <c>ui.watch_rebuilds</c> tick by one per press to see it.
///     </para>
///     <para>
///         The state lives in the signal, not in the widget. (Coming from Flutter? A widget's fields
///         are its state here — mutate them and call <c>MarkNeedsLayout</c>. But a signal is less
///         ceremony and rebuilds less.)
///     </para>
/// </summary>
internal sealed class CounterPage : ComposedWidget
{
    // Safe as a field here because this page instance is retained for the app's lifetime. A signal
    // that must outlive rebuilds of its *owner* belongs above it — in a store the page is handed,
    // the way Zigote.UI.Gallery does it — or the state resets when the owner is reconstructed.
    private readonly Signal<int> _count = new(0);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new Scaffold(
            appBar: new AppBar(title: new Text("Hello World"), centerTitle: true),
            body: new Center(
                new Column(
                    mainAxisAlignment: MainAxisAlignment.Center,
                    children: [
                        new Text(
                            data: "Hello, World!",
                            style: new TextStyle(fontSize: 28, fontWeight: FontWeight.Bold)
                        ),
                        new SizedBox(height: 8),
                        new Text(
                            data: "You have pushed the button this many times:",
                            style: new TextStyle(color: theme.TextSecondary)
                        ),
                        new SizedBox(height: 8),
                        // Only this subtree re-runs when _count changes.
                        new Watch(() => new Text(
                                data: _count.Value.ToString(),
                                style: new TextStyle(fontSize: 34, fontWeight: FontWeight.SemiBold)
                            )
                        ),
                        new SizedBox(height: 24),
                        new Text(
                            data: "Press Shift+D for devtools",
                            style: new TextStyle(fontSize: 12, color: theme.TextMuted)
                        ),
                    ]
                )
            ),
            floatingActionButton: new FloatingActionButton(
                onPressed: () => _count.Value++,
                child: new Icon(MaterialIcons.Add),
                tooltip: "Increment"
            )
        );
    }
}
