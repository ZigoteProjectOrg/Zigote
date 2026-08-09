# Cookbook

Worked solutions to the problems every real app hits. Framework-agnostic — read your migration guide
for the vocabulary, this for the shape.

Recipes assume the usings from [`README.md`](README.md#project-setup).

---

## Contents

- [Load something async, with loading / error / retry](#load-something-async-with-loading--error--retry)
- [Debounced search with latest-wins cancellation](#debounced-search-with-latest-wins-cancellation)
- [A list of fifty thousand rows](#a-list-of-fifty-thousand-rows)
- [Master/detail that folds at phone width](#masterdetail-that-folds-at-phone-width)
- [A form with validation](#a-form-with-validation)
- [Background work without hitching the frame](#background-work-without-hitching-the-frame)
- [A dialog that returns a value](#a-dialog-that-returns-a-value)
- [Follow the system theme](#follow-the-system-theme)
- [Animate a state change](#animate-a-state-change)
- [Application keyboard shortcuts](#application-keyboard-shortcuts)
- [Test it headlessly](#test-it-headlessly)
- [Wire up error reporting](#wire-up-error-reporting)

---

## Load something async, with loading / error / retry

The state machine lives in a bloc; the view is a `Watch` over its state.

```csharp
public abstract record ProfileEvent
{
    public sealed record Requested(int UserId) : ProfileEvent;
    public sealed record Loaded(User User)     : ProfileEvent;
    public sealed record Failed(string Reason) : ProfileEvent;
}

public sealed record ProfileState(User? User, bool Busy, string? Error)
{
    public static ProfileState Initial => new(null, false, null);
}

public sealed class ProfileBloc(IUserApi api)
    : Bloc<ProfileEvent, ProfileState>(ProfileState.Initial)
{
    protected override async ValueTask OnEventAsync(ProfileEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case ProfileEvent.Requested(var id):
                Emit(Current with { Busy = true, Error = null });
                try
                {
                    Add(new ProfileEvent.Loaded(await api.GetAsync(id, Restart())));
                }
                catch (OperationCanceledException) { }        // superseded — the newer request owns the state
                catch (Exception ex)
                {
                    Add(new ProfileEvent.Failed(ex.Message));
                }
                break;

            case ProfileEvent.Loaded(var user):
                Emit(new ProfileState(user, false, null));
                break;

            case ProfileEvent.Failed(var reason):
                Emit(Current with { Busy = false, Error = reason });
                break;
        }
    }
}
```

```csharp
public sealed class ProfilePage(ProfileBloc bloc, int userId) : ComposedWidget
{
    protected override Widget Build(BuildContext ctx)
    {
        bloc.Add(new ProfileEvent.Requested(userId));   // Build runs once — this fires once

        return new Watch(() => bloc.State.Value switch
        {
            { Busy: true }            => new Center(new Spinner()),
            { Error: { } message }    => Retry(message),
            { User: { } user }        => Content(user),
            _                         => SizedBox.Shrink(),
        });
    }

    private Widget Retry(string message) => new Center(new Column(
        mainAxisSize: MainAxisSize.Min,
        spacing: Spacing.Md,
        children:
        [
            new Label(message) { Color = ThemeData.Dark.Error },
            new Button("Try again", () => bloc.Add(new ProfileEvent.Requested(userId))),
        ]));

    private static Widget Content(User user) => new Label(user.Name);
}
```

Why the bloc and not `FutureBuilder<T>`: `Restart()` gives you latest-wins cancellation, the state is
inspectable and testable without a widget tree, and the retry path is one more `Add` rather than a
new `Future`. `FutureBuilder<T>` is fine for a genuinely one-shot load with no retry:

```csharp
new FutureBuilder<User>(api.GetAsync(userId), (ctx, snap) =>
    snap switch
    {
        { IsWaiting: true } => new Spinner(),
        { HasError: true }  => new Label(snap.Error!.Message),
        { HasData: true }   => new Label(snap.Data!.Name),
        _                   => SizedBox.Shrink(),
    });
```

---

## Debounced search with latest-wins cancellation

Two independent concerns: don't hit the API on every keystroke (debounce), and don't let a slow
early response overwrite a fast late one (latest-wins). `Restart()` handles the second for free.

```csharp
public sealed class SearchBloc(ISearchApi api)
    : Bloc<SearchEvent, SearchState>(SearchState.Empty)
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    protected override async ValueTask OnEventAsync(SearchEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case SearchEvent.QueryChanged(var text):
                Emit(Current with { Query = text, Busy = text.Length > 0 });
                if (text.Length == 0) { Emit(Current with { Hits = [], Busy = false }); return; }

                // Restart() cancels the previous search — including one still inside its debounce.
                var token = Restart();
                try
                {
                    await Task.Delay(Debounce, token);
                    Add(new SearchEvent.ResultsArrived(await api.SearchAsync(text, token)));
                }
                catch (OperationCanceledException) { }
                break;

            case SearchEvent.ResultsArrived(var hits):
                Emit(Current with { Hits = hits, Busy = false });
                break;
        }
    }
}
```

The field wires straight in:

```csharp
new TextField(
    decoration: new InputDecoration(hintText: "Search"),
    onChanged: text => bloc.Add(new SearchEvent.QueryChanged(text)));
```

---

## A list of fifty thousand rows

`ListView` virtualizes **measure, layout and paint** to the viewport — scrolling stays O(viewport).
`ListView.Builder` virtualizes *construction* on top of that: a row is built when it enters the
window and destroyed when it leaves, so fifty thousand rows cost what fifty do.

```csharp
var list = ListView.Builder(rows.Count, i => RowFor(rows[i]), itemExtent: 36);
list.HeightOf = i => rows[i].IsHeader ? 48f : 36f;     // still O(viewport), still index-driven
```

`GridView.Builder(crossAxisCount, itemCount, i => …)` is the grid form — one row of cells built at a
time, and it scrolls itself.

The trade is that **a row scrolled out is destroyed**: hover, focus, a nested scroll offset, a
running animation. Keep row state in your model and read it in the builder.

When rows must stay alive, `SetItems` materializes them — at roughly 8 µs each, so fifty thousand is
a frozen window, and on a media app a long enough freeze is audible.

`Background.Slice` fills across frames within a per-frame budget, and supersedes any fill already
running under the same key:

```csharp
public static class ProgressiveRows
{
    /// <summary>Below this, one frame's work is not worth a frame of latency to hide.</summary>
    private const int Threshold = 400;

    public static void Fill(Background background, ListView list, int count,
                            Func<int, Widget> build, bool keepScroll)
    {
        list.SetItems([], keepScroll);

        if (count <= Threshold)
        {
            for (var i = 0; i < count; i++) list.AddItem(build(i));
            return;
        }

        // The list is the key: a query that changes on every keystroke cancels its own
        // half-built predecessor instead of interleaving with it.
        background.Slice(list, count, i => list.AddItem(build(i)));
    }
}
```

Variable-height rows — set `HeightOf` and the list keeps a prefix-sum offset table and
binary-searches the visible window, so cost stays O(viewport) rather than O(count):

```csharp
_list.ItemHeight = 36f;                                   // uniform default
_list.HeightOf   = i => rows[i].IsHeader ? 48f : 36f;     // or per-row
```

Two rules for the list itself:

- **Hoist it.** `private readonly ListView _list = new();` — never construct it inside a `Watch` or a
  `Build` that re-runs, or its scroll position dies with every rebuild.
- **`keepScroll: true`** on `SetItems` when the content is a refinement of what was there (a filter
  narrowing), `false` when it is a different list entirely (a new album).

---

## Master/detail that folds at phone width

`AdaptiveBuilder` rebuilds only when the size class crosses a breakpoint, and cross-fades the swap.
Size classes: `Compact` (< 600), `Medium` (< 840), `Expanded`.

```csharp
public sealed class LibraryShell : ComposedWidget
{
    private readonly Sidebar _sidebar = new();      // hoisted: shared by both layouts,
    private readonly DetailPane _detail = new();    // so selection and scroll survive the fold

    protected override Widget Build(BuildContext ctx) =>
        new AdaptiveBuilder((_, sizeClass) => sizeClass switch
        {
            WindowSizeClass.Compact => _showDetail.Value ? _detail : _sidebar,
            _ => new Row(children:
                 [
                     new SizedBox(width: 260, child: _sidebar),
                     new Expanded(_detail),
                 ]),
        }, transitionDuration: 0.15f);
}
```

Both branches return the *same* `_sidebar` and `_detail` instances. `AdaptiveBuilder` and the
attach/detach machinery handle a shared child being re-parented mid-cross-fade — that is exactly what
the retained model is for. Building fresh instances per branch would reset the fold every resize.

If you only need the raw constraints rather than a named class, use `LayoutBuilder`.

For a real sidebar/content shell with a collapse threshold and a back button, use
`NavigationSplitView` (Material) or `AdwNavigationSplitView` (Adwaita) instead of hand-rolling it.

---

## A form with validation

Draft state in a signal, per-field errors derived with `Computed`, submit gated on validity.

```csharp
public sealed record SignupDraft(string Email = "", string Password = "");

public sealed class SignupForm : ComposedWidget
{
    private readonly Signal<SignupDraft> _draft = new(new SignupDraft());
    private readonly Signal<bool> _submitted = new(false);

    private readonly Computed<string?> _emailError;
    private readonly Computed<string?> _passwordError;
    private readonly Computed<bool> _valid;

    public SignupForm()
    {
        _emailError = Computed.From(() =>
            !_submitted.Value             ? null
          : _draft.Value.Email.Length == 0 ? "Email is required"
          : !_draft.Value.Email.Contains('@') ? "That is not an email address"
          : null);

        _passwordError = Computed.From(() =>
            !_submitted.Value                  ? null
          : _draft.Value.Password.Length < 8   ? "At least 8 characters"
          : null);

        // Reads the same signals; recomputes only when they change.
        _valid = Computed.From(() =>
            _draft.Value.Email.Contains('@') && _draft.Value.Password.Length >= 8);
    }

    protected override Widget Build(BuildContext ctx) => new Column(
        crossAxisAlignment: CrossAxisAlignment.Stretch,
        spacing: Spacing.Md,
        children:
        [
            Field("Email", v => _draft.Value = _draft.Value with { Email = v }, _emailError),
            Field("Password", v => _draft.Value = _draft.Value with { Password = v }, _passwordError,
                  obscure: true),
            new Watch(() => new Button("Create account", _valid.Value ? Submit : null)),
        ]);

    private static Widget Field(string hint, Action<string> onChanged,
                                Computed<string?> error, bool obscure = false) =>
        new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            spacing: Spacing.Xxs,
            children:
            [
                new TextField(
                    decoration: new InputDecoration(hintText: hint),
                    onChanged: onChanged,
                    obscureText: obscure),

                // Scoped to the error alone: typing repaints one caption, not the form.
                new Watch(() => error.Value is { } message
                    ? new Label(message) { Style = Label.LabelStyle.Caption, Color = ThemeData.Dark.Error }
                    : SizedBox.Shrink()),
            ]);

    private void Submit()
    {
        _submitted.Value = true;
        if (_valid.Value) { /* bloc.Add(new AuthEvent.Signup(_draft.Value)); */ }
    }
}
```

Note `_valid.Value ? Submit : null` — a null callback disables the button. That is the whole
`CanExecute` story.

---

## Background work without hitching the frame

`Background` is the worker pool. It marshals results back to the UI thread for you.

```csharp
// Fire and forget, result delivered on the UI thread
background.Run(
    () => ImageDecoder.Decode(path),                  // worker thread
    decoded => _cover.Image = decoded);               // UI thread, next frame

// Async work, cancelled when the owner is disposed
background.RunAsync(async ct =>
{
    var tags = await _scanner.ScanAsync(folder, ct);
    background.Post(() => _bloc.Add(new LibraryEvent.Scanned(tags)));
});

// Latest-wins: each call supersedes the previous
var latest = background.Latest();

// Chunked work with a per-frame budget — see the fifty-thousand-row recipe
background.Slice(key, count, i => DoOne(i), onDone: () => _status.Text = "Done");
```

Give the frame loop a budget for deferred work in your app's `OnUpdate`:

```csharp
protected override void OnUpdate(float dt)
{
    _background.RunFrame(TimeSpan.FromMilliseconds(4));   // a quarter of a 60 Hz frame
}
```

Writing a `Signal` from a worker thread is legal — the loop is woken and the subtree swap lands on
the UI thread in the next `Measure`. You only need `Post` when you are mutating widgets directly.

---

## A dialog that returns a value

Routes are awaitable and complete with the pop result:

```csharp
var confirmed = await ctx.Push(new ConfirmPage($"Delete {item.Name}?"));
if (confirmed is true) _bloc.Add(new LibraryEvent.Delete(item.Id));
```

```csharp
public sealed class ConfirmPage(string question) : ComposedWidget
{
    protected override Widget Build(BuildContext ctx) => new Center(new Column(
        mainAxisSize: MainAxisSize.Min,
        spacing: Spacing.Lg,
        children:
        [
            new Label(question) { Style = Label.LabelStyle.Title },
            new Row(mainAxisSize: MainAxisSize.Min, spacing: Spacing.Sm, children:
            [
                new Button("Cancel", () => ctx.Pop(false)),
                new Button("Delete", () => ctx.Pop(true)),
            ]),
        ]));
}
```

For a true modal over the current screen rather than a pushed route, use `Dialog` — it traps focus,
dims behind, and dismisses on Esc:

```csharp
Dialog.Confirm("Delete track", $"Remove {name} from your library?",
    onConfirm: () => _bloc.Add(new LibraryEvent.Delete(id))).Show();
```

---

## Follow the system theme

`ZigoteApp` syncs `Theme` into the tree each frame, so assigning it is enough:

```csharp
public sealed class MyApp : ZigoteApp
{
    private readonly Signal<bool> _dark = new(true);

    public MyApp()
    {
        Home = new Shell();
        _dark.Changed += dark => Theme = dark ? ThemeData.Dark : ThemeData.Light;
    }
}
```

On Linux, `AdwaitaApp` does this for you against the real desktop settings — system light/dark *and*
the accent colour, live:

```csharp
public sealed class MyApp : AdwaitaApp
{
    public MyApp() : base(title: "My App") { Home = new Shell(); }

    protected override void OnInit()
    {
        SystemStyleChanged += () => _status.Text =
            $"{(SystemPrefersDark ? "dark" : "light")}, accent {SystemAccent}";
    }
}
```

Inside widgets, read colours from the ambient theme rather than a static — `Theme.Of(ctx)` registers
the reading widget as a dependent, so it rebuilds when the theme flips:

```csharp
protected override Widget Build(BuildContext ctx)
{
    var theme = Theme.Of(ctx);
    return new ColoredBox(theme.Surface, new Padding(EdgeInsets.All(Spacing.Md), _content));
}
```

Controls that read the theme during `Measure` rather than `Build` are handled too: a theme change
bumps `BuildContext.Generation`, which invalidates every measure cache.

---

## Animate a state change

**Implicit** — the widget animates when the property changes. Reach for these first:

```csharp
_panel.Child = _expanded ? _details : SizedBox.Shrink();   // AnimatedSize animates the height
new AnimatedOpacity(visible ? 1f : 0f, _badge, duration: 0.2f)
new AnimatedSwitcher(_pages[index], duration: 0.25f)       // cross-fades page swaps
```

**Fluent** — for entrances and one-shots:

```csharp
new Card { Child = content }.Animate().Fade(300.ms).Move(delay: 100.ms)
```

**Explicit** — when you need the driving value. Every `Widget` is an `ITickerProvider`, so
`vsync: this` just works; the ticker is owned by the mount period and disposed with it. Build the
controller in `OnMount`, not the constructor — its ticker's lifetime is the mount, not the instance:

```csharp
public sealed class Pulse : ComposedWidget
{
    private readonly Opacity _fade = new(1.0, new Icon(MaterialIcons.Circle));
    private AnimationController _controller = null!;

    protected override void OnMount()
    {
        _controller = new AnimationController(durationSeconds: 0.8f, vsync: this)
        {
            Curve = Curves.EaseInOut,
        };
        _controller.OnTick += () => { _fade.Value = 0.3f + 0.7f * _controller.Value; MarkNeedsPaint(); };
        _controller.Repeat(reverse: true);
    }

    protected override Widget Build(BuildContext ctx) => _fade;
}
```

`MarkNeedsPaint`, not `MarkNeedsLayout` — an opacity change cannot alter the measured size, and skipping the
relayout keeps a 60 Hz animation off the layout path entirely.

---

## Application keyboard shortcuts

Bind chords to string action ids on the window's `Keymap`, then handle them in one place. Keeping
actions as ids rather than delegates is what makes a "keyboard shortcuts" help sheet and future
rebinding possible.

```csharp
private const string ActionSearch  = "app.search";
private const string ActionDismiss = "app.dismiss";

protected override void OnInit()
{
    var window = App!;

    window.Keymap.Bind(ActionSearch,  KeyChord.Command(KeyCode.F));
    window.Keymap.Bind(ActionDismiss, new KeyChord(KeyCode.Escape));

    window.OnShortcut = action =>
    {
        switch (action)
        {
            case ActionSearch:  _shell.FocusSearch(); return true;
            case ActionDismiss: return _shell.DismissTop();   // false → let the framework handle Esc
            default:            return false;
        }
    };
}
```

`KeyChord.Command(...)` is Cmd on macOS and Ctrl elsewhere. Returning `false` lets the event continue
to the framework's own handling (Esc dismissing the top overlay, Tab traversal).

---

## Test it headlessly

Everything is testable without a window: build a tree, measure, lay out, dispatch synthetic input,
assert. No native engine, no `STAThread`, no device.

```csharp
public class LibraryPageTests
{
    private static T Mount<T>(T widget, float width = 800, float height = 600) where T : Widget
    {
        widget.Measure(Constraints.Loose(width, height));
        widget.Layout(Offset.Zero);
        return widget;
    }

    [Fact]
    public void EmptyQuery_ShowsEveryTrack()
    {
        var bloc = new LibraryBloc(new FakeLibrary(TenTracks));
        bloc.Add(new LibraryEvent.Load());          // synchronous handler: already applied

        Assert.Equal(10, bloc.State.Value.Visible.Length);
    }

    [Fact]
    public void Button_FiresOnPointerUp()
    {
        var clicked = false;
        var button = Mount(new Button("Save", () => clicked = true));

        var centre = new Offset(button.Bounds.X + 10, button.Bounds.Y + 10);
        button.HitTest(centre)!.OnPointerDown(centre);
        button.HitTest(centre)!.OnPointerUp(centre);

        Assert.True(clicked);
    }

    [Fact]
    public void Row_ReusesKeyedInstancesAcrossReorder()
    {
        var a = new SizedBox { Key = new ValueKey<int>(1) };
        var b = new SizedBox { Key = new ValueKey<int>(2) };
        var row = new Row([a, b]);

        row.SetChildren([new SizedBox { Key = new ValueKey<int>(2) },
                         new SizedBox { Key = new ValueKey<int>(1) }]);

        Assert.Same(b, row.Children[0]);
        Assert.Same(a, row.Children[1]);
    }
}
```

You can also assert on the accessibility tree, which is the cheapest way to test "what does this
screen actually say":

```csharp
var tree = SemanticsBuilder.Build(Mount(page), [], new Size(800, 600));
var button = tree.Flatten().First(n => n.Role == SemanticsRole.Button);
Assert.Equal("Save", button.Label);
```

Bloc tests need no widget tree at all, and a `SyncBloc` (or any handler that does not await) has
already applied its state change by the time `Add` returns — no pumping, no polling, no
`await Task.Delay(1)`.

Don't reference `Zigote.Editor` from a test project: it initialises the native engine.

---

## Wire up error reporting

Two seams, and both default to swallowing into a debug log. Set them at startup or you will lose
exceptions from async handlers and workers.

```csharp
protected override void OnInit()
{
    BlocErrors.OnError   = (ex, origin) => Log.Error(ex, "bloc {Origin}", origin);
    Background.OnError   = (ex, origin) => Log.Error(ex, "background {Origin}", origin);
}
```

Or, with `Zigote.Logging` referenced, one call routes both into Serilog:

```csharp
AppLog.CaptureFailures();
```

A throwing bloc handler is reported and the pump carries on — one bad event does not take the screen
down. That is only useful if someone is listening.
