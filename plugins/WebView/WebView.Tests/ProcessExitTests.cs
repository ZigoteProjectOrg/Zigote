using System.Diagnostics;
using Xunit;
using Zigote.Core.Engine;

namespace WebView.Tests;

/// <summary>
///     A process that used a webview must be able to exit. WebKit finalizes its process-wide
///     context from a C++ static destructor at exit, on the process main thread — and
///     <c>WebsiteDataStore</c>'s destructor asserts it is on WebKit's main thread, which is the GTK
///     thread whenever the plugin owns one. Nothing inside the process can observe that abort
///     (it happens after the last managed code runs), so the check is a child process and its
///     exit code: 0, not 134.
/// </summary>
public class ProcessExitTests
{
    private static bool Headless =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null &&
        Environment.GetEnvironmentVariable("DISPLAY") is null;

    [Fact]
    public void AProcessThatBuiltAWebViewExitsCleanly()
    {
        if (Headless) return;

        // Under `dotnet test` the current process is the test host, not the muxer — and the muxer
        // is what can run the test assembly as a plain program.
        string host = Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet"
            ? Environment.ProcessPath!
            : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(ProcessExitTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("*ExitProbe*");
        start.Environment["ZIGOTE_EXIT_PROBE"] = "webview-only-dispose";
        start.Environment["ZIGOTE_WEBVIEW_THREADED"] = "1";

        using var child = Process.Start(start)!;
        string output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd();
        Assert.True(child.WaitForExit(120_000), "the child never exited");
        // 134 = SIGABRT inside WebKit's exit handler. Anything else non-zero means the child did
        // not run at all, which would make this test silently useless.
        Assert.True(child.ExitCode == 0, $"child exited {child.ExitCode}:\n{output}");
        Assert.Contains("Total: 1", output); // the probe really ran, so exit code 0 means something
    }
}

/// <summary>The child half of <see cref="ProcessExitTests" />, and a bench harness for teardown
///     orders. Inert unless <c>ZIGOTE_EXIT_PROBE</c> names one.</summary>
public class ExitProbe
{
    [Fact]
    public void BuildAndTearDown()
    {
        string mode = Environment.GetEnvironmentVariable("ZIGOTE_EXIT_PROBE") ?? "";
        if (mode.Length == 0) return;

        ZigoteEngine? engine = null;
        if (mode.StartsWith("engine", StringComparison.Ordinal))
        {
            engine = new ZigoteEngine();
            engine.Initialize(width: 320, height: 240, title: "exit probe");
            if (mode == "engine-only") { engine.Dispose(); return; }
        }

        var controller = new WebViewController();
        controller.EnsureAttached(new NativeParent(NativeParentKind.Wayland, 0, 0));
        Thread.Sleep(500);

        switch (mode)
        {
            case "both": controller.Dispose(); engine!.Dispose(); break;
            case "engine-first": engine!.Dispose(); controller.Dispose(); break;
            case "webview-only-dispose": controller.Dispose(); break;
            case "engine-only-dispose": engine!.Dispose(); break;
            case "neither": break;
        }
    }
}
