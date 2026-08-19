using Camera;
using Zigote.Core.Platform;

namespace CameraExample;

/// <summary>The desktop entry point.</summary>
public static class Program
{
    public static void Main(string[] args)
    {
        // The one line any consumer adds per plugin: registered before the App exists, so
        // the plugin starts with the app and its channels are live by the first frame.
        PluginHost.Register(new CameraPlugin());
        new CameraExampleApp().Run();
    }
}
