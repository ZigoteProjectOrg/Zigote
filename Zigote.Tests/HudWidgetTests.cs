using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Scripting;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

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
        var media = new MediaQuery(new MediaQueryData(vw, vh), hudRoot);
        var wrapper = new ThemeProvider(ThemeData.Dark, media);
        wrapper.Measure(Constraints.Tight(vw, vh));
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
                0f,
                0f,
                0f,
                0.5f
            )
        );
        Hud.Root = box;
        Assert.Same(box, Hud.Root);

        Hud.Reset(); // play-stop drops the HUD
        Assert.Null(Hud.Root);
    }

    [Fact]
    public void HostedTree_LaysOutAtViewportRect_AndPaints()
    {
        var box = new ColoredBox(
            new Color(
                0.1f,
                0.1f,
                0.1f,
                0.8f
            )
        );
        var root = new Align(Alignment.BottomRight, new SizedBox(200f, 80f, box));

        var p = HostPaint(
            root,
            800f,
            600f,
            new Offset(10f, 20f)
        );

        // The bottom-right box sits at the far corner of the viewport, offset by the viewport origin.
        Assert.Equal(10f + 800f - 200f, box.Bounds.X, 1);
        Assert.Equal(20f + 600f - 80f, box.Bounds.Y, 1);
        Assert.Equal(200f, box.Bounds.Width, 1);
        Assert.Equal(80f, box.Bounds.Height, 1);
        Assert.True(p.Count >= 1); // the ColoredBox emitted its fill rect
    }

    [Fact]
    public void HostedTree_HitTest_RoutesOpaque_AndPassesThroughTransparent()
    {
        var box = new ColoredBox(
            new Color(
                0.1f,
                0.1f,
                0.1f,
                0.8f
            )
        );
        var root = new Align(Alignment.BottomRight, new SizedBox(200f, 80f, box));
        HostPaint(
            root,
            800f,
            600f,
            Offset.Zero
        );

        // Empty top-left region: the Align has no child there → null → the click falls through to the viewport.
        Assert.Null(root.HitTest(new Offset(20f, 20f)));

        // Inside the opaque panel (bottom-right): the ColoredBox absorbs the hit.
        Assert.Same(box, root.HitTest(new Offset(800f - 100f, 600f - 40f)));
    }

    [Fact]
    public void HostedTree_InteractiveWidget_ReceivesTap()
    {
        var fired = 0;
        var pressable = new Pressable {
            Child = new SizedBox(120f, 40f),
            OnPressed = () => fired++,
        };
        var root = new Align(Alignment.TopLeft, new SizedBox(120f, 40f, pressable));
        HostPaint(
            root,
            800f,
            600f,
            Offset.Zero
        );

        var hit = root.HitTest(new Offset(60f, 20f));
        Assert.Same(pressable, hit);

        // The host routes the captured pointer straight to the hit widget, like App.DispatchEvent.
        hit!.OnPointerDown(new Offset(60f, 20f));
        hit.OnPointerUp(new Offset(60f, 20f));
        Assert.Equal(1, fired);
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
            probe,
            640f,
            480f,
            Offset.Zero
        );

        Assert.NotNull(seenTheme); // a theme is in scope (host-provided)
        Assert.Equal(
            640f,
            seenMedia.Width,
            1
        ); // MediaQuery reports the viewport size, not the default
        Assert.Equal(480f, seenMedia.Height, 1);
    }

    /// <summary>
    ///     A trivial <see cref="StatelessWidget" /> that reports the ambient context during Build —
    ///     proves theme/media-aware HUD widgets resolve their inherited data when hosted.
    /// </summary>
    private sealed class ContextProbe(Action<BuildContext> onBuild) : StatelessWidget
    {
        protected override Widget Build(BuildContext context)
        {
            onBuild(context);
            return new SizedBox(0f, 0f);
        }
    }
}