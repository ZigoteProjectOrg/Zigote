using System.Diagnostics;
using System.Text.Json;
using Zigote.Core.Animation;
using Xunit;
using Zigote.Core.Engine;

namespace WebView.Tests;

/// <summary>
///     The Wayland texture backend end to end, WebKit processes and all: frames rendered
///     offscreen, synthetic input reaching the page, focus, selection, scroll. Skipped headless
///     (no Wayland/X display for GTK); needs no engine, via the backend's internal frame seam.
///     <para>
///         Deliberately SYNCHRONOUS: GTK and its main context are single-threaded, and the
///         backend's contract is "everything on the one UI thread". An async test resumes on
///         changing threadpool threads after each await, which deadlocks GLib's context
///         ownership — one xunit thread playing the UI thread is the honest simulation.
///     </para>
/// </summary>
public class OffscreenIntegrationTests
{
    private static bool Headless =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null &&
        Environment.GetEnvironmentVariable("DISPLAY") is null;

    [Fact]
    public void Wayland_RendersFrames_TakesClicks_FocusesSelectsAndScrolls()
    {
        if (Headless) return;

        using var controller = new WebViewController();
        var overlay = controller.EnsureAttached(new NativeParent(NativeParentKind.Wayland, 0, 0));
        Assert.Null(overlay); // texture mode: no overlay view to position
        var backend = Assert.IsType<WebKitOffscreenBackend>(controller.TextureBackend);
        backend.SetSurfaceSize(logicalWidth: 400, logicalHeight: 300, scale: 1f);
        controller.LoadHtml(
            "<body style='background:#204080;margin:0;height:3000px' " +
            "onclick=\"document.title='clicked'\">" +
            "<h1 style='color:#f0a020;margin:0;font-size:30px'>select this offscreen text</h1></body>");

        // Poll until a frame carries the page's background — the first frames are the pre-load
        // white surface, and each repaint bumps the version.
        bool hasBlue = PollUntil(() =>
        {
            if (!backend.TryCopyFrame(out byte[] rgba, out _, out _, out int version) || version == 0)
                return false;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
                if (rgba[i] < 0x60 && rgba[i + 2] > 0x60)
                    return true;
            return false;
        });
        Assert.True(hasBlue, "no frame ever contained the page background");

        // A synthetic click lands in the DOM.
        backend.PointerDown(200, 150);
        backend.PointerUp(200, 150);
        Assert.True(
            PollUntil(() => Eval(controller, "document.title") == "clicked"),
            "click never reached the page");

        // Focus is synthesized, not inherited: the page only believes it is focused after
        // SetPageFocus — which is what turns on carets, focus rings and selection painting.
        backend.SetPageFocus(true);
        Assert.True(
            PollUntil(() => Eval(controller, "document.hasFocus()") == "true"),
            "page never gained focus");

        // A press-drag-release across the headline selects text (motion carries the button mask).
        backend.PointerDown(5, 15);
        backend.PointerMove(120, 15);
        backend.PointerMove(280, 15);
        backend.PointerUp(280, 15);
        string? selected = null;
        Assert.True(
            PollUntil(() =>
            {
                selected = Eval(controller, "window.getSelection().toString()");
                return !string.IsNullOrEmpty(selected);
            }),
            "drag did not select any text");
        Assert.Contains("select", selected);

        // Wheel-down (negative dy in Zigote) scrolls the page DOWN — scrollY grows.
        backend.Scroll(dx: 0, dy: -3, x: 200, y: 150);
        double scrollY = 0;
        Assert.True(
            PollUntil(() =>
                Eval(controller, "window.scrollY") is { } raw &&
                double.TryParse(raw, out scrollY) && scrollY != 0),
            "the page never scrolled");
        Assert.True(scrollY > 0, $"expected the page to scroll down, scrollY={scrollY}");
    }

