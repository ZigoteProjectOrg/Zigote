using System.Diagnostics;
using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.Core.Events;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Focus;
using MediaQueryData = Zigote.UI.Widgets.MediaQueryData;

namespace Zigote.UI.Host;

public partial class App
{
    // ── Secondary-window frame plumbing ───────────────────────────────────────

    private void RouteEventsToSecondaryWindows()
    {
        if (_secondaryWindows.Count == 0) return;
        for (int i = 0; i < _secondaryWindows.Count; i++) _secondaryWindows[i]._events.Clear();

        uint mainId = Engine.MainWindowId;
        int write = 0;
        for (int read = 0; read < _events.Count; read++)
        {
            var evt = _events[read];
            App? target = null;
            if (evt.WindowId != 0 && evt.WindowId != mainId)
            {
                for (int i = 0; i < _secondaryWindows.Count; i++)
                {
                    if (_secondaryWindows[i].WindowId == evt.WindowId)
                    {
                        target = _secondaryWindows[i];
                        break;
                    }
                }
            }

            if (target is not null) target._events.Add(evt);
            else _events[write++] = evt; // main-window or global event — keep in the main batch
        }

        _events.RemoveRange(index: write, count: _events.Count - write);
    }

    private bool AnySecondaryWindowWantsFrame()
    {
        for (int i = 0; i < _secondaryWindows.Count; i++)
        {
            var w = _secondaryWindows[i];
            if (w._repaint.AnyDirty || w._needsLayout || w._pendingRelayout) return true;
            if (w.WantsContinuousFrame()) return true;
            if (w.FocusedWidget is ITextInputClient { WantsCaretBlink: true }) return true;
        }

        return false;
    }

    private void PumpSecondaryWindows()
    {
        // Reverse: a window may Close() itself out of the list mid-frame (✕ handling).
        for (int i = _secondaryWindows.Count - 1; i >= 0; i--)
            _secondaryWindows[i].SecondaryFrame(DeltaTime);
    }

    /// <summary>
    ///     Per-frame processing for a secondary window, driven from the main app's
    ///     <see cref="Frame" />: dispatch this window's routed events, layout, paint, and present
    ///     through its <see cref="NativeWindow" />. Global work (SDL poll, ticker advance, audio) is
    ///     the main frame's job and must not repeat here.
    /// </summary>
    private void SecondaryFrame(float dt)
    {
        if (NativeWindow is not { IsAlive: true } || Root is null)
        {
            _events.Clear();
            return;
        }

        DeltaTime = dt;

        // Ambient App.Active follows the window being processed so widget code that resolves the
        // app statically (focus requests, Time reads, popup hosts) lands on this window.
        var prevActive = Active;
        Active = this;
        try
        {
            if (WantsContinuousFrame()) _repaint.MarkOverlay();
            FrameTick?.Invoke(DeltaTime);

            AdvanceTooltip(DeltaTime);

            if (_snackbars.Count > 0) _repaint.MarkOverlay();
            for (int i = _snackbars.Count - 1; i >= 0; i--)
            {
                var s = _snackbars[i];
                s.Tick(DeltaTime);
                if (s.IsDone)
                {
                    PopOverlay(s);
                    _snackbars.RemoveAt(i);
                }
            }

            if (_tooltipTimer > 0 && _tooltipOverlay is null) _repaint.MarkOverlay();
            if (FocusedWidget is ITextInputClient { WantsCaretBlink: true })
                MarkPaintFor(FocusedWidget);

            if (_needsLayout || _pendingRelayout)
                LayoutTree();

            _pendingRelayout = false;
            bool discrete = false;
            foreach (var evt in _events)
            {
                DispatchEvent(evt);
                if (evt is not MouseMoveEvent) discrete = true;
            }

            _events.Clear();

            // The ✕ handler may have closed the window mid-dispatch — its tree is gone.
            if (NativeWindow is not { IsAlive: true } || Root is null) return;

            if (_needsLayout || _pendingRelayout || discrete)
            {
                LayoutTree();
                _repaint.MarkAll();
            }

            ProcessPendingAutoFocus();

            if (!_repaint.AnyDirty && !ContinuousUpdate) return;
            if (ContinuousUpdate) _repaint.MarkAll();

            SecondaryPaintAndPresent();
        }
        finally
        {
            Active = prevActive;
        }
    }

    /// <summary>Paint the dirty layer(s) and present through this secondary window's own native target.</summary>
    private void SecondaryPaintAndPresent()
    {
        if (NativeWindow is null || Root is null) return;

        // Same rounded-corner clip the main window gets — a devtools or Settings window sitting
        // next to it with square corners is the tell that it is not a real GNOME window.
        bool csdRounded = CsdRounded;
        var windowRect = WindowRect;

        InTreeWalk = true;
        try
        {
            if (_repaint.RootDirty)
            {
                _paint.Clear();
                if (csdRounded) _paint.AddClipStart(bounds: windowRect, radius: CsdCornerRadius);
                PaintChromeBackdrop();
                Root.Paint(_paint);
                PaintCsdOutline(csdRounded);
                if (csdRounded) _paint.AddClipEnd();
                _repaint.RootPainted();
            }

            if (_repaint.OverlayDirty)
            {
                _overlayPaint.Clear();
                if (csdRounded)
                    _overlayPaint.AddClipStart(bounds: windowRect, radius: CsdCornerRadius);
                foreach (var ov in _overlays) ov.Paint(_overlayPaint);
                if (csdRounded) _overlayPaint.AddClipEnd();
                _repaint.OverlayPainted();
            }
        }
        finally
        {
            InTreeWalk = false;
        }

        DrainDeferredOverlayOps();

        NativeWindow.SubmitPaint(_paint);
        NativeWindow.SubmitOverlay(_overlayPaint);
        NativeWindow.Render();
        _repaint.ResetDamage();
    }

    private void ProcessPendingAutoFocus()
    {
        if (_pendingAutoFocus.Count == 0) return;
        foreach (var ov in _pendingAutoFocus)
        {
            if (ov is INoAutoFocus || !_overlays.Contains(ov) ||
                !FocusTraversal.HasFocusable(ov)) continue;
            _focusRestore.Add((ov, FocusedWidget));
            FocusFirstIn(ov);
        }

        _pendingAutoFocus.Clear();
    }

    /// <summary>
    ///     Software frame-rate limiter: sleeps (then briefly spins for accuracy) so the continuous
    ///     render loop holds <see cref="FrameIntervalTicks" /> — the monitor's refresh, or
    ///     <see cref="FrameRateLimit" /> when that is slower. Resyncs instead of bursting when a heavy
    ///     frame falls behind, so the cap never overshoots afterwards.
    ///     <para>
    ///         With vsync on the present already blocks at the panel's rate, so this mostly matters for
    ///         an explicit cap, for vsync-off, and for keeping the CPU-side loop from spinning ahead of
    ///         a swapchain synced to a different monitor than the one the window is on.
    ///     </para>
    /// </summary>
    private void PaceFrame()
    {
        if (FrameRateLimit < 0) return; // unpaced — see FrameRateLimit
        long interval = FrameIntervalTicks;
        long now = _clock.ElapsedTicks;
        if (_paceAnchorTicks == 0) _paceAnchorTicks = now;
        _paceAnchorTicks += interval;
        if (_paceAnchorTicks < now)
        {
            _paceAnchorTicks = now; // fell behind — resync rather than catch up in a burst
            return;
        }

        long oneMs = Stopwatch.Frequency / 1000;
        while (true)
        {
            long remaining = _paceAnchorTicks - _clock.ElapsedTicks;
            if (remaining <= 0) break;
            if (remaining > 2 * oneMs) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    /// <summary>
    ///     Apply a pending hot-reload: re-run every <c>Build()</c> in the live tree (root + overlays) so
    ///     edited widget code takes effect, then force a relayout. Widget instances and their
    ///     their fields are preserved — only <c>Build</c> re-runs. UI thread only.
    /// </summary>
    private void ApplyHotReload()
    {
        if (!HotReload.TryTakePending(out var types)) return;

        if (Root is not null) HotReload.MarkSubtreeForRebuild(Root);
        foreach (var ov in _overlays) HotReload.MarkSubtreeForRebuild(ov);
        foreach (var win in _secondaryWindows)
        {
            if (win.Root is not null) HotReload.MarkSubtreeForRebuild(win.Root);
            foreach (var ov in win._overlays) HotReload.MarkSubtreeForRebuild(ov);
            win.RequestLayout();
        }

        // Bump the build generation so even Measure-cached subtrees that did not depend on an inherited
        // widget re-measure against the freshly built children.
        BuildContext.Current.BumpGeneration();
        RequestLayout();
        HotReload.RaiseReloaded(types);
    }

    private void LayoutTree()
    {
        if (Root is null) return;

        using var _ = Profiler.Scope("UI.Layout");
        BuildContext.Current.Reset();
        // Refresh ambient context (MediaQuery) before measuring — every widget can read it.
        // Padding carries the real device safe area (notch / home indicator; zero on desktop)
        // so the SafeArea widget insets by actual hardware values. Queried on the main window
        // only — secondary windows are desktop-only and have no obstructions.
        if (!_safeAreaValid)
        {
            if (ParentApp is null)
            {
                (float l, float t, float r, float b) = Engine.GetSafeArea();
                _safeArea = new EdgeInsets(
                    left: l,
                    top: t,
                    right: r,
                    bottom: b
                );
            }

            _safeAreaValid = true;
        }

        BuildContext.Current.MediaQuery = new MediaQueryData(
            width: LayoutWidth,
            height: LayoutHeight,
            devicePixelRatio: HostScale,
            padding: _safeArea
        );

        var c = Constraints.Tight(width: LayoutWidth, height: LayoutHeight);

        // The app theme scope wraps the WHOLE pass — root and overlays — so ThemeProvider.Of
        // resolves the live App.Theme everywhere (see _appThemeScope). The scope instance is reused
        // (updated silently) to keep the layout pass allocation-free.
        _appThemeScope.SetDataSilently(Theme);
        var ctx = BuildContext.Current;
        ctx.Push(_appThemeScope);
        InTreeWalk = true;
        try
        {
            Root.Measure(c);
            Root.Layout(Offset.Zero);

            foreach (var ov in _overlays)
            {
                ov.Measure(c);
                ov.Layout(Offset.Zero);
            }
        }
        finally
        {
            InTreeWalk = false;
            ctx.Pop(_appThemeScope);
        }

        _needsLayout = false;
        _pendingRelayout = false;

        DrainDeferredOverlayOps();
    }
}
