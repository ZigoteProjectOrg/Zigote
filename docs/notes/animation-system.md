# One animation core for UI, 2D and 3D

A design document for Zigote's animation system: what exists, what is missing, and the smallest set
of additions that makes one core drive widgets, sprites, particles, scene nodes and cameras alike.
Written against the tree at `5e59c82` plus the working copy.

Everything in the inventory is code that exists today, with a file reference. Everything in the
design is marked **build**, **reuse**, **delete** or **deferred**, and every deferral says what
would trigger it. Where a proposal was considered and rejected it says so — the rejected halves are
the useful part of a design document.

> **Status (2026-09-01).** **Phase 1 has landed** — `Motion`/`MotionSpec`, the full easing family
> and its combinators, `Speed`/`TimeScale`/`ReduceMotion`, springs on `AnimationController` and on
> every implicit widget, rotation effects, the named presets and `Stagger`, with a Gallery **Motion**
> page and the Animations page's spring group now driving a real spring. Three things in the first
> draft turned out to be wrong and are corrected below: rotation was never blocked, reduce-motion
> must exempt indicators, and `SmoothScroller` has not been converted yet.
>
> **Contents:** [Verdict](#verdict) · [What landed](#what-landed) · [What exists today](#what-exists-today) ·
[The three clocks](#the-three-clocks) · [The gaps](#the-gaps) ·
[Where this beats Flutter](#where-this-beats-flutter) · [The design](#the-design) ·
[Feature catalogue](#feature-catalogue) · [API sketches](#api-sketches) ·
[Invariants](#invariants-anything-here-must-not-break) · [Phasing](#phasing) ·
[Deliberately not building](#deliberately-not-building) · [Measurement](#measurement)

---

## Verdict

Zigote has **four animation systems that do not know about each other**: the widget
`AnimationController`/`Ticker` pair, `SmoothScroller`'s hand-rolled fling integrator, the 3D
`AnimationClip`/`AnimationPlayer`, and VFX's `FloatCurve`. Each is individually fine. Together they
mean a curve authored in the VFX editor cannot ease a widget, a spring cannot drive a scene node,
and a fling cannot hand its velocity to a settling animation.

The structural luck is large: **`Zigote.Core` is already referenced by `Zigote.UI`, `Zigote.Vfx`,
`Zigote.Render2D`, `Zigote.ECS`, `Zigote.Physics2D` and `Zigote.Runtime`**, and already carries
`Vec2`/`Vec3`/`Quat`/`Transform3D` (`Zigote.Core/Math3D/`) next to `Color`/`Offset`/`Size`. A
universal animation core is a namespace decision, not an architecture migration. `Zigote.Core/Animation`
is already that namespace; it is simply under-built.

Ranked by payoff over risk:

| # | Item | Payoff | Risk | Verdict |
|---|---|---|---|---|
| 1 | **`Motion`** — value+velocity primitive with spring/decay/duration kinds; retarget preserves velocity | Largest. Turns every implicit animation interruptible and deletes two hand-rolled integrators | Low | **Build** — Phase 1 |
| 2 | **Curve combinators** (`Interval`, `Flipped`, `Bezier`, `Keyed`) on the existing `Func<float,float>` currency | Unlocks stagger, mirrored exits, editor-authored easing | Very low | **Build** — Phase 1 |
| 3 | **`Speed` / `TimeScale` / reduce-motion** on the controller | Accessibility + debugging (slow-mo) for ~40 lines | Very low | **Build** — Phase 1 |
| 4 | **Pose blending** for 3D clips (weighted pose buffer, crossfade, additive layer) | The difference between "plays a glTF clip" and "animates a character" | Medium | **Build** — Phase 3 |
| 5 | **Lerp unification** — one `ILerp<T>` static-abstract, delete the four `Tween<T>` subclasses | Small; mostly deletion | Low | **Build** — Phase 2 |
| 6 | **Hero / shared-element transitions** + `.Stagger()` over children | The two things people notice and Flutter is known for | Medium | **Build** — Phase 4 |
| 7 | **Clip events** (time-tagged callbacks) for 3D, matching `SpriteAnimator`'s frame events | Gameplay hooks (footsteps, hitboxes) | Low | **Build** — Phase 3 |
| 8 | ~~**Property-binding tracks** (clip drives arbitrary properties by path)~~ | Would generalise clips past TRS | Medium-high | **Deferred** — no consumer. Trigger: the editor timeline wants to key a non-transform property |
| 9 | ~~**Animation state machine / blend-tree graph**~~ | Character controllers | High | **Deferred** — `Zigote.Graphs.*` could host it; nothing asks yet |
| 10 | ~~**Skinning, IK, morph-target apply**~~ | Character rendering | High | **Not here** — engine-side (Zig), a different document |

The one-line version: **the core is missing one primitive (velocity), one combinator set (curves)
and one blend step (poses). Everything else in "feature rich" is composition on top of what already
ships.**

---

## What landed

| Change | Where | Notes |
|---|---|---|
| `MotionSpec` + `Motion` | `Zigote.Core/Animation/Motion.cs` | Duration / Spring / Decay in one struct, fixed 1/240 s substep, settle on displacement *and* velocity. Named fields rather than the sketch's `A`/`B` — the sketch was terser and worse |
| Easing family + combinators | `Zigote.Core/Animation/Curves.Easing.cs` | Cubic→quintic, sine, expo, circ, back, elastic, bounce, plus `Interval`, `Flipped`, `Chain` and a real `Bezier` solver. The eight original curves are untouched |
| Springs on the controller | `AnimationController.Spec` | Null by default, so all 40 files that use it are unchanged. `Velocity` is readable *and* settable — that is the gesture hand-off seam |
| `Speed` / `TimeScale` / `ReduceMotion` | same | See the correction below on what reduce-motion may not touch |
| Springs on implicit widgets | `ImplicitlyAnimatedWidget.Spec`, `.AllowOvershoot` | One property makes `AnimatedAlign`/`Container`/`Padding`/`Opacity`/`Size` interruptible. Velocity carries across the restart |
| Rotation | `RotateEffect`, `WiggleEffect`, `Animate.Rotate()/Wiggle()` | Through `PaintList.PushTransform`, with inverse-mapped hit-testing — the path the `Transform` widget already took |
| Loops | `Animate.Loop` / `LoopReverse` | Ambient motion without a hand-rolled ticker |
| Named presets | `Zigote.UI/Widgets/Animate/AnimatePresets.cs` | `FadeInUp/Down/Left/Right`, `PopIn`, `ZoomIn`, `SpinIn`, `PopOut`, `FadeOutDown`, `Attention`, `Wobble`, `Pulse`, `Breathe`, `Spin`, `Stagger`. Applying one to an existing `Animate` appends to that timeline instead of nesting a second widget |
| `AnimatedFloat` | `Zigote.Core/Animation/AnimatedFloat.cs` | One number that springs to a target and stops — `Motion` plus a ticker and a dirty callback. It replaced the controller-plus-`from`/`to`/`started`-triple that had been written out three times (segmented control pill, tab bar underline, progress fill) and is what the toggle group's sliding selection is built on |
| Adwaita adoption | `AdwMotion` + 10 widgets | The kit's four springs in one token class; `AdwSwitch`, `AdwBanner`, `AdwPopover`, `AdwDialog`, `AdwToast`, `AdwOverlaySplitView`, `AdwNavigationView` and `AdwExpanderRow` are now interruptible; `AdwProgressBar` eases to each value; `AdwStatusPage` fades up once per mount; the bottom sheet's snap takes the release velocity; `AdwToggleGroup`'s selection slides between segments (and `AdwInlineViewSwitcher` retains its group so it can) |
| Gallery | `Pages/MotionPage.cs`, `Pages/AnimationsPage.cs` | A new **Motion** page (interruption side-by-side, entrances, exits, attention, ambient loops, a staggered list), and the Animations page's 35 easings now exact and its spring group real — mass, stiffness, damping, initial velocity and epsilon all drive the motion, with a live value/velocity read-out |
| Tests | `Zigote.Tests/MotionTests.cs`, `AnimateTests.cs`, `AdwMotionTests.cs` | 33 covering settle-and-stop, frame-rate independence, retarget velocity, overshoot, decay projection, reduce-motion, rotation paint + hit-test, stagger timing, every `AdwMotion` spec settling both ways, the progress bar's ease-and-stop, the sheet's velocity hand-off, and the toggle group's selection being mid-slide rather than arrived on frame two |
| `Zigote.UI.Theme.Motion` → `Durations` | 11 files | The token class had the name the new primitive needed, and it was a set of durations anyway |

### Corrections to the first draft

1. **Rotation was never blocked.** `PaintList.PushTransform` and the `Transform` widget have shipped
   a 2×3 affine on the native transform stack all along; the `Animate` doc comment claiming "the
   renderer has no such primitives" was stale. Rotation cost ~40 lines, not a renderer project.
   Blur and colour-matrix remain genuinely absent.
2. **Reduce-motion must exempt indicators.** The first draft said durations collapse to zero. Making
   `Repeat()` obey that froze every spinner and indeterminate progress bar — which reads as a hung
   app, the opposite of an accessibility win. `Forward`/`Reverse` jump; `Repeat` does not, and
   decorative loops opt out one level up in `Animate.Loop`.
3. **`SmoothScroller` is not converted yet.** The deletion is still the right one and the `Decay`
   spec that replaces its integrator exists and is tested. It is a behaviour change to the single
   most-used interaction in the framework, so it wants its own change and its own before/after —
   not a ride-along in the phase that introduced the primitive.

---

## What exists today

| Piece | Where | What it does | Reach |
|---|---|---|---|
| `AnimationController` | `Zigote.Core/Animation/AnimationController.cs` | 0→1 progress over a duration, forward/reverse/toggle/repeat/seek, `Completed`/`Dismissed` edges, dt clamped by `MaxFrameDelta`, implicit `float` conversion | 40 files |
| `Ticker` / `ITickerProvider` | `Zigote.Core/Animation/Ticker.cs` | Global running set, swap-remove on stop, `Muted`, `AnyActive` as an incrementally-maintained counter | 61 creation sites |
| `Curves` | `Zigote.Core/Animation/Curves.cs` | 8 easing functions incl. `Spring` (a *normalised* under-damped response — a curve, not a simulation) | 64 refs / 34 files |
| `Tween<T>` | `Zigote.Core/Animation/Tween.cs` | Abstract lerp + four hand-written subclasses (float, `Color`, `Offset`, `Size`) | 13 refs |
| `ImplicitlyAnimatedWidget` | `Zigote.UI/Widgets/Transitions/` | Owns a controller + its own `Ticker`, restarts 0→1 on each change; base of `AnimatedOpacity`/`Align`/`Padding`/`Container`/`Size`/`Switcher` | 8 widgets |
| Explicit transitions | `Zigote.UI/Widgets/Transitions/` | `FadeTransition`, `ScaleTransition`, `SlideTransition`, `AnimatedBuilder`, `TweenAnimationBuilder` | — |
| `Animate` | `Zigote.UI/Widgets/Animate/` | flutter_animate-style chain: `Fade`/`Scale`/`Move`/`Slide`/`Shake`/`Then`, delay-duration-curve inheritance, resolved absolute windows, `Target` for state-driven play | Gallery + apps |
| `AnimateDurations` | same | `500.ms`, `0.3.seconds` extension members | — |
| `Route.Transition` | `Zigote.UI/Widgets/Navigation/Route.cs` | One controller per route; `TransitionOffset`/`TransitionOpacity` overridden by `PageRoute` | Navigator |
| `SmoothScroller` | `Zigote.UI/Widgets/Layout/SmoothScroller.cs` | Exponential ease toward target + exponential fling decay, **its own integrator**, not a controller | Every scroller |
| `AnimationClip` / `Channel` / `Player` | `Zigote.Runtime/Animation/AnimationData.cs` | glTF-shaped keyframe channels (T/R/S/weights), binary-searched sampling, shortest-path quaternion nlerp, allocation-free `SampleInto`, name-matched apply to `SceneNode` | Editor, runtime |
| `SpriteAnimator` | `Zigote.Render2D/SpriteAnimator.cs` | Frame clips, loop/ping-pong/once, `NextClip`, per-frame events that never skip a frame | 2D games |
| `FloatCurve` / `CurveKey` | `Zigote.Vfx/Sampling.cs` | Keyed 0..1 curve sampled per particle by normalised age; baked to 8 samples for the GPU path | 30 refs |
| `TimelinePanel` | `Zigote.Editor/Panels/TimelinePanel.cs` | Transport + scrubber over `EditorState.AnimationPlayer` |  Editor |
| `GltfAnimationImporter` | `Zigote.Editor/Animation/` | glTF → `AnimationClip` | Import |

Tests already cover the seams: `ImplicitAnimationTests`, `AnimateTests`, `SpriteAnimationTests`, and
`FrameHotPathAllocationTests.TickerAdvanceAll_SteadyState_AllocatesZero`.

### The three clocks

| Clock | Rate | Driven from | Who rides it |
|---|---|---|---|
| **Presentation** | Render dt, clamped to `MaxFrameDelta` (100 ms) | `Ticker.AdvanceAll` in `Zigote.UI/App/App.cs:1447`, after event dispatch so input lands in the frame that presents it | Every widget animation, routes, scrollers |
| **Simulation** | Fixed 120 Hz (`GameSession.FixedDt`), backlog capped at 250 ms | `GameSession.Update` → scripts, physics, `Sprites.AdvanceAnimation`, VFX | Sprite clips, particles, gameplay |
| **Ad-hoc** | Whatever the caller passes | `AnimationPlayer.Tick(dt)` called by the editor / host | 3D clips |

This split is correct and the design keeps it. Presentation motion must track the display and
absorb hitches; simulation motion must be deterministic and frame-rate independent. The bug this
prevents is a spring integrated at a variable dt inside a fixed-step world — the same input
producing different gameplay on a faster machine.

What is *not* correct today is that the third clock is unnamed. `AnimationPlayer` is ticked by
whoever remembers to; a clip attached to a gameplay entity should be on the fixed clock, a clip
driving a cutscene camera on the presentation clock, and nothing in the API says which.

---

## The gaps

Against Flutter (`package:flutter/animation.dart` + `flutter_animate`), SwiftUI, and Unity, ranked
by how often the absence is felt:

| Gap | Consequence today |
|---|---|
| **No velocity anywhere except the scroll fling** | Every implicit animation restarts 0→1 on retarget. Change a target mid-flight and the motion visibly kinks — Flutter has the same flaw; SwiftUI does not |
| **No physical spring** | `Curves.Spring` is a fixed normalised shape: it cannot absorb an incoming velocity, cannot settle early, and its overshoot does not scale with distance |
| **No `Interval`/`Flipped`/`Bezier` combinators** | Stagger and mirrored exit animations are hand-rolled per site; designer-supplied cubic-bezier easings cannot be typed in |
| **No clip blending** | Two `AnimationPlayer`s over one subtree fight for the same node transforms — last writer wins |
| **No clip events** | 3D clips cannot fire a footstep; the 2D `SpriteAnimator` can. Same need, one implementation |
| **No shared-element (Hero) transition** | The single most-recognised Flutter motion is absent |
| **No stagger helper** | List entrance animations are written by hand with per-index delays |
| **No global speed / reduce-motion** | No slow-mo debugging, and the OS "reduce motion" preference cannot be honoured centrally — only `Skeleton` has a local switch |
| **Four curve dialects** | `Func<float,float>`, `FloatCurve` stops, `AnimationInterp` on channels, and `SmoothScroller`'s constants |
| **`Tween<T>` is one class per type** | Adding `Vec3` or `Transform3D` interpolation means another subclass, despite `Vec3.Lerp` and `Quat.Slerp` already existing |
| **Rotation / blur / colour-matrix effects missing from `Animate`** | Blocked on renderer primitives, not on the animation system — flagged, not scheduled here |

---

## Where this beats Flutter

Five honest axes. Not "more classes" — Flutter's animation library is small and good.

1. **One core, four consumers.** Flutter's animation library is UI-only; a Flutter game reaches for
   Flame and gets a second, unrelated one. Here `Zigote.Core.Animation` is already below UI, VFX,
   sprites, ECS and the scene graph, and already sits next to the maths types a 3D animation needs.
   The same curve object eases a button and a particle's alpha; the same spring settles a sheet and
   a camera boom.

2. **Interruption is the default, not an escape hatch.** Flutter's `AnimatedContainer` restarts its
   controller when the target changes: velocity resets to whatever the curve says at t=0. With
   `Motion` carrying velocity, retargeting mid-flight is continuous — the behaviour SwiftUI's
   `withAnimation(.spring)` gets and Flutter needs `AnimationController` + `SpringSimulation` +
   manual velocity plumbing to approximate.

3. **Explicit clocks.** Flutter has one (vsync). Zigote already runs a fixed-step simulation loop
   next to the render loop; saying which clock an animation rides is a property, not a workaround.

4. **Animations are data the editor can author.** Curves and clips are values, and there is already
   a curve editor (`GradientEditor`/VFX stops) and a `TimelinePanel`. Flutter has no editor at all;
   its curves are code.

5. **Zero-allocation and idle-gated by construction.** `Ticker.AnyActive` already gates the event
   loop's sleep (`App.cs:1297`) and an allocation guard test already pins the steady state. Any new
   motion type inherits both constraints as invariants, not aspirations.

Being *bigger* than Flutter's animation library is not a goal. The catalogue below adds nine things
and deletes four.

---

## The design

Six layers, bottom-up. Layers 0–2 live in `Zigote.Core/Animation`; nothing above needs a new project.

| Layer | Type | Status |
|---|---|---|
| 0 | `Func<float, float>` curves + combinators | **reuse + extend** |
| 1 | `Motion` — value, velocity, target, spec | **build** (the one real addition) |
| 2 | `ILerp<T>` + `Lerp` overloads | **build small, delete more** |
| 3 | `AnimationController`, `Ticker` — transport | **reuse**, thin over layer 1 |
| 4 | Clips, channels, poses, blending | **extend** |
| 5 | Widgets, effects, transitions, editor | **reuse + 3 additions** |

### Layer 0 — curves stay functions

The currency is already `Func<float, float>` (`AnimationController.Curve`, `AnimateEffect.Curve`),
used at 64 sites. It does not change. What is added is combinators, because every one of them is a
few lines and each removes a hand-rolled site:

```csharp
public static Func<float, float> Interval(float begin, float end, Func<float, float>? curve = null);
public static Func<float, float> Flipped(Func<float, float> curve);   // 1 - c(1 - t)
public static Func<float, float> Bezier(float x1, float y1, float x2, float y2);
public static Func<float, float> Keyed(IReadOnlyList<CurveKey> stops); // shared with VFX
public static Func<float, float> Chain(Func<float, float> a, Func<float, float> b);
```

**Rejected: a `Curve` class or struct hierarchy.** VFX needs its curve as *serialisable keyed data*
(`VfxAssetJson` round-trips stops; `VfxGpuParams` bakes 8 samples for the shader). Widgets need a
*function*. Unifying the storage would force one of them into the wrong shape. Unify the
**evaluation contract** instead: `FloatCurve` keeps its keys and grows `AsCurve()` returning the
delegate. One line, no migration, no new type.

Bezier is worth its ~20 lines (Newton iteration on x, like CSS `cubic-bezier`) because designers
hand over bezier control points and today there is nowhere to type them.

### Layer 1 — `Motion`, the missing primitive

One mutable struct, one file, roughly 150 lines. No interface, no subclasses, no allocation:

```csharp
public enum MotionKind { Duration, Spring, Decay }

/// <summary>Immutable description of how a value should travel. Cheap to copy, safe as a field.</summary>
public readonly record struct MotionSpec(MotionKind Kind, float A, float B, Func<float, float>? Curve)
{
    public static MotionSpec Duration(float seconds, Func<float, float>? curve = null);
    /// <param name="response">Approximate settle period in seconds.</param>
    /// <param name="damping">1 = critically damped (no overshoot), &lt;1 = bouncy.</param>
    public static MotionSpec Spring(float response = 0.35f, float damping = 1f);
    /// <param name="friction">Per-second exponential velocity decay (the fling model).</param>
    public static MotionSpec Decay(float friction = 3.2f);

    public static MotionSpec Smooth  { get; } // response .35, damping 1
    public static MotionSpec Snappy  { get; } // response .22, damping .85
    public static MotionSpec Bouncy  { get; } // response .45, damping .65
}

/// <summary>A 1-D value in motion: position, velocity, target. Tick returns false once settled.</summary>
public struct Motion
{
    public float Value;
    public float Velocity;
    public float Target;
    public MotionSpec Spec;

    public bool Tick(float dt);
    /// <summary>Aim somewhere new WITHOUT discarding velocity — the whole reason this type exists.</summary>
    public void Retarget(float target);
    public void SnapTo(float value);
}
```

The parameterisation is SwiftUI's (`response`, `dampingFraction`), not raw stiffness/damping,
because response is a duration a designer can reason about: ω = 2π/response, ζ = damping, and the
usual `stiffness = ω²`, `damping = 2ζω` fall out. `Curves.Spring`'s existing constants become one
of the presets rather than the only option.

Three integration details that are not optional:

- **Fixed internal substep.** The spring integrates semi-implicit Euler at a fixed 1/240 s internal
  step, consuming `dt` in whole substeps and carrying the remainder. Without it, stiff springs go
  unstable at low frame rates and — worse — behave differently at 60 and 144 Hz, which breaks the
  determinism the fixed-step gameplay loop exists to provide.
- **Settle test on both terms.** Done when `|Target − Value| < ε` *and* `|Velocity| < εv`, then
  snap exactly to `Target`. A spring that never reports done keeps `Ticker.AnyActive` true and
  holds the event loop awake forever — the idle gate makes this a battery bug, not a cosmetic one.
- **`Duration` mode derives velocity** from the curve slope so a duration animation interrupted by a
  spring hands over a sane velocity. That is the one line that makes mixed retargeting work.

`AnimationController` keeps its entire public surface and becomes a transport over a
`MotionSpec.Duration` motion; adding `Spec` lets any of the 40 files opt into a spring by assigning
one property. **Deletion:** `SmoothScroller`'s fling and ease constants become `MotionSpec.Decay`
and `MotionSpec.Spring` — the hand-rolled integrator goes, and its fling velocity can hand off to
the settle animation instead of being clamped to zero at the edge.

**Rejected: an `ISimulation` interface with a class per kind** (Flutter's
`Simulation`/`SpringSimulation`/`FrictionSimulation`). Three kinds, all under 30 lines of maths, all
needing to live in a struct field of a widget without an allocation per animation. A `switch` on
three enum cases is the smaller, faster, and more debuggable version. If a fourth kind arrives with
genuinely different state, revisit.

### Layer 2 — lerp once, for every type

`Tween<T>` is four classes for four types while `Vec3.Lerp`, `Quat.Slerp` and `Transform3D.Lerp`
already exist in `Zigote.Core/Math3D`. The lazy unification is a static-abstract interface so
generic animation code stays allocation-free and unboxed:

```csharp
public interface ILerp<T> { static abstract T Lerp(T a, T b, float t); }
```

implemented on the value types themselves (`Color`, `Offset`, `Size`, `Rect`, `Vec2`, `Vec3`,
`Quat` → slerp, `Transform3D`). `TweenAnimationBuilder<T>` and friends constrain on it; the four
`Tween<T>` subclasses are deleted once their 13 call sites move. Cost is mostly `git rm`.

### Layer 3 — transport, and naming the clock

`AnimationController` gains three properties and no new concepts:

| Addition | Why |
|---|---|
| `float Speed` | Per-animation rate. Already exists on `AnimationPlayer` and `SpriteAnimator`; the widget controller is the odd one out |
| `static float TimeScale` | Global slow-mo for debugging motion, and the hook a DevTools slider binds to |
| `static bool ReduceMotion` | OS "reduce motion" preference: durations collapse to 0 and springs snap. Accessibility, so it is not deferred. `Skeleton`'s local switch folds into it |

And a documented rule, enforced by nothing but reviewed for: **presentation animations tick on
`Ticker`; anything a gameplay script can observe ticks on the fixed clock.** `AnimationPlayer` grows
an explicit `Clock` marker so a clip attached to an entity is stepped by `GameSession`'s fixed loop
and a cutscene clip by the render loop.

### Layer 4 — poses and blending

Today `AnimationPlayer.ApplyTo` writes `SceneNode.Position/Rotation/Scale` directly. Two players
over one subtree overwrite each other, and there is no crossfade when switching clips — the
character teleports into the new pose.

The fix reuses the allocation-free `SampleInto` that already exists:

```csharp
/// A weighted accumulation of sampled poses, applied to the scene once per tick.
public sealed class PoseBuffer
{
    public void Add(AnimationClip clip, float time, float weight);      // blend (weights normalise)
    public void AddAdditive(AnimationClip clip, float time, float weight, AnimationClip reference);
    public void ApplyTo(SceneNode root);   // single writer — the only thing that touches transforms
    public void Clear();
}
```

`AnimationPlayer` grows `CrossFadeTo(clip, seconds)`, which is two sampled clips and a `Motion`
driving the weight — the layer-1 primitive paying for itself. Additive layers (a recoil clip on top
of a locomotion clip) are the same buffer with a reference pose subtracted. Translation and scale
blend linearly; rotation blends by successive `Quat.Slerp` in accumulation order, which is correct
for two clips and acceptable for more (documented ceiling; NLERP-of-many is a known wart, and
nothing in this codebase blends four clips yet).

**Clip events** land here too: an ordered `(time, name)` list on the clip, and a player that walks
the interval `[previousTime, time)` so no event is skipped on a large dt — the algorithm
`SpriteAnimator` already implements and got right. Same semantics, both dimensions.

### Layer 5 — the widget surface

Three additions, everything else is composition on `Animate`:

- **`Hero`** — a shared-element transition. Two widgets with the same tag on the outgoing and
  incoming route; the `Navigator` already owns the route pair and its transition controller
  (`Route.Transition`), so the work is: collect tagged bounds before the route swap, fly a proxy
  in an overlay layer along the interpolated rect, hide the endpoints while in flight. The overlay
  layer is the piece that does not exist yet and needs the paint pipeline's opinion.
- **`.Stagger(perChild)`** on `Animate` over a children list — a per-index `Interval` curve.
  Twenty lines once layer 0 has `Interval`.
- **Motion-aware implicit widgets** — `ImplicitlyAnimatedWidget` swaps its restarting controller for
  a `Motion` per animated value. That single change makes `AnimatedContainer`, `AnimatedPadding`,
  `AnimatedAlign`, `AnimatedOpacity` and `AnimatedSize` interruptible with no API change and no
  call-site churn. It is the highest-visibility item in the whole document.

---

## Feature catalogue

What "feature rich" means concretely. Status is against the tree today.

### Motion primitives

| Feature | Status |
|---|---|
| Duration + easing curve | ✅ `AnimationController` + `Curves` |
| Forward / reverse / toggle / repeat / ping-pong / seek | ✅ |
| Status edges (`OnCompleted`, `OnDismissed`, `OnTick`) | ✅ |
| Delta clamping across hitches | ✅ `MaxFrameDelta` |
| Cubic-bezier easing from control points | ✅ `Curves.Bezier` |
| `Interval` / `Flipped` / curve chaining | ✅ `Curves.Easing.cs` |
| Keyed curves shared with VFX | ✅ `FloatCurve.AsCurve()` |
| Physical spring with real overshoot | ✅ `MotionSpec.Spring` |
| Velocity-preserving retarget | ✅ `Motion.Retarget` |
| Decay / fling as a first-class motion | ✅ `MotionSpec.Decay` — `SmoothScroller` not yet converted to it |
| Per-animation `Speed`, global `TimeScale` | ✅ |
| Reduce-motion honoured globally | ✅ `AnimationController.ReduceMotion` (indicators exempt) |
| Gesture hand-off (drag velocity → spring settle) | ✅ `AnimationController.Velocity` is settable; `Seek` keeps it |

### UI

| Feature | Status |
|---|---|
| Implicit: opacity, align, padding, container, size, switcher | ✅ `Zigote.UI/Widgets/Transitions/` |
| Explicit: fade, scale, slide, builder, tween-builder | ✅ |
| Declarative chains with delay/duration/curve inheritance and `Then` | ✅ `Animate` |
| Effects: fade, scale, move, slide, shake | ✅ `AnimateEffects.cs` |
| Duration literals (`500.ms`) | ✅ |
| Route push/pop transitions | ✅ `Route`/`PageRoute` |
| Scroll ease + fling | ✅ `SmoothScroller` |
| Skeleton / shimmer placeholder | ✅ `Skeleton` |
| Named presets (`PopIn`, `FadeInUp`, `Attention`, …) | ✅ `AnimatePresets` |
| Ambient loops (`Pulse`, `Breathe`, `Spin`) | ✅ `Animate.Loop` |
| Interruptible implicit animations | ✅ `ImplicitlyAnimatedWidget.Spec` |
| Staggered entrance over children | ✅ `children.Stagger()` |
| Hero / shared-element | 🔨 Phase 4 |
| Animated list insert/remove/reorder | 🔨 Phase 4 |
| Scroll-linked motion (parallax, collapsing header) | ⚪ Already possible — `Animate.Target` takes any float, including a normalised scroll offset. Needs a documented recipe, not code |
| Rotation effects | ✅ `Rotate`/`Wiggle` — blur and colour-matrix stay blocked on renderer primitives |
| Shared-axis / container-transform patterns | ⚪ Compositions of Hero + route transitions once Phase 4 lands |

### 2D and 3D

| Feature | Status |
|---|---|
| Sprite frame clips, loop modes, chaining, frame events | ✅ `SpriteAnimator` |
| glTF keyframe import | ✅ `GltfAnimationImporter` |
| Clip sampling: step, linear, shortest-path quaternion | ✅ `AnimationChannel` |
| Allocation-free per-frame sampling | ✅ `SampleInto` |
| Editor transport + scrubber | ✅ `TimelinePanel` |
| Particle curves over lifetime (CPU + GPU-baked) | ✅ `Zigote.Vfx` |
| Cubic-spline keyframe interpolation | 🔨 Phase 3 — currently approximated as linear (noted in the source) |
| Crossfade between clips | 🔨 Phase 3 |
| Weighted pose blending + additive layers | 🔨 Phase 3 |
| Time-tagged clip events | 🔨 Phase 3 |
| Camera moves driven by a clip | ⚪ Falls out of Phase 3 + `Zigote.Cinematics` — a `SceneNode` is a `SceneNode` |
| Morph-target weights applied | ⛔ Data imports; applying needs engine support |
| Skinning / skeletons / IK | ⛔ Engine-side, out of scope |
| Root motion | ⛔ Deferred until a character controller exists to want it |
| Blend trees / state machine | ⛔ Deferred (see [not building](#deliberately-not-building)) |

Legend: ✅ exists · 🔨 designed here · ⚪ composition, needs docs not code · ⛔ out of scope.

---

## API sketches

What a user writes after Phase 1 and Phase 4. Existing code keeps compiling unchanged.

```csharp
// Interruptible by default — retarget mid-flight and the velocity carries through.
new AnimatedContainer(child: content) {
    Width  = expanded ? 480f : 240f,
    Motion = MotionSpec.Snappy,
};

// The same spec drives a scene node.
boom.AnimatePosition(to: shotTarget, spec: MotionSpec.Smooth, clock: AnimationClock.Fixed);

// A fling handing its velocity to a settling spring, instead of clamping to zero at the edge.
scroller.Fling(velocity: gesture.VelocityY);   // MotionSpec.Decay → MotionSpec.Spring at the bound

// Staggered entrance, one Interval curve per index.
new Column(children: rows)
    .Animate()
    .FadeIn(duration: 240.ms)
    .Move(begin: new Offset(x: 0f, y: 12f))
    .Stagger(perChild: 40.ms);

// A designer's easing, typed in as given.
Curve = Curves.Bezier(x1: 0.2f, y1: 0f, x2: 0f, y2: 1f);

// 3D: crossfade locomotion, add a recoil layer on top, and hear the footsteps.
player.CrossFadeTo(clip: run, seconds: 0.15f);
player.AddAdditive(clip: recoil, weight: 0.6f, reference: idlePose);
player.ClipEvent += name => { if (name == "footstep") Audio.Play("step"); };
```

---

## Invariants (anything here must not break)

1. **Zero allocation in the steady state.** `FrameHotPathAllocationTests` pins `Ticker.AdvanceAll`.
   `Motion` is a struct held by value; `MotionSpec` is a readonly record struct; the spring
   substep loop allocates nothing. A `PoseBuffer` reuses its dictionaries the way `AnimationPlayer`
   already reuses `_poseScratch`.
2. **The idle gate stays closed.** `App.cs:1297` sleeps the event loop when `!Ticker.AnyActive`. Any
   motion must *stop* its ticker when settled — including a spring that asymptotes. Settle
   thresholds are part of the contract, not a tuning detail.
3. **Damage stays bounded.** `App.cs:1456` full-repaints when a ticker is active but nothing marked.
   New animations mark their own region, or they cost a full window clear per frame.
4. **Detach → re-attach rebinds.** `ImplicitlyAnimatedWidget.Attach` restores both the ticker *and*
   the `OnTick` subscription; the comment there records the bug from getting it half-right.
   `Motion`-based widgets inherit the same requirement.
5. **The fixed clock stays deterministic.** No `MaxFrameDelta` clamping and no wall-clock reads
   inside a fixed-step motion; springs substep at a fixed rate so 60 Hz and 144 Hz agree.
6. **One writer per scene-node transform per tick.** After Phase 3, that writer is `PoseBuffer.ApplyTo`.
7. **Public API is additive.** 40 files use `AnimationController` and 64 sites use `Curves`; both
   keep their shape. Deletions are limited to the four `Tween<T>` subclasses and `SmoothScroller`'s
   private integrator.

---

## Phasing

| Phase | Contents | Rough size | Unlocks |
|---|---|---|---|
| **1 — Motion** ✅ | `MotionSpec`/`Motion`, controller `Spec`/`Speed`/`TimeScale`/`ReduceMotion`, curve combinators, `FloatCurve.AsCurve`, `ImplicitlyAnimatedWidget.Spec`, rotation effects, presets + `Stagger`, Gallery pages | ~1150 added | Interruption everywhere, springs, gesture hand-off, accessibility. **Outstanding:** the `SmoothScroller` conversion |
| **2 — Lerp** | `ILerp<T>` on the value types, generic tween path, delete four `Tween<T>` subclasses | ~120 added, ~60 deleted | `Vec3`/`Quat`/`Transform3D` animate through the same code as `Color` |
| **3 — Clips** | `PoseBuffer`, crossfade, additive layer, clip events, cubic-spline sampling, explicit clock on `AnimationPlayer` | ~400 | Characters, cutscenes, camera moves |
| **4 — Surface** | `Hero` + overlay layer, `.Stagger()`, animated list | ~350 | The transitions people notice |

Phases 1 and 3 are independent; 2 is a cleanup that can slip; 4 depends only on 1 and is now
unblocked — `Stagger` shipped early with Phase 1 because `Interval` made it four lines.

Each phase ships with the check that fails if the logic breaks: Phase 1 a spring-settles-and-stops
test plus an allocation guard, Phase 3 an event-never-skipped test mirroring the sprite one, Phase 4
a Gallery page (the Adwaita gallery already has `AnimationsPage`).

---

## Deliberately not building

| Not building | Why | What would change the answer |
|---|---|---|
| **Animation state machine / blend-tree graph editor** | `Zigote.Graphs.*` could host it and it would look impressive. Nothing in the repo has two clips to blend yet | A game project with a character controller that outgrows `CrossFadeTo` |
| **Property-binding tracks** (clip keys arbitrary properties by path) | Reflection-or-codegen plumbing for a generality no consumer asked for. TRS + weights covers glTF, which is what the importer produces | The editor timeline wants to key a material colour or a light intensity |
| **Skinning, IK, morph apply** | Engine-side (Zig + wgpu), a different document, a different skill set | The engine grows skinned-mesh support |
| **Rive / Lottie runtimes** | File-format work with no demand, and both duplicate what `Animate` + SVG already do for UI motion | A design team hands over Lottie files |
| **A second scheduler or an animation thread** | The frame is already one dependency chain and the render-thread work is the correct place to spend that risk — see [`parallelism-simd-and-atomics.md`](parallelism-simd-and-atomics.md) | Never, independently of that work |
| **An `AnimatedX` widget per property** (Flutter's explosion) | `Animate` + `Motion` covers the same ground declaratively; six implicit widgets is enough | A property that genuinely cannot be expressed as an effect |
| **A curve editor UI** | VFX already has one over the same stops | Curves become editable on widgets, which is a designer-tooling question, not this one |
| **Timeline serialisation v2** | The glTF-shaped clip is the format; the editor round-trips it | Clips need non-TRS tracks, i.e. the deferred item above |

---

## Measurement

The machine's variance rule applies: cross-run A/B below ~20% is noise, so variants interleave in
one process (`Zigote.Runtime.Benchmark` is the pattern).

| What | How |
|---|---|
| Steady-state allocation | Extend `FrameHotPathAllocationTests` with a spring-driven widget and a `PoseBuffer` blend |
| Idle gating | A test asserting `Ticker.AnyActive` goes false within N ticks of a spring reaching its target — the battery regression guard |
| Spring stability | Integrate the same spring at 1/30, 1/60, 1/144 and assert the settle time matches within tolerance |
| Blend cost | Sample + blend 4 clips × 60 nodes, per frame, against today's single-clip apply |
| Perceived smoothness | The Gallery animations page under `ZIGOTE_SHOT`, before/after, for the interruption case specifically |
