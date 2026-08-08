using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Zigote.Core.Native;

namespace Zigote.Core.Engine;

/// <summary>
///     Small cross-platform file-manager operations the in-app file browser's context menu
///     needs: move-to-trash (recoverable, never a hard delete) and reveal-in-file-manager.
///     Per-OS strategy: macOS trashes through the native NSFileManager export; Windows through
///     the shell (SHFileOperation with FOF_ALLOWUNDO); Linux implements the XDG Trash spec
///     directly (~/.local/share/Trash). All best-effort — false/no-op on failure.
/// </summary>
public static class FileOperations
{
    /// <summary>Move a file or folder to the OS trash/recycle bin. False when it failed (the
    ///     caller should surface that rather than fall back to permanent deletion).</summary>
    public static bool MoveToTrash(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) return false;
            if (OperatingSystem.IsMacOS()) return MacTrash(full);
            if (OperatingSystem.IsWindows()) return WindowsRecycle(full);
            if (OperatingSystem.IsLinux()) return XdgTrash(full);
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileOperations] Trash failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Open the OS file manager with <paramref name="path" /> selected (or its folder
    ///     shown, where selection isn't supported).</summary>
    public static void RevealInFileManager(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", ["-R", full]));
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\""));
            }
            else
            {
                var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full;
                Process.Start(new ProcessStartInfo("xdg-open", [dir]));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileOperations] Reveal failed: {ex.Message}");
        }
    }

    private static unsafe bool MacTrash(string full)
    {
        var bytes = Encoding.UTF8.GetBytes(full + "\0");
        fixed (byte* p = bytes)
        {
            return NativeEngine.FileTrash(p);
        }
    }

    private static bool WindowsRecycle(string full)
    {
        // Double-NUL-terminated path list, undo-able (recycle bin), no confirmation UI.
        var op = new ShFileOpStruct {
            Func = 0x0003, // FO_DELETE
            From = full + "\0\0",
            Flags = 0x0040 | 0x0010 | 0x0004, // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        };
        return SHFileOperationW(ref op) == 0 && !op.AnyOperationsAborted;
    }

    /// <summary>freedesktop.org Trash spec: move into ~/.local/share/Trash/files plus a
    ///     .trashinfo record so desktop trash UIs can list and restore it.</summary>
    private static bool XdgTrash(string full)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share"
            );
        var files = Path.Combine(dataHome, "Trash", "files");
        var info = Path.Combine(dataHome, "Trash", "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        var target = Path.Combine(files, name);
        for (var i = 2; File.Exists(target) || Directory.Exists(target); i++)
            target = Path.Combine(files, $"{name}.{i}");

        File.WriteAllText(
            Path.Combine(info, Path.GetFileName(target) + ".trashinfo"),
            "[Trash Info]\n" +
            $"Path={Uri.EscapeDataString(full).Replace("%2F", "/")}\n" +
            $"DeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n"
        );
        if (Directory.Exists(full)) Directory.Move(full, target);
        else File.Move(full, target);
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
}