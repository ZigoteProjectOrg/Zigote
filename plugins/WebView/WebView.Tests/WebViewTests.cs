using Xunit;
using Zigote.Core.Engine;

namespace WebView.Tests;

/// <summary>Controller contract without a display: pending calls, platform refusals, page-state
///     bookkeeping, the JSON bridge helpers, disposal. Everything that needs a real browser is in
///     <see cref="OffscreenIntegrationTests" />.</summary>
public class WebViewTests
{
    [Fact]
    public async Task BeforeAttach_CallsAreSafeAndJsAnswersNull()
    {
        using var controller = new WebViewController();
        controller.Navigate("https://example.org");
        controller.GoBack();
        controller.Reload();
        controller.AddUserScript("window.x = 1;");
        await controller.PostMessageAsync("hello"); // no view: evaluates to nothing, does not throw
        await controller.ClearBrowsingDataAsync();
        Assert.Null(await controller.EvaluateJavaScriptAsync("1+1"));
        Assert.Null(controller.Url);
        Assert.False(controller.IsLoading);
    }

    [Fact]
    public void Attach_RefusesHeadless()
    {
        using var controller = new WebViewController();
        Assert.Null(controller.EnsureAttached(new NativeParent(NativeParentKind.None, 0, 0)));
        Assert.NotNull(controller.LastError);
    }

    [Fact]
    public void Disposed_ThrowsOnAttachAndUserScripts()
    {
        var controller = new WebViewController();
        controller.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => controller.EnsureAttached(new NativeParent(NativeParentKind.X11, 1, 1)));
        Assert.Throws<ObjectDisposedException>(() => controller.AddUserScript("1"));
    }

    [Fact]
    public void EnsureEmbeddableVideoDriver_RespectsExplicitChoice()
    {
        string? prior = Environment.GetEnvironmentVariable("SDL_VIDEO_DRIVER");
        try
        {
            Environment.SetEnvironmentVariable("SDL_VIDEO_DRIVER", "wayland");
            WebViewController.EnsureEmbeddableVideoDriver();
            Assert.Equal("wayland", Environment.GetEnvironmentVariable("SDL_VIDEO_DRIVER"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SDL_VIDEO_DRIVER", prior);
        }
    }

    [Fact]
    public void Settings_DefaultToWhatAnEmbeddedExtensionNeeds()
    {
        var settings = new WebViewController().Settings;
        Assert.True(settings.JavaScriptEnabled);
        Assert.Null(settings.UserAgent);
        Assert.False(settings.DevToolsEnabled);
        Assert.False(settings.AllowAutoplay);
    }

    [Fact]
    public void NavigationFilter_AllowsByDefault_BlocksOnFalse_AndBlocksOnThrow()
    {
        using var controller = new WebViewController();
        Assert.True(controller.AllowNavigation("https://example.org"));

        controller.NavigationFilter = url => url.StartsWith("https://");
        Assert.True(controller.AllowNavigation("https://example.org"));
        Assert.False(controller.AllowNavigation("http://example.org"));

        // A filter is a security hook: a throwing one must not become "allow everything".
        controller.NavigationFilter = _ => throw new InvalidOperationException("boom");
        Assert.False(controller.AllowNavigation("https://example.org"));
    }

    [Fact]
    public void Progress_IsClamped_DeduplicatedAndCompletedByLoadEnd()
    {
        using var controller = new WebViewController();
        var seen = new List<double>();
        controller.ProgressChanged += seen.Add;

        controller.OnProgressChanged(0.5);
        controller.OnProgressChanged(0.5); // same value: no second event
        controller.OnProgressChanged(9); // clamped
        Assert.Equal([0.5, 1], seen);
        Assert.Equal(1, controller.Progress);

        controller.OnProgressChanged(0.2);
        controller.OnLoadingChanged(false); // a finished load always ends at 1
        Assert.Equal(1, controller.Progress);
        Assert.False(controller.IsLoading);
    }

    [Fact]
    public void History_FiresOnlyWhenItActuallyMoves()
    {
        using var controller = new WebViewController();
        int events = 0;
        controller.HistoryChanged += () => events++;

        controller.OnHistoryChanged(false, false); // unchanged from the initial state
        Assert.Equal(0, events);

        controller.OnHistoryChanged(true, false);
        controller.OnHistoryChanged(true, false);
        Assert.Equal(1, events);
        Assert.True(controller.CanGoBack);
        Assert.False(controller.CanGoForward);
    }

    [Fact]
    public void LoadFailed_CarriesUrlAndReason()
    {
        using var controller = new WebViewController();
        WebViewError? failure = null;
        controller.LoadFailed += e => failure = e;
        controller.OnLoadFailed("https://nope.invalid", "Could not resolve host");
        Assert.Equal("https://nope.invalid", failure?.Url);
        Assert.Contains("resolve", failure?.Message);
        Assert.Contains("nope.invalid", failure.ToString());
    }

    private sealed record Ping(string Kind, int Value);

    [Fact]
    public void ReceiveJson_ParsesDropsBadJsonAndUnsubscribes()
    {
        using var controller = new WebViewController();
        var received = new List<Ping>();
        var subscription = controller.ReceiveJson<Ping>(received.Add);

        controller.OnMessageReceived("""{"kind":"tap","value":7}""");
        Assert.Equal(new Ping("tap", 7), Assert.Single(received));

        // Garbage from a page is logged and dropped, never thrown into the UI thread.
        controller.OnMessageReceived("not json at all");
        Assert.Single(received);

        subscription.Dispose();
        controller.OnMessageReceived("""{"kind":"tap","value":8}""");
        Assert.Single(received);
    }

    [Fact]
    public void MessageReceived_PassesTheRawPayloadThrough()
    {
        using var controller = new WebViewController();
        string? raw = null;
        controller.MessageReceived += m => raw = m;
        controller.OnMessageReceived("plain text");
        Assert.Equal("plain text", raw);
    }

    [Fact]
    public void BridgeShim_WiresTheHostSendAndIsIdempotent()
    {
        string shim = WebViewBridge.Shim("window.hostSend");
        Assert.Contains("window.hostSend", shim);
        Assert.Contains("window.zigote", shim);
        // Re-injection (an iframe, a second AddUserScript) must not wipe registered listeners.
        Assert.Contains("if (window.zigote && window.zigote.__zigote) return;", shim);
    }
}

/// <summary>
///     The BGRA→RGBA conversion the Linux texture backend runs on every damaged row. Vectorized
///     four pixels at a time with a scalar tail, so the widths that matter are the ones either
///     side of that boundary — a tail bug would corrupt only the right edge of the page.
/// </summary>
public class SwizzleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(16)]
    public unsafe void Swizzle_ConvertsEveryPixelAndForcesOpaqueAlpha(int width)
    {
        const int height = 3;
        int stride = (width * 4) + 8; // Cairo pads rows; the padding must never be read as pixels
        var source = new byte[stride * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * stride) + (x * 4);
            source[i] = (byte)(x + 1); // B
            source[i + 1] = (byte)(x + 100); // G
            source[i + 2] = (byte)(x + 200); // R
            source[i + 3] = 0; // A — premultiplied garbage, must come out opaque
        }

        var destination = new byte[width * height * 4];
        fixed (byte* src = source)
        {
            WebKitOffscreenBackend.Swizzle(src: src, stride: stride, dst: destination,
                width: width, y0: 0, y1: height);
        }

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = ((y * width) + x) * 4;
            Assert.Equal((byte)(x + 200), destination[i]); // R
            Assert.Equal((byte)(x + 100), destination[i + 1]); // G
            Assert.Equal((byte)(x + 1), destination[i + 2]); // B
            Assert.Equal(0xFF, destination[i + 3]);
        }
    }

    [Fact]
    public unsafe void Swizzle_TouchesOnlyTheRowsItWasGiven()
    {
        const int width = 8, height = 4, stride = width * 4;
        var source = new byte[stride * height];
        Array.Fill(source, (byte)0x11);
        var destination = new byte[width * height * 4];

        fixed (byte* src = source)
        {
            WebKitOffscreenBackend.Swizzle(src: src, stride: stride, dst: destination,
                width: width, y0: 1, y1: 3);
        }

        Assert.All(destination[..(width * 4)], b => Assert.Equal(0, b)); // row 0 untouched
        Assert.All(destination[(3 * width * 4)..], b => Assert.Equal(0, b)); // row 3 untouched
        Assert.Equal(0x11, destination[width * 4]); // row 1 converted
    }
}
