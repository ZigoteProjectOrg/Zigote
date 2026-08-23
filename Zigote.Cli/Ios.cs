using System.Text.Json;

namespace Zigote.Cli;

/// <summary>
///     iOS simulators, and running an app on one.
///     <para>
///         Deliberately thin, like <see cref="Device" />: <c>simctl</c> knows what exists and the
///         .NET iOS SDK knows how to install and launch, so the only thing missing was something
///         that talks to both. What this adds is the pair of facts a person otherwise has to know —
///         that the simulator RID is <c>iossimulator-arm64</c> (or -x64 on an Intel Mac) and that
///         the SDK selects a simulator through <c>_DeviceName=:v2:udid=…</c>, a property whose name
///         no error message ever mentions.
///     </para>
/// </summary>
public static class Ios
{
    /// <summary>
    ///     Booted simulators, as <c>simctl</c> reports them. Only booted ones: an unbooted simulator
    ///     is a device the user has not chosen, and there are dozens of them on any Mac with Xcode —
    ///     listing all of them would bury the phone that is actually plugged in.
    /// </summary>
    internal static List<Device.Attached> Simulators()
    {
        if (!OperatingSystem.IsMacOS()) return [];
        string? json = Shell.Capture(
            file: "xcrun",
            arguments: ["simctl", "list", "devices", "booted", "--json"],
            requireSuccess: true
        );
        if (json is null) return [];

        var simulators = new List<Device.Attached>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("devices", out var runtimes)) return [];
            foreach (var runtime in runtimes.EnumerateObject())
            foreach (var device in runtime.Value.EnumerateArray())
            {
                string udid = device.GetProperty("udid").GetString() ?? "";
                string name = device.GetProperty("name").GetString() ?? udid;
                if (udid.Length == 0) continue;
                simulators.Add(new Device.Attached(
                        Serial: udid,
                        State: "device",
                        Model: name,
                        Kind: Device.Kind.IosSimulator
                    )
                );
            }
        }
        catch (JsonException)
        {
            // simctl's schema is Apple's, not ours. A shape we cannot read is "no simulators", not a
            // crash in the middle of `zigote run`.
            return [];
        }

        return simulators;
    }

    /// <summary>
    ///     Build the iOS head and run it on one simulator. <c>dotnet build -t:Run</c> rather than
    ///     <c>dotnet run</c>: the iOS SDK's Run target is what installs the bundle and launches it,
    ///     and `dotnet run` would try to execute the app binary on the Mac.
    /// </summary>
    internal static int Launch(Device.Attached simulator, string directory, bool release)
    {
        if (!OperatingSystem.IsMacOS())
            throw new CliError("iOS needs macOS with Xcode.");

        string head = Projects.RequireHead(
            root: directory,
            suffix: ".iOS",
            create: "`zigote add ios` writes one."
        );
        string rid = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                     is System.Runtime.InteropServices.Architecture.Arm64
            ? "iossimulator-arm64"
            : "iossimulator-x64";

        Console.WriteLine($"  {simulator.Model} (simulator)  →  {rid}");
        Console.WriteLine("  hot reload off on iOS: edits need another `zigote run`.");
        Console.WriteLine();
        return Shell.Dotnet([
                "build", head, "-t:Run",
                "--configuration", release ? "Release" : "Debug",
                $"--property:ZigTargetRid={rid}",
                // The SDK's own way of naming a simulator. Undocumented in every error it produces
                // when it is missing, which is why this verb exists.
                $"--property:_DeviceName=:v2:udid={simulator.Serial}",
            ]
        );
    }
}
