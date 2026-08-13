using Zigote.Core.Native;

namespace Zigote.Core.Rendering;

/// <summary>
///     Validates the ABI between the C# layer and the native Zig library at startup.
/// </summary>
public static class RendererAbiInfo
{
    /// <summary>
    ///     Verify that the native library's struct sizes match the C# compile-time sizes.
    ///     Throws <see cref="InvalidOperationException" /> on any mismatch.
    ///     Called automatically from <see cref="Engine.ZigoteEngine.Initialize" />.
    /// </summary>
    public const uint ExpectedAbiVersion = 9;

    public static unsafe void Validate(ZgAbiInfo info)
    {
        if (info.AbiVersion != ExpectedAbiVersion)
            throw new InvalidOperationException(
                $"ABI version mismatch: C# expects version {ExpectedAbiVersion}, native reports {info.AbiVersion}. Rebuild libzigote."
            );

        var paintCmdSize = (uint)sizeof(ZgPaintCommand);
        var eventSize = (uint)sizeof(ZgEvent);
        var handleSize = (uint)sizeof(nuint);

        if (info.PaintCommandSize != paintCmdSize)
            throw new InvalidOperationException(
                $"ABI mismatch: ZgPaintCommand is {paintCmdSize} bytes in C# but {info.PaintCommandSize} in native."
            );

        if (info.EventSize != eventSize)
            throw new InvalidOperationException(
                $"ABI mismatch: ZgEvent is {eventSize} bytes in C# but {info.EventSize} in native."
            );

        if (info.HandleSize != handleSize)
            throw new InvalidOperationException(
                $"ABI mismatch: handle size is {handleSize} in C# but {info.HandleSize} in native."
            );

        var settingsSize = (uint)sizeof(ZgRenderSettings3D);
        if (info.RenderSettings3DSize != settingsSize)
            throw new InvalidOperationException(
                $"ABI mismatch: ZgRenderSettings3D is {settingsSize} bytes in C# but {info.RenderSettings3DSize} in native. Field order/count drifted — reconcile ZgStructs.cs with src/ffi/root.zig."
            );
    }
}
