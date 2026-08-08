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
    public static void Main()
    {
        new HelloWorldApp().Run();
    }
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
        DevTools.Install(app, DevToolsProfile.TwoD);
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
///         The page can therefore be a <see cref="StatelessWidget" />: the state lives in the signal,
///         not in the widget. (Coming from Flutter? A <c>StatefulWidget</c> + <c>SetState</c> also
///         exists and works the way you'd expect — but a signal is less ceremony and rebuilds less.)
///     </para>
/// </summary>
internal sealed class CounterPage : StatelessWidget
{
    // Safe as a field here because this page instance is retained for the app's lifetime. A signal
    // that must outlive rebuilds of its *owner* belongs above it — in a store the page is handed,
    // the way Zigote.UI.Gallery does it — or the state resets when the owner is reconstructed.
    private readonly Signal<int> _count = new(0);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new Scaffold(
            new AppBar(new Text("Hello World"), centerTitle: true),
            new Center(
                new Column(
                    mainAxisAlignment: MainAxisAlignment.Center,
                    children: [
                        new Text(
                            "Hello, World!",
                            new TextStyle(28, fontWeight: FontWeight.Bold)
                        ),
                        new SizedBox(height: 8),
                        new Text(
                            "You have pushed the button this many times:",
                            new TextStyle(color: theme.TextSecondary)
                        ),
                        new SizedBox(height: 8),
                        // Only this subtree re-runs when _count changes.
                        new Watch(() => new Text(
                                _count.Value.ToString(),
                                new TextStyle(34, fontWeight: FontWeight.SemiBold)
                            )
                        ),
                        new SizedBox(height: 24),
                        new Text(
                            "Press Shift+D for devtools",
                            new TextStyle(12, color: theme.TextMuted)
                        ),
                    ]
                )
            ),
            new FloatingActionButton(
                () => _count.Value++,
                new Icon(MaterialIcons.Add),
                tooltip: "Increment"
            )
        );
    }
}