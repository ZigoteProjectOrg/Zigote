using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SecureStorage;

/// <summary>
///     Desktop implementation — three different keystores behind one shape:
///     <list type="bullet">
///         <item>Linux: the Secret Service (GNOME Keyring, KWallet) through <c>secret-tool</c>.</item>
///         <item>macOS: the login keychain through the <c>security</c> tool.</item>
///         <item>Windows: DPAPI (<c>CryptProtectData</c>, current user) over a file in LocalAppData.</item>
///     </list>
///     A Linux box with no <c>secret-tool</c> and no keyring daemon reports unavailable rather
///     than falling back to a file — an app that cannot store a token safely needs to know.
/// </summary>
internal static class SecureStorageDriver
{
    /// <summary>Desktops have no package id; the shared layer falls back to the assembly name.</summary>
    public static string? DefaultService => null;

    private static readonly Lazy<string?> SecretTool = new(() => Which("secret-tool"));

    public static bool Available =>
        OperatingSystem.IsWindows() ||
        (OperatingSystem.IsMacOS() && Which("security") is not null) ||
        (OperatingSystem.IsLinux() && SecretTool.Value is not null);

    public static Task<bool> WriteAsync(string service, string key, string value) => Task.Run(() =>
    {
        if (OperatingSystem.IsWindows()) return WriteWindows(service, key, value);
        if (OperatingSystem.IsMacOS())
            // -U updates the item in place when it already exists.
            // ponytail: the secret rides in argv here, where same-user processes can see it for
            // the length of the call (macOS hides argv from other users). The fix is SecItemAdd
            // through Security.framework interop — worth it when macOS is a shipping target.
            return Run("security",
                ["add-generic-password", "-s", service, "-a", key, "-w", value, "-U"], null).Ok;
        // The secret goes in on stdin, never in argv — argv is world-readable on Linux.
        return Run(SecretTool.Value!,
            ["store", "--label", $"{service}: {key}", "service", service, "account", key],
            value).Ok;
    });

    public static Task<string?> ReadAsync(string service, string key) => Task.Run(() =>
    {
        if (OperatingSystem.IsWindows()) return ReadWindows(service, key);
        if (OperatingSystem.IsMacOS())
        {
            var found = Run("security", ["find-generic-password", "-s", service, "-a", key, "-w"], null);
            // Exit 44 is "no such item"; the password comes back with a trailing newline.
            return found.Ok ? found.Output.TrimEnd('\n') : null;
        }

        var looked = Run(SecretTool.Value!, ["lookup", "service", service, "account", key], null);
        // secret-tool prints the secret with no trailing newline, and exits 1 when there is none.
        return looked is { Ok: true, Output.Length: > 0 } ? looked.Output : null;
    });

    public static Task<bool> DeleteAsync(string service, string key) => Task.Run(() =>
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Delete(FilePath(service, key));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var removed = Run("security", ["delete-generic-password", "-s", service, "-a", key], null);
            // Already absent (exit 44) is the outcome the caller asked for.
            return removed.Ok || removed.Exit == 44;
        }

        return Run(SecretTool.Value!, ["clear", "service", service, "account", key], null).Ok;
    });

    // ---- Windows: DPAPI ---------------------------------------------------------------------

    /// <summary>One file per secret, encrypted to the current user account.</summary>
    internal static string FilePath(string service, string key) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        service, "secrets", key + ".bin");

    [SupportedOSPlatform("windows")]
    private static bool WriteWindows(string service, string key, string value)
    {
        try
        {
            string path = FilePath(service, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Protect(Encoding.UTF8.GetBytes(value), service, encrypt: true));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindows(string service, string key)
    {
        try
        {
            string path = FilePath(service, key);
            if (!File.Exists(path)) return null;
            return Encoding.UTF8.GetString(Protect(File.ReadAllBytes(path), service, encrypt: false));
        }
        catch (Exception)
        {
            // Wrong user, corrupted file, machine reinstalled: the secret is not readable, which
            // for the caller is the same as not being there.
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input, string? description, ref DataBlob entropy, nint reserved,
        nint prompt, int flags, out DataBlob output);

    [SupportedOSPlatform("windows")]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, nint description, ref DataBlob entropy, nint reserved,
        nint prompt, int flags, out DataBlob output);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint handle);

    /// <summary>
    ///     DPAPI both ways. The service name is the secondary entropy, so a secret encrypted for
    ///     one app cannot be decrypted by another running as the same user.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] data, string service, bool encrypt)
    {
        byte[] entropy = Encoding.UTF8.GetBytes(service);
        var input = default(DataBlob);
        var salt = default(DataBlob);
        var output = default(DataBlob);
        var inputHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            input = new DataBlob { Size = data.Length, Data = inputHandle.AddrOfPinnedObject() };
            salt = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };

            const int currentUser = 0;
            bool ok = encrypt
                ? CryptProtectData(ref input, null, ref salt, 0, 0, currentUser, out output)
                : CryptUnprotectData(ref input, 0, ref salt, 0, 0, currentUser, out output);
            if (!ok) throw new InvalidOperationException("DPAPI refused the data");

            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != 0) LocalFree(output.Data);
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    // ---- Process plumbing --------------------------------------------------------------------

    /// <summary>The first match for a tool on PATH, or null when it is not installed.</summary>
    internal static string? Which(string tool)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(dir, tool);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // A malformed PATH entry is not worth an exception.
            }
        }

        return null;
    }

    private static (bool Ok, int Exit, string Output) Run(string file, string[] arguments, string? stdin)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false
            };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return (false, -1, "");
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return (process.ExitCode == 0, process.ExitCode, output);
        }
        catch (Exception)
        {
            return (false, -1, "");
        }
    }
}
