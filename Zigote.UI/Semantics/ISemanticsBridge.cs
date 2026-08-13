using Zigote.UI.Host;

namespace Zigote.UI.Semantics;

/// <summary>
///     The seam between Zigote's platform-neutral <see cref="SemanticsNode" /> tree and a host
///     platform's
///     native accessibility API (macOS <c>NSAccessibility</c>, Windows UI Automation, Linux
///     AT-SPI/ATK).
///     The app builds + diffs the semantics tree in managed code; a bridge implementation is
///     responsible
///     for projecting it onto the OS so screen readers (VoiceOver / Narrator / Orca) can read the UI
///     and
///     route their gestures back through <see cref="PerformAction" />.
///     <para>
///         No native bridge ships today — this contract is the reserved seam (mirroring the renderer's
///         <c>GpuBackend</c> vtable). <see cref="App.SemanticsBridge" /> defaults to <c>null</c>, in
///         which case the tree is still built on demand for the in-engine Semantics inspector + tests
///         but
///         is not pushed to the OS. Assign a bridge to light up a real screen reader.
///     </para>
/// </summary>
public interface ISemanticsBridge
{
    /// <summary>
    ///     Push the current accessibility tree. Called after layout whenever the semantics changed
    ///     (structure, focus, or an announced value). Implementations should diff against the previously
    ///     supplied tree by <see cref="SemanticsNode.Id" /> rather than rebuilding native peers wholesale.
    /// </summary>
    void Update(SemanticsNode root);

    /// <summary>The node that currently holds accessibility focus changed (or null when focus cleared).</summary>
    void FocusChanged(SemanticsNode? focused) { }

    /// <summary>Tear down all native peers (window closing / bridge detached).</summary>
    void Clear() { }
}
