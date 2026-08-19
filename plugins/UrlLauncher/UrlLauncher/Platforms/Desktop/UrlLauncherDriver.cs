using System.Diagnostics;

namespace UrlLauncher;

/// <summary>Desktop implementation — the shell knows the handler on all three OSes.</summary>
internal static class UrlLauncherDriver
{
    public static Task<bool> OpenAsync(string url)
    {
        // On a worker: `xdg-open` behind UseShellExecute can block for a second or two while the
        // handler starts, and the frame it blocks is the click that asked for it.
        return Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })
                    ?.Dispose();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }
}
