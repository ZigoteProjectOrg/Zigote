using AppLinks;

namespace PluginsDemo;

/// <summary>
///     The desktop entry point — and the first thing the AppLinks plugin is for: a second launch
///     hands its links to the copy already running and exits without opening a window.
/// </summary>
public static class Program
{
    // Synchronous on purpose: an `async Main` resumes after the await on a thread-pool thread,
    // and SDL's video init must run on the macOS main thread.
    public static void Main(string[] args)
    {
        if (!AppLinksPlugin.StartAsync("dev.zigote.PluginsDemo", args).GetAwaiter().GetResult())
            return;   // another instance took the links

        new PluginsDemoApp().Run();
    }
}
