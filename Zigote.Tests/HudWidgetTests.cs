using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Scripting;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Guards the widget-based game HUD: a play-mode script can publish a full <c>Zigote.UI</c> widget
///     tree
///     via <see cref="Hud.Root" />, and the host (the editor's ViewportPanel) measures/lays-out/paints
///     it at
///     the viewport rect and routes input to it. These headless tests reproduce that hosting pipeline
///     —
///     wrap the game tree in a <see cref="ThemeProvider" /> + viewport <see cref="MediaQuery" />
///     exactly like
///     the host, then drive Measure/Layout/Paint/HitTest — without a native window.
/// </summary>
public class HudWidgetTests
{
    /// <summary>
    ///     Mirror <c>ViewportPanel.DrawGameHud</c>: theme + viewport MediaQuery wrapper, tight measure,
    ///     layout at the viewport origin, paint.
    /// </summary>
    private static PaintList HostPaint(Widget hudRoot, float vw, float vh, Offset origin)
    {
        var media = new MediaQuery(data: new MediaQueryData(width: vw, height: vh), child: hudRoot);
        var wrapper = new ThemeProvider(data: ThemeData.Dark, child: media);
        wrapper.Measure(Constraints.Tight(width: vw, height: vh));
        wrapper.Layout(origin);
        var p = new PaintList();
        wrapper.Paint(p);
        return p;
    }

    [Fact]
    public void Root_StartsNull_SetGet_AndResetClears()
    {
        Hud.Reset();
        Assert.Null(Hud.Root);

        var box = new ColoredBox(
            new Color(
                r: 0f,
                g: 0f,
                b: 0f,
                a: 0.5f
            )
        );
        Hud.Root = box;
        Assert.Same(expected: box, actual: Hud.Root);

        Hud.Reset(); // play-stop drops the HUD
        Assert.Null(Hud.Root);
    }

    [Fact]
    public void HostedTree_LaysOutAtViewportRect_AndPaints()
    {
        var box = new ColoredBox(
            new Color(
                r: 0.1f,
                g: 0.1f,
                b: 0.1f,
                a: 0.8f
            )
        );
        var root = new Align(
            alignment: Alignment.BottomRight,
            child: new SizedBox(width: 200f, height: 80f, child: box)
        );

        var p = HostPaint(
            hudRoot: root,
            vw: 800f,
            vh: 600f,
            origin: new Offset(x: 10f, y: 20f)
        );

        // The bottom-right box sits at the far corner of the viewport, offset by the viewport origin.
        Assert.Equal(expected: 10f + 800f - 200f, actual: box.Bounds.X, precision: 1);
        Assert.Equal(expected: 20f + 600f - 80f, actual: box.Bounds.Y, precision: 1);
        Assert.Equal(expected: 200f, actual: box.Bounds.Width, precision: 1);
        Assert.Equal(expected: 80f, actual: box.Bounds.Height, precision: 1);
        Assert.True(p.Count >= 1); // the ColoredBox emitted its fill rect
    }

    [Fact]
    public void HostedTree_HitTest_RoutesOpaque_AndPassesThroughTransparent()
    {
        var box = new ColoredBox(
            new Color(
                r: 0.1f,
                g: 0.1f,
                b: 0.1f,
                a: 0.8f
            )
        );
        var root = new Align(
            alignment: Alignment.BottomRight,
            child: new SizedBox(width: 200f, height: 80f, child: box)
        );
        HostPaint(
            hudRoot: root,
            vw: 800f,
            vh: 600f,
            origin: Offset.Zero
        );

        // Empty top-left region: the Align has no child there → null → the click falls through to the viewport.
        Assert.Null(root.HitTest(new Offset(x: 20f, y: 20f)));

        // Inside the opaque panel (bottom-right): the ColoredBox absorbs the hit.
        Assert.Same(expected: box, actual: root.HitTest(new Offset(x: 800f - 100f, y: 600f - 40f)));
    }

    [Fact]
    public void HostedTree_InteractiveWidget_ReceivesTap()
    {
        int fired = 0;
        var pressable = new Pressable {
            Child = new SizedBox(width: 120f, height: 40f),
            OnPressed = () => fired++,
        };
        var root = new Align(
            alignment: Alignment.TopLeft,
            child: new SizedBox(width: 120f, height: 40f, child: pressable)
        );
        HostPaint(
            hudRoot: root,
            vw: 800f,
            vh: 600f,
            origin: Offset.Zero
        );

        var hit = root.HitTest(new Offset(x: 60f, y: 20f));
        Assert.Same(expected: pressable, actual: hit);

        // The host routes the captured pointer straight to the hit widget, like App.DispatchEvent.
        hit!.OnPointerDown(new Offset(x: 60f, y: 20f));
        hit.OnPointerUp(new Offset(x: 60f, y: 20f));
        Assert.Equal(expected: 1, actual: fired);
    }

    [Fact]
    public void HostedTree_ThemeAndMediaQuery_ResolveInsideHud()
    {
        ThemeData? seenTheme = null;
        MediaQueryData seenMedia = default;
        var probe = new ContextProbe(ctx =>
            {
                seenTheme = Theme.Of(ctx);
                seenMedia = MediaQuery.Of(ctx);
            }
        );

        HostPaint(
            hudRoot: probe,
            vw: 640f,
            vh: 480f,
            origin: Offset.Zero
        );

        Assert.NotNull(seenTheme); // a theme is in scope (host-provided)
        Assert.Equal(
            expected: 640f,
            actual: seenMedia.Width,
            precision: 1
        ); // MediaQuery reports the viewport size, not the default
        Assert.Equal(expected: 480f, actual: seenMedia.Height, precision: 1);
    }

    /// <summary>
    ///     A trivial <see cref="ComposedWidget" /> that reports the ambient context during Build —
    ///     proves theme/media-aware HUD widgets resolve their inherited data when hosted.
    /// </summary>
    private sealed class ContextProbe(Action<BuildContext> onBuild) : ComposedWidget
    {
        protected override Widget Build(BuildContext context)
        {
            onBuild(context);
            return new SizedBox(width: 0f, height: 0f);
        }
    }
}
