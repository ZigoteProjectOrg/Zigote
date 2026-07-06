using Zigote.UI.Widgets;

namespace Zigote.Scripting;

/// <summary>
///     The game heads-up display for play mode. A game <see cref="Component" /> assigns <see cref="Root" /> a
///     full <c>Zigote.UI</c> widget tree — any widget works (<c>Stack</c>/<c>Align</c>/<c>Column</c>/
///     <c>Row</c>/<c>Card</c>/<c>ProgressBar</c>/<c>Label</c>, interactive controls, custom-painted leaves).
///     The host (the editor's viewport in play mode) wraps it in an ambient theme + a viewport-sized
///     <c>MediaQuery</c>, measures it tight to the viewport, lays it out at the viewport's top-left, paints it
///     over the 3D image every frame, and routes pointer/keyboard/focus input into it (opaque surfaces and
///     interactive controls capture; transparent regions pass clicks through to the camera).
///     <para>
///         This is the framework's normal retained model: build the tree <b>once</b> (e.g. in
///         <c>OnCreate</c>) and mutate the retained widgets' properties in <c>OnUpdate</c> — hover, focus, and
///         in-flight animations survive. The host knows nothing about what the HUD means; it just lays out and
///         paints whatever the game publishes. Mirrors the <see cref="Input" /> static-provider pattern.
///     </para>
/// </summary>
public static class Hud
{
    /// <summary>
    ///     The game's HUD widget tree, or null for none. Set it from a play-mode <see cref="Component" />;
    ///     clear it (or let <see cref="Reset" /> on play-stop do so) to remove the HUD.
    /// </summary>
    public static Widget? Root { get; set; }

    /// <summary>Drop the HUD. Called by the host when play stops so nothing lingers into edit mode.</summary>
    internal static void Reset()
    {
        Root = null;
    }
}