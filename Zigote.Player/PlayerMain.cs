using System.Diagnostics;
using System.Reflection;
using Zigote.Core.Native;
using Zigote.Runtime;
using Zigote.Scripting.Metadata;
using Zigote.UI.Host;
using Zigote.UI.Theme;

namespace Zigote.Player;

/// <summary>
///     Standalone player entry point. The exported game's generated <c>Program.g.cs</c> calls
///     <see cref="Run" /> with a script-registration callback (the statically-linked replacement for
///     the editor's reflection-over-ALC script discovery — required under NativeAOT).
/// </summary>
public static class PlayerMain
{
    private const int TargetFps = 60;

    public static int Run(Action<ScriptRegistry> registerScripts)
    {
        // iOS owns the process entry: UIApplicationMain must run before any window exists, and
        // SDL's wrapper calls the game body back on the main thread after launch (same inversion
        // as ZigoteApp.Run). Android never reaches this Main — the generated Application object
        // registers RunCore via MobileHost.SetAndroidMain instead.
        if (OperatingSystem.IsIOS())
        {
            var exit = 1;
            MobileHost.RunApp(() => exit = RunCore(registerScripts));
            return exit;
        }

        return RunCore(registerScripts);
    }

    private static int RunCore(Action<ScriptRegistry> registerScripts)
    {
        var content = ResolveContentDir();
        if (content is null)
        {
            Console.Error.WriteLine("[Zigote] Content directory not found next to the executable.");
            return 1;
        }

        GameHost host;
        try
        {
            host = GameHost.Load(Path.Combine(content, "game.zigoteproj"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Zigote] Failed to load game content: {ex.Message}");
            return 1;
        }

        using var app = new App(
            host.Project.Name,
            (uint)Math.Max(320, host.Project.WindowWidth),
            (uint)Math.Max(240, host.Project.WindowHeight)
        );
        app.Theme = ThemeData.Dark;
        if (host.Project.DevToolsEnabled) TryInstallDevTools(app);
        // The 3D scene renders every frame; never idle-wait on events.
        app.ContinuousUpdate = true;

        registerScripts(host.Scripts);
        host.ApplyRenderSettings();
        host.ApplyEnvironment();
        host.Start();

        var viewport = new GameViewport(host, app.Theme);
        app.Root = viewport;
        app.RequestFocus(viewport);

        var targetTicks = Stopwatch.Frequency / TargetFps;
        var clock = Stopwatch.StartNew();
        while (!app.ShouldQuit)
        {
            var frameStart = clock.ElapsedTicks;
            app.Frame();
            host.Tick(app.DeltaTime);

            var remaining = targetTicks - (clock.ElapsedTicks - frameStart);
            if (remaining > 0) Thread.Sleep((int)(remaining * 1000 / Stopwatch.Frequency));
        }

        host.Dispose();
        return 0;
    }

    /// <summary>
    ///     Late-binds to <c>Zigote.UI.DevTools.DevTools.Install(app, ThreeD)</c> via reflection,
    ///     mirroring <c>ZigoteApp</c>'s auto-install: the DevTools assemblies ship only when the
    ///     project's manifest opts in (<c>DevToolsEnabled</c>), so Zigote.Player can't take a
    ///     compile-time dependency on them. Absent DLL or any failure is a silent no-op.
    /// </summary>
    private static void TryInstallDevTools(App app)
    {
        try
        {
            var type = Type.GetType("Zigote.UI.DevTools.DevTools, Zigote.UI.DevTools");
            var install = type?.GetMethod("Install", BindingFlags.Public | BindingFlags.Static);
            var profile = Type.GetType("Zigote.UI.DevTools.DevToolsProfile, Zigote.UI.DevTools");
            if (install is null || profile is null) return;
            install.Invoke(null, [app, Enum.Parse(profile, "ThreeD")]);
        }
        catch
        {
            // DevTools assemblies not bundled with this export — run without the overlay.
        }
    }

    /// <summary>
    ///     Locate the bundled Content dir: next to the executable (Windows/Linux), or in
    ///     Contents/Resources for a macOS .app (the executable lives in Contents/MacOS).
    /// </summary>
    private static string? ResolveContentDir()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates = [
            Path.Combine(baseDir, "Content"),
            Path.GetFullPath(
                Path.Combine(
                    baseDir,
                    "..",
                    "Resources",
                    "Content"
                )
            ),
        ];
        return candidates.FirstOrDefault(Directory.Exists);
    }
}