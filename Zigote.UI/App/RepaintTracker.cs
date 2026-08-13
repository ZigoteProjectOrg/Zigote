using Zigote.Core;

namespace Zigote.UI.Host;

/// <summary>
///     Dirty tracking for the frame loop. Two independent, composable optimizations:
///     <list type="number">
///         <item>
///             <b>Layer granularity (CPU walk).</b> The retained tree paints into two command lists
///             (root + overlay, see <see cref="App" />); this records which layer actually changed so
///             each is re-walked only when dirty. A clean layer keeps last frame's command buffer.
///         </item>
///         <item>
///             <b>Sub-rectangle damage (GPU scissor).</b> When the <em>only</em> thing that changed
///             this
///             frame is a small, precisely-known region (a blinking caret, a value-drag), the
///             accumulated
///             <see cref="Damage" /> rects are handed to native, which repaints just those rects into
///             the
///             persistent scene texture (<c>loadOp = load</c> + scissor) instead of clearing +
///             redrawing
///             the whole frame. The final swapchain blit stays full-frame (the backbuffer rotates and
///             cannot preserve content), so the win is fill-rate, not the present.
///         </item>
///     </list>
///     <para>
///         <b>Damage is frame-global and conservative.</b> Any change whose region is not precisely
///         known
///         — every path that goes through <see cref="MarkAll" />/<see cref="MarkRoot" />/
///         <see cref="MarkOverlay" /> (discrete input, layout, animation, continuous mode,
///         debug/snackbar/
///         tooltip ticks) — sets <see cref="FullDamage" />, forcing a full clear. Only the precise
///         <see cref="AddDamageRoot" />/<see cref="AddDamageOverlay" /> path (fed by
///         <c>App.MarkPaintFor</c>) produces partial damage. So a dirty frame always has either
///         <see cref="FullDamage" /> or at least one damage rect — never a dirty layer with no region.
///     </para>
///     <para>
///         Rects are kept pairwise non-overlapping (overlapping additions are merged into their
///         bounding
///         union) so native can replay ops once per rect without double-blending the shared pixels.
///     </para>
/// </summary>
public sealed class RepaintTracker
{
    /// <summary>
    ///     Upper bound on tracked disjoint damage rects; overflow degrades to
    ///     <see cref="FullDamage" />.
    /// </summary>
    public const int MaxDamageRects = 16;

    private readonly Rect[] _damage = new Rect[MaxDamageRects];
    private int _damageCount;

    // Starts true so the first frame is a full clear (nothing is preserved yet).

    /// <summary>The root widget layer needs its paint commands re-emitted.</summary>
    public bool RootDirty { get; private set; } = true;

    /// <summary>The overlay layer (dialogs, debug overlay, tooltip, snackbars) needs re-emitting.</summary>
    public bool OverlayDirty { get; private set; } = true;

    /// <summary>
    ///     Anything to paint this frame? When false (and not in a continuous mode) the frame is
    ///     skipped.
    /// </summary>
    public bool AnyDirty => RootDirty || OverlayDirty;

    /// <summary>Count of frames in which the root layer was actually re-walked (diagnostics + tests).</summary>
    public long RootPaints { get; private set; }

    /// <summary>Count of frames in which the overlay layer was actually re-walked (diagnostics + tests).</summary>
    public long OverlayPaints { get; private set; }

    /// <summary>
    ///     True when the whole frame must be repainted (the safe default). When false,
    ///     <see cref="Damage" />
    ///     holds the exact regions that changed and native may repaint only those.
    /// </summary>
    public bool FullDamage { get; private set; } = true;

    /// <summary>Number of disjoint damage rects accumulated this frame (0 when <see cref="FullDamage" />).</summary>
    public int DamageCount => FullDamage ? 0 : _damageCount;

    /// <summary>The disjoint damage rects for this frame (absolute logical-pixel screen rects).</summary>
    public ReadOnlySpan<Rect> Damage =>
        FullDamage ? default : _damage.AsSpan(start: 0, length: _damageCount);

    /// <summary>Mark both layers dirty and force a full-frame repaint — the safe default for any change.</summary>
    public void MarkAll()
    {
        RootDirty = true;
        OverlayDirty = true;
        FullDamage = true;
    }

    /// <summary>Mark the root layer dirty with an unknown region — forces a full-frame repaint.</summary>
    public void MarkRoot()
    {
        RootDirty = true;
        FullDamage = true;
    }

    /// <summary>Mark the overlay layer dirty with an unknown region — forces a full-frame repaint.</summary>
    public void MarkOverlay()
    {
        OverlayDirty = true;
        FullDamage = true;
    }

    /// <summary>
    ///     Mark the root layer dirty over a precise region (e.g. a blinking caret's field). Keeps the
    ///     frame in partial-repaint mode unless something else forced <see cref="FullDamage" />.
    /// </summary>
    public void AddDamageRoot(Rect region)
    {
        RootDirty = true;
        Accumulate(region);
    }

    /// <summary>Mark the overlay layer dirty over a precise region. See <see cref="AddDamageRoot" />.</summary>
    public void AddDamageOverlay(Rect region)
    {
        OverlayDirty = true;
        Accumulate(region);
    }

    private void Accumulate(Rect region)
    {
        if (FullDamage) return;

        // A dirty layer with no locatable region is not safe to repaint partially — fall back to full.
        if (region.IsEmpty)
        {
            FullDamage = true;
            return;
        }

        // Merge into any overlapping existing rect, iterating to a fixpoint: a merge grows the rect,
        // which may then overlap another, so keep folding until the set is pairwise non-overlapping.
        while (true)
        {
            int mergedInto = -1;
            for (int i = 0; i < _damageCount; i++)
            {
                if (!_damage[i].Overlaps(region)) continue;
                region = Rect.Union(a: _damage[i], b: region);
                _damage[i] = _damage[--_damageCount]; // swap-remove the absorbed rect
                mergedInto = i;
                break;
            }

            if (mergedInto < 0) break;
        }

        if (_damageCount >= _damage.Length)
        {
            // Too many disjoint regions to track individually — repaint everything this frame.
            FullDamage = true;
            return;
        }

        _damage[_damageCount++] = region;
    }

    /// <summary>
    ///     Widen this frame's GPU damage without re-dirtying a layer — used by the post-paint
    ///     paint-list diff, whose layer has already been re-walked and only needs a larger replay
    ///     region. Degrades to <see cref="FullDamage" /> on overflow like the mark paths.
    /// </summary>
    public void AddDamageBoundsOnly(Rect region) => Accumulate(region);

    /// <summary>
    ///     Force this frame to a full-frame repaint without re-dirtying the (already re-walked)
    ///     layers — the paint-list diff found a change it cannot bound.
    /// </summary>
    public void ForceFullDamage() => FullDamage = true;

    /// <summary>Record that the root layer was re-painted this frame; clears its dirty flag.</summary>
    public void RootPainted()
    {
        RootDirty = false;
        RootPaints++;
    }

    /// <summary>Record that the overlay layer was re-painted this frame; clears its dirty flag.</summary>
    public void OverlayPainted()
    {
        OverlayDirty = false;
        OverlayPaints++;
    }

    /// <summary>
    ///     Clear the accumulated damage for the next frame. Called once per frame after the paint has been
    ///     submitted and presented. Resets to the "no partial info yet" state (<see cref="FullDamage" />
    ///     becomes false — the next frame only becomes full again if something marks it so).
    /// </summary>
    public void ResetDamage()
    {
        FullDamage = false;
        _damageCount = 0;
    }
}
