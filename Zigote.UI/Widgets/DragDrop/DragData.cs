namespace Zigote.UI.Widgets;

/// <summary>
///     The payload carried by a drag, shared by external OS drag-and-drop and in-app drags.
///
///     <para>
///         External drops (<see cref="IsExternal" /> = true) populate <see cref="Files" /> and/or
///         <see cref="Text" />. In-app drags carry an arbitrary <see cref="Payload" /> (and optionally a
///         <see cref="Text" /> representation for cross-boundary drops, e.g. dragging out to the OS).
///     </para>
///
///     <para>Drop targets inspect it in <see cref="Widget.CanAcceptDrop" /> to decide acceptance.</para>
/// </summary>
public sealed class DragData
{
    /// <summary>Absolute file paths for an OS file drop; empty when none.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Plain text for an OS/in-app text payload; null when none.</summary>
    public string? Text { get; init; }

    /// <summary>Arbitrary in-app payload (null for external OS drops).</summary>
    public object? Payload { get; init; }

    /// <summary>True when this drag originated from the OS (files/text dragged onto the window).</summary>
    public bool IsExternal { get; init; }

    public bool HasFiles => Files.Count > 0;
    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>Build an in-app drag payload, with an optional text form for dragging out to the OS.</summary>
    public static DragData ForPayload(object payload, string? text = null)
    {
        return new DragData {
            Payload = payload,
            Text = text,
        };
    }
}