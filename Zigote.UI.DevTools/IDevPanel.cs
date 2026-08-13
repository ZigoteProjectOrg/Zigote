using Zigote.UI.Widgets;

namespace Zigote.UI.DevTools;

/// <summary>
///     One page of the devtools overlay. Unlike the old immediate-mode panels, a dev panel builds a
///     <b>retained widget tree</b> once (via <see cref="Build" />) and mutates it in place each frame
///     (via <see cref="Refresh" />) — the same model every other Zigote widget follows. It is grouped
///     under a <see cref="DevCategory" /> tab.
///     <para>
///         Panels live wherever their data does — the built-in General/2D/3D panels ship in
///         <c>Zigote.UI.DevTools</c>; a host can register its own (scene, physics, gameplay) with
///         <see cref="DevTools.Register" />, keeping the dependency arrow pointing the right way.
///     </para>
/// </summary>
public interface IDevPanel
{
    /// <summary>Short tab title.</summary>
    string Title { get; }

    /// <summary>Which top-level tab this panel sits under.</summary>
    DevCategory Category { get; }

    /// <summary>False hides the panel from the strip (e.g. a renderer panel with no native engine).</summary>
    bool IsAvailable => true;

    /// <summary>
    ///     Compose the panel's retained widget tree. Called at most once (the result is cached by the
    ///     host and reused across frames). Retain references to any widgets/charts you mutate in
    ///     <see cref="Refresh" /> as fields.
    /// </summary>
    Widget Build(BuildContext context);

    /// <summary>
    ///     Called once per frame while this panel is the visible one, after tickers advance. Update
    ///     live labels, push nothing (the rings sample themselves), and invalidate charts whose data
    ///     revision changed. Default no-op for static panels.
    /// </summary>
    void Refresh(float dt) { }
}