    /// <summary>
    ///     The bridge an embedded web extension actually lives on: a user script registered
    ///     BEFORE the widget ever mounts, running at document-start, and messages crossing in
    ///     both directions as JSON.
    /// </summary>
    [Fact]
    public void Bridge_InjectsUserScriptsAtDocumentStart_AndCarriesMessagesBothWays()
    {
        if (Headless) return;

        using var controller = new WebViewController();
        var received = new List<string>();
        controller.MessageReceived += received.Add;

        // Registered before attach: the controller replays it into the backend, and it must land
        // before the page's own inline script runs — which is what the page below checks.
        controller.AddUserScript("window.__seeded = 'from the host';");

        var backend = Attach(controller);
        backend.SetSurfaceSize(logicalWidth: 320, logicalHeight: 200, scale: 1f);
        controller.LoadHtml(
            """
            <body><script>
              window.__seenAtStart = window.__seeded;
              window.zigote.onMessage(function (m) { document.title = 'got:' + m.text; });
              window.zigote.postMessage({ kind: 'ready', seeded: window.__seeded });
            </script></body>
            """);

        Assert.True(PollUntil(() => received.Count > 0), "the page's postMessage never reached the host");
        var ping = JsonSerializer.Deserialize<JsonElement>(received[0]);
        Assert.Equal("ready", ping.GetProperty("kind").GetString());
        Assert.Equal("from the host", ping.GetProperty("seeded").GetString());
        Assert.Equal("from the host", Eval(controller, "window.__seenAtStart"));

        // Host → page: an object arrives as an object, not as a string of JSON.
        Await(controller.PostMessageAsync(new { text = "pong" }));
        Assert.True(PollUntil(() => Eval(controller, "document.title") == "got:pong"),
            "the host's message never reached the page");
    }

    /// <summary>The checkout hook: a filter that vetoes a navigation, and the history/progress
    ///     state a browser chrome binds to.</summary>
    [Fact]
    public void NavigationFilter_BlocksVetoedUrls_AndHistoryAndProgressTrackLoads()
    {
        if (Headless) return;

        using var controller = new WebViewController();
        var offered = new List<string>();
        controller.NavigationFilter = url =>
        {
            offered.Add(url);
            return !url.Contains("blocked.invalid");
        };

        var backend = Attach(controller);
        backend.SetSurfaceSize(logicalWidth: 320, logicalHeight: 200, scale: 1f);

        // Real navigations, not LoadHtml: WebKit treats loaded HTML as alternate content for the
        // current URI and never pushes a history entry for it, which would make GoBack meaningless.
        controller.Navigate("data:text/html,<body id='first'>first</body>");
        Assert.True(PollUntil(() => Eval(controller, "document.body.id") == "first"), "the first page never loaded");
        Assert.Equal(1, controller.Progress);
        Assert.False(controller.IsLoading);
        Assert.False(controller.CanGoBack);

        // Vetoed: the filter sees the URL and the page stays exactly where it was.
        controller.Navigate("https://blocked.invalid/checkout");
        Assert.True(PollUntil(() => offered.Any(u => u.Contains("blocked.invalid"))),
            "the filter was never asked about the blocked URL");
        PollUntil(() => false, TimeSpan.FromSeconds(1)); // give a navigation the chance to sneak through
        Assert.Equal("first", Eval(controller, "document.body.id"));

        // Allowed: a second document, and now there is history to walk.
        int historyEvents = 0;
        controller.HistoryChanged += () => historyEvents++;
        controller.Navigate("data:text/html,<body id='second'>second</body>");
        Assert.True(PollUntil(() => Eval(controller, "document.body.id") == "second"), "the second page never loaded");
        Assert.True(PollUntil(() => controller.CanGoBack), "history never recorded the first page");
        Assert.True(historyEvents > 0);

        controller.GoBack();
        Assert.True(PollUntil(() => Eval(controller, "document.body.id") == "first"), "GoBack did not return");
        Assert.True(PollUntil(() => controller.CanGoForward), "there should be somewhere forward to go");
    }

    /// <summary>A load that cannot succeed reports itself, rather than leaving a blank rectangle
    ///     and no explanation.</summary>
    [Fact]
    public void LoadFailed_ReportsUnreachableHosts()
    {
        if (Headless) return;

        using var controller = new WebViewController();
        WebViewError? failure = null;
        controller.LoadFailed += e => failure = e;

        var backend = Attach(controller);
        backend.SetSurfaceSize(logicalWidth: 320, logicalHeight: 200, scale: 1f);
        controller.Navigate("https://this-host-does-not-exist.invalid/");

        Assert.True(PollUntil(() => failure is not null), "an unresolvable host reported no failure");
        Assert.Contains("invalid", failure!.Value.Url);
        Assert.NotEmpty(failure.Value.Message);
        Assert.False(controller.IsLoading);
    }

    /// <summary><see cref="WebViewSettings" /> reaching the real engine, and the "log out for
    ///     real" button completing against WebKit's data manager.</summary>
    [Fact]
    public void Settings_ApplyToTheRealEngine_AndBrowsingDataClears()
    {
        if (Headless) return;

        using var controller = new WebViewController(new WebViewSettings { UserAgent = "Zigote-Test-Agent/1.0" });
        var backend = Attach(controller);
        backend.SetSurfaceSize(logicalWidth: 320, logicalHeight: 200, scale: 1f);
        controller.LoadHtml("<body>agent</body>");

        Assert.True(PollUntil(() => Eval(controller, "navigator.userAgent") == "Zigote-Test-Agent/1.0"),
            "the custom user agent never reached the page");

        Eval(controller, "localStorage.setItem('k', 'v'), 1");
        Await(controller.ClearBrowsingDataAsync());
    }

    /// <summary>Scripts off means scripts off — including the bridge, which is the honest
    ///     consequence and worth pinning so nobody expects a half-working webview.</summary>
    [Fact]
    public void JavaScriptDisabled_LeavesThePageInert()
    {
        if (Headless) return;

        using var controller = new WebViewController(new WebViewSettings { JavaScriptEnabled = false });
        var backend = Attach(controller);
        backend.SetSurfaceSize(logicalWidth: 320, logicalHeight: 200, scale: 1f);
        controller.LoadHtml("<body><script>document.title = 'ran'</script>inert</body>");

        // Let the load settle, then confirm the page's own script never ran.
        Assert.True(PollUntil(() => !controller.IsLoading && controller.Progress == 1), "the page never finished loading");
        Assert.NotEqual("ran", controller.Title);
    }

    /// <summary>
    ///     The frame path is damage-driven: a page that is not repainting must produce no frames
    ///     at all. This is the difference between an idle webview costing nothing and costing a
    ///     full-surface convert plus upload sixty times a second — the whole reason the offscreen
    ///     backend can share a 60 fps budget with the rest of the app.
    /// </summary>
    [Fact]
    public void IdlePage_ProducesNoFrames_AndAChangedPageDoes()
    {
        if (Headless) return;

        using var controller = new WebViewController();
        var backend = Attach(controller);
        backend.SetSurfaceSize(logicalWidth: 320, logicalHeight: 200, scale: 1f);
        controller.LoadHtml("<body style='background:#204080;margin:0'>static</body>");

        Assert.True(PollUntil(() => backend.TryCopyFrame(out _, out _, out _, out int v) && v > 0),
            "the page never rendered a frame");

        // Let the load fully settle, then watch a second of an untouched page.
        PollUntil(() => false, TimeSpan.FromSeconds(1));
        backend.TryCopyFrame(out _, out _, out _, out int settled);
        PollUntil(() => false, TimeSpan.FromSeconds(1));
        backend.TryCopyFrame(out _, out _, out _, out int idle);
        Assert.Equal(settled, idle);

        // A real repaint still gets through.
        Eval(controller, "document.body.style.background = '#ff0000', 1");
        Assert.True(
            PollUntil(() => backend.TryCopyFrame(out _, out _, out _, out int v) && v > idle),
            "a repainted page produced no new frame");
    }

    /// <summary>
    ///     Damage-driven partial updates are only safe if they are indistinguishable from
    ///     converting the whole surface. Animate a page for a while — many small damage rects —
    ///     then reconvert everything and demand the two agree byte for byte. A row-range or
    ///     stride mistake in the fast path shows up here and nowhere else.
    /// </summary>
    [Fact]
    public void PartialUpdates_MatchAFullReconversion_ByteForByte()
    {
        if (Headless) return;

        using var controller = new WebViewController();
        var backend = Attach(controller);
        // A width whose row is not a multiple of the vector width, to exercise the scalar tail.
        backend.SetSurfaceSize(logicalWidth: 331, logicalHeight: 207, scale: 1.5f);
        controller.LoadHtml(
            """
            <body style='background:#101014;margin:0;color:#eee;font:14px sans-serif'>
              <h1 id='h'>animating</h1><p>text that moves around while the frames tick by</p>
              <script>
                var n = 0;
                setInterval(function () {
                  n++;
                  document.getElementById('h').style.marginLeft = (n % 40) + 'px';
                  document.getElementById('h').textContent = 'frame ' + n;
                }, 30);
              </script>
            </body>
            """);

        Assert.True(PollUntil(() => backend.TryCopyFrame(out _, out _, out _, out int v) && v > 3),
            "the animation never produced frames");
        PollUntil(() => false, TimeSpan.FromSeconds(2)); // let a few hundred damage rects go by

        // Freeze the page, settle, then snapshot what the incremental path built...
        Eval(controller, "for (var i = 1; i < 9999; i++) clearInterval(i); 1");
        PollUntil(() => false, TimeSpan.FromSeconds(1));
        Assert.True(backend.TryCopyFrame(out byte[] incremental, out int w, out int h, out int before));

        // ...and compare it against converting every row from scratch.
        backend.ForceFullRedraw();
        Assert.True(PollUntil(() => backend.TryCopyFrame(out _, out _, out _, out int v) && v > before),
            "the forced full redraw produced no frame");
        Assert.True(backend.TryCopyFrame(out byte[] full, out int fw, out int fh, out _));

        Assert.Equal((w, h), (fw, fh));
        int firstDifference = incremental.AsSpan().CommonPrefixLength(full);
        Assert.True(firstDifference == full.Length,
            $"partial updates diverged from a full conversion at byte {firstDifference} " +
            $"(pixel {firstDifference / 4}, row {firstDifference / 4 / w}) of {full.Length}");
    }

    /// <summary>
    ///     The page advances on the ENGINE's frame clock and on nothing else. A backend that keeps
    ///     a clock of its own (a GLib timeout, a threadpool timer) delivers frames at a phase
    ///     unrelated to the frame being drawn, which is what scrolling stutter is made of — so
    ///     "no tick, no frame" is the property to pin, not an implementation detail.
    /// </summary>
    [Fact]
    public void FramesAdvanceOnlyOnTheFrameClock()
    {
        if (Headless) return;

        using var controller = new WebViewController();
        var backend = Attach(controller);
        backend.SetSurfaceSize(logicalWidth: 300, logicalHeight: 200, scale: 1f);
        controller.LoadHtml("<body style='background:#204080;margin:0'>clocked</body>");
        Assert.True(PollUntil(() => backend.TryCopyFrame(out _, out _, out _, out int v) && v > 0),
            "the page never rendered");

        // Queue a repaint and then stop ticking entirely. Snapshot AFTER the eval, since
        // evaluating pumps the loop itself.
        Eval(controller, "document.body.style.background = '#20a030', 1");
        backend.TryCopyFrame(out _, out _, out _, out int ticked);

        Thread.Sleep(500); // NOT ticking — no engine frames, so no page frames either
        backend.TryCopyFrame(out _, out _, out _, out int unticked);
        Assert.Equal(ticked, unticked);

        // Resume the clock and the page comes with it.
        Eval(controller, "document.body.style.background = '#3050c0', 1");
        Assert.True(PollUntil(() => backend.TryCopyFrame(out _, out _, out _, out int v) && v > ticked),
            "the frame clock resumed but the page did not");
    }

    /// <summary>
    ///     A background tab: the page keeps running — timers, sockets, JS — but nobody can see it,
    ///     so it must stop paying for the frame conversion and the texture upload. Without this a
    ///     browser's tenth tab costs exactly as much as its visible one.
    /// </summary>
    [Fact]
    public void UndisplayedPage_KeepsRunningButStopsProducingFrames()
    {
        if (Headless) return;

        using var controller = new WebViewController();
        var backend = Attach(controller);
        backend.SetSurfaceSize(logicalWidth: 300, logicalHeight: 200, scale: 1f);
        controller.LoadHtml(
            "<body style='background:#204080;margin:0'><script>window.n = 0;" +
            "setInterval(function () { window.n++; document.body.style.background =" +
            "(window.n % 2) ? '#a03020' : '#204080'; }, 30);</script></body>");
        Assert.True(PollUntil(() => backend.TryCopyFrame(out _, out _, out _, out int v) && v > 2),
            "the page never animated");

        backend.SetDisplayed(false);
        PollUntil(() => false, TimeSpan.FromSeconds(1)); // let any in-flight frame settle
        backend.TryCopyFrame(out _, out _, out _, out int hidden);
        PollUntil(() => false, TimeSpan.FromSeconds(1));
        backend.TryCopyFrame(out _, out _, out _, out int stillHidden);
        Assert.Equal(hidden, stillHidden);

        // The page never stopped: its interval kept firing the whole time it was hidden.
        Assert.True(int.TryParse(Eval(controller, "window.n"), out int ticks) && ticks > 0,
            "the hidden page stopped running its script");

        backend.SetDisplayed(true);
        Assert.True(PollUntil(() => backend.TryCopyFrame(out _, out _, out _, out int v) && v > hidden),
            "a re-displayed page produced no frames");
    }

    /// <summary>Bring up the texture backend the way the widget does, and assert this platform
    ///     really is in texture mode.</summary>
    private static WebKitOffscreenBackend Attach(WebViewController controller)
    {
        var overlay = controller.EnsureAttached(new NativeParent(NativeParentKind.Wayland, 0, 0));
        Assert.Null(overlay); // texture mode: no overlay view to position
        return Assert.IsType<WebKitOffscreenBackend>(controller.TextureBackend);
    }

    /// <summary>Await on the one thread that owns the GLib context — a real await would resume
    ///     on a threadpool thread and deadlock GTK.</summary>
    private static void Await(Task task)
    {
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            Frame();
            Thread.Sleep(5);
        }

        Assert.True(task.IsCompleted, "the task never completed");
        task.GetAwaiter().GetResult(); // rethrow anything it faulted with
    }

    /// <summary>
    ///     One engine frame, without an engine: the backends hang their per-frame work off
    ///     <see cref="Ticker" />s exactly so the page advances on the frame clock, and advancing
    ///     them by hand is what makes these tests the same sequence a real app runs.
    /// </summary>
    private static void Frame() => Ticker.AdvanceAll(1f / 60f);

    /// <summary>Pump-and-check on the one thread until the condition holds (20s ceiling).</summary>
    private static bool PollUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < (timeout ?? TimeSpan.FromSeconds(20)))
        {
            Frame();
            if (condition()) return true;
            Thread.Sleep(25);
        }

        return false;
    }

    /// <summary>Evaluate JS, pumping the loop this thread owns until the answer lands.</summary>
    private static string? Eval(WebViewController controller, string script)
    {
        var task = controller.EvaluateJavaScriptAsync(script);
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            Frame();
            Thread.Sleep(5);
        }

        return task.IsCompleted ? task.Result : null;
    }
}
