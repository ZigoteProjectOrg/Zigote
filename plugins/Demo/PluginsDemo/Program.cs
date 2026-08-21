using AppLinks;

namespace PluginsDemo;

/// <summary>
///     The desktop entry point — and the first thing the AppLinks plugin is for: a second launch
///     hands its links to the copy already running and exits without opening a window.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        if (!await AppLinksPlugin.StartAsync("dev.zigote.PluginsDemo", args))
            return;   // another instance took the links

        new PluginsDemoApp().Run();
    }
}
