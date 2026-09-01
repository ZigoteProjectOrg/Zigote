using Zigote.Core;
using Zigote.Core.Animation;

namespace Zigote.UI.Widgets;

/// <summary>
///     Named motions built out of <see cref="Animate" />'s effects — the ones every app writes by
///     hand otherwise. Each one is a couple of effects with the timing already chosen, and each
///     returns the same <see cref="Animate" /> so they keep composing:
///     <code>
///     new Card(...).PopIn().Wobble(delay: 600.ms);
///     </code>
///     <para>
///         Applying a preset to something that is already an <see cref="Animate" /> appends to that
///         timeline instead of wrapping it again, so the chain above builds one widget, not three.
///     </para>
/// </summary>
public static class AnimatePresets
{
    /// <summary>The timeline to append to: the widget's own if it is one, a fresh wrapper if not.</summary>
    private static Animate Chain(this Widget widget) => widget as Animate ?? widget.Animate();

    // ── Entrances ─────────────────────────────────────────────────────────────

    /// <summary>Fade in while rising into place — the default entrance for content.</summary>
    public static Animate FadeInUp(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null, float distance = 16f)
    {
        return widget.Chain()
            .Fade(duration: duration, delay: delay, curve: Curves.EaseOutCubic)
            .Move(begin: new Offset(x: 0f, y: distance), end: Offset.Zero);
    }

    /// <summary>Fade in while dropping into place — for things that belong above, like a banner.</summary>
    public static Animate FadeInDown(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null, float distance = 16f)
    {
        return widget.Chain()
            .Fade(duration: duration, delay: delay, curve: Curves.EaseOutCubic)
            .Move(begin: new Offset(x: 0f, y: -distance), end: Offset.Zero);
    }

    /// <summary>Fade in from the left.</summary>
    public static Animate FadeInLeft(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null, float distance = 24f)
    {
        return widget.Chain()
            .Fade(duration: duration, delay: delay, curve: Curves.EaseOutCubic)
            .Move(begin: new Offset(x: -distance, y: 0f), end: Offset.Zero);
    }

    /// <summary>Fade in from the right.</summary>
    public static Animate FadeInRight(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null, float distance = 24f)
    {
        return widget.Chain()
            .Fade(duration: duration, delay: delay, curve: Curves.EaseOutCubic)
            .Move(begin: new Offset(x: distance, y: 0f), end: Offset.Zero);
    }

    /// <summary>Fade in with a small overshooting scale — dialogs, toasts, anything that arrives.</summary>
    public static Animate PopIn(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null)
    {
        return widget.Chain()
            .Fade(duration: duration ?? 220.ms, delay: delay, curve: Curves.EaseOutCubic)
            .Scale(begin: 0.85f, end: 1f, curve: Curves.EaseOutBack);
    }

    /// <summary>Grow in from small. Louder than <see cref="PopIn" />; use it on hero content.</summary>
    public static Animate ZoomIn(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null, float from = 0.5f)
    {
        return widget.Chain()
            .Fade(duration: duration ?? 320.ms, delay: delay, curve: Curves.EaseOutCubic)
            .Scale(begin: from, end: 1f);
    }

    /// <summary>Unwind into place from a quarter turn — a playful entrance for icons and badges.</summary>
    public static Animate SpinIn(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null, float turns = 0.25f)
    {
        return widget.Chain()
            .Fade(duration: duration ?? 420.ms, delay: delay, curve: Curves.EaseOutCubic)
            .Rotate(begin: -turns, end: 0f, curve: Curves.EaseOutBack)
            .Scale(begin: 0.7f, end: 1f);
    }

    // ── Exits ─────────────────────────────────────────────────────────────────

    /// <summary>Shrink and fade away. Pair with <see cref="PopIn" /> for a symmetric dismissal.</summary>
    public static Animate PopOut(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null)
    {
        return widget.Chain()
            .Fade(begin: 1f, end: 0f, duration: duration ?? 180.ms, delay: delay,
                curve: Curves.EaseInCubic)
            .Scale(begin: 1f, end: 0.9f);
    }

    /// <summary>Fade out downward — the mirror of <see cref="FadeInUp" />.</summary>
    public static Animate FadeOutDown(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null, float distance = 16f)
    {
        return widget.Chain()
            .Fade(begin: 1f, end: 0f, duration: duration, delay: delay, curve: Curves.EaseInCubic)
            .Move(begin: Offset.Zero, end: new Offset(x: 0f, y: distance));
    }

    // ── Attention ─────────────────────────────────────────────────────────────

    /// <summary>Shake sideways — a rejected password, a field that failed validation.</summary>
    public static Animate Attention(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null, float amount = 6f)
    {
        return widget.Chain()
            .Shake(duration: duration ?? 400.ms, delay: delay, amount: new Offset(x: amount, y: 0f));
    }

    /// <summary>Rock back and forth — the same "no" as <see cref="Attention" />, in rotation.</summary>
    public static Animate Wobble(this Widget widget, TimeSpan? duration = null,
        TimeSpan? delay = null, float amount = 0.02f)
    {
        return widget.Chain().Wiggle(duration: duration ?? 500.ms, delay: delay, amount: amount);
    }

    // ── Ambient loops ─────────────────────────────────────────────────────────

    /// <summary>Breathe in and out of a slightly larger size, forever. Draws the eye without noise.</summary>
    public static Animate Pulse(this Widget widget, TimeSpan? duration = null, float amount = 1.06f)
    {
        var a = widget.Chain()
            .Scale(duration: duration ?? 900.ms, begin: 1f, end: amount, curve: Curves.EaseInOutSine);
        a.Loop = true;
        a.LoopReverse = true;
        return a;
    }

    /// <summary>Fade gently in and out, forever — a "still loading" hint that costs no layout.</summary>
    public static Animate Breathe(this Widget widget, TimeSpan? duration = null, float dim = 0.55f)
    {
        var a = widget.Chain()
            .Fade(duration: duration ?? 1.seconds, begin: 1f, end: dim, curve: Curves.EaseInOutSine);
        a.Loop = true;
        a.LoopReverse = true;
        return a;
    }

    /// <summary>Rotate forever at a constant rate — a spinner out of any glyph.</summary>
    public static Animate Spin(this Widget widget, TimeSpan? duration = null, bool clockwise = true)
    {
        var a = widget.Chain()
            .Rotate(duration: duration ?? 1.2.seconds, begin: 0f, end: clockwise ? 1f : -1f,
                curve: Curves.Linear);
        a.Loop = true;
        return a;
    }

    // ── Sequences ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Apply an entrance to each widget in turn, each one starting <paramref name="step" /> after
    ///     the one before it — the list that arrives as a wave instead of all at once. Defaults to
    ///     <see cref="FadeInUp" />; pass <paramref name="entrance" /> for any other motion.
    /// </summary>
    public static List<Widget> Stagger(this IEnumerable<Widget> children, TimeSpan? step = null,
        Func<Widget, TimeSpan, Animate>? entrance = null)
    {
        var stepped = step ?? 60.ms;
        var build = entrance ?? ((w, d) => w.FadeInUp(delay: d));
        var result = new List<Widget>();
        int index = 0;
        foreach (var child in children)
            result.Add(build(child, stepped * index++));
        return result;
    }
}
