using Xunit;
using Zigote.Render2D;

namespace Zigote.Tests;

public class SpriteAnimationTests
{
    // ── Tick: frame stepping + event delivery ────────────────────────────────

    [Fact]
    public void Tick_LargeDt_EntersEverySkippedFrame_AndFiresTheirEventsInOrder()
    {
        var clip = new SpriteClip(name: "run", frames: Frames(6), fps: 10f);
        clip.AddEvent(frameIndex: 0, eventName: "start");
        clip.AddEvent(frameIndex: 1, eventName: "step");
        clip.AddEvent(frameIndex: 2, eventName: "windup");
        clip.AddEvent(frameIndex: 4, eventName: "hitbox");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("run");
        Assert.Equal(
            expected: ["start"],
            actual: fired
        ); // entering frame 0 via Play fires immediately

        // 0.45 s at 10 fps crosses frames 1, 2, 3, 4 in one Tick — none may be skipped.
        animator.Tick(0.45f);

        Assert.Equal(expected: 4, actual: animator.CurrentFrameIndex);
        Assert.Equal(expected: Frames(6)[4], actual: animator.CurrentFrame);
        Assert.Equal(expected: ["start", "step", "windup", "hitbox"], actual: fired);
        Assert.False(animator.IsFinished);
    }

    [Fact]
    public void Loop_WrapReFiresEventsOnTheNextPass()
    {
        var clip = new SpriteClip(name: "cycle", frames: Frames(3), fps: 10f);
        clip.AddEvent(frameIndex: 0, eventName: "loopstart");
        clip.AddEvent(frameIndex: 2, eventName: "end");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("cycle");
        animator.Tick(0.35f); // f1, f2 (end), wrap to f0 (loopstart again)

        Assert.Equal(expected: 0, actual: animator.CurrentFrameIndex);
        Assert.Equal(expected: ["loopstart", "end", "loopstart"], actual: fired);
        Assert.Equal(
            expected: 0.05f,
            actual: animator.Time,
            precision: 4
        ); // Time wrapped with the frames
    }

    [Fact]
    public void Once_ClampsOnLastFrame_AndSetsIsFinished()
    {
        var clip = new SpriteClip(
            name: "die",
            frames: Frames(3),
            fps: 10f,
            loop: SpriteLoopMode.Once
        );
        clip.AddEvent(frameIndex: 2, eventName: "done");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("die");
        animator.Tick(1f);

        Assert.True(animator.IsFinished);
        Assert.Equal(expected: 2, actual: animator.CurrentFrameIndex);
        Assert.Equal(expected: clip.Duration, actual: animator.Time, precision: 5);
        Assert.Equal(expected: ["done"], actual: fired);

        animator.Tick(0.5f); // finished → no-op, no re-fire
        Assert.Equal(expected: 2, actual: animator.CurrentFrameIndex);
        Assert.Equal(expected: clip.Duration, actual: animator.Time, precision: 5);
        Assert.Equal(expected: ["done"], actual: fired);
    }

    [Fact]
    public void Once_WithNextClip_TransitionsAndFiresItsFrameZeroEvents()
    {
        var attack = new SpriteClip(
            name: "attack",
            frames: Frames(2),
            fps: 10f,
            loop: SpriteLoopMode.Once,
            nextClip: "idle"
        );
        attack.AddEvent(frameIndex: 1, eventName: "swing");
        var idle = new SpriteClip(name: "idle", frames: Frames(2), fps: 10f);
        idle.AddEvent(frameIndex: 0, eventName: "idle-start");

        var animator = new SpriteAnimator([attack, idle]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("attack");
        animator.Tick(0.25f); // 0.2 s finishes attack, 0.05 s residual carries into idle

        Assert.Equal(expected: "idle", actual: animator.CurrentClip);
        Assert.Equal(expected: 0, actual: animator.CurrentFrameIndex);
        Assert.False(animator.IsFinished);
        Assert.Equal(expected: ["swing", "idle-start"], actual: fired);
        Assert.Equal(expected: 0.05f, actual: animator.Time, precision: 4);
    }

    [Fact]
    public void PingPong_TurnsWithoutDoubleFiringEndpoints_AndFiresOnTheReversePass()
    {
        var clip = new SpriteClip(
            name: "sway",
            frames: Frames(3),
            fps: 10f,
            loop: SpriteLoopMode.PingPong
        );
        clip.AddEvent(frameIndex: 0, eventName: "e0");
        clip.AddEvent(frameIndex: 1, eventName: "e1");
        clip.AddEvent(frameIndex: 2, eventName: "e2");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("sway");
        var indexes = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            animator.Tick(0.1f);
            indexes.Add(animator.CurrentFrameIndex);
        }

        Assert.Equal(expected: [1, 2, 1, 0, 1, 2], actual: indexes);
        Assert.Equal(expected: ["e0", "e1", "e2", "e1", "e0", "e1", "e2"], actual: fired);
    }

    [Fact]
    public void PingPong_LargeDt_CrossesTheTurnInOneTick()
    {
        var clip = new SpriteClip(
            name: "sway",
            frames: Frames(3),
            fps: 10f,
            loop: SpriteLoopMode.PingPong
        );
        clip.AddEvent(frameIndex: 0, eventName: "e0");
        clip.AddEvent(frameIndex: 1, eventName: "e1");
        clip.AddEvent(frameIndex: 2, eventName: "e2");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("sway");
        animator.Tick(0.35f); // f1, f2, turn, back to f1 — the endpoint fires exactly once

        Assert.Equal(expected: 1, actual: animator.CurrentFrameIndex);
        Assert.Equal(expected: ["e0", "e1", "e2", "e1"], actual: fired);
    }

    // ── Play semantics ───────────────────────────────────────────────────────

    [Fact]
    public void Play_SameClip_DoesNotRestart_UnlessRequested()
    {
        var clip = new SpriteClip(name: "walk", frames: Frames(3), fps: 10f);
        clip.AddEvent(frameIndex: 0, eventName: "go");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("walk");
        animator.Tick(0.15f);
        Assert.Equal(expected: 1, actual: animator.CurrentFrameIndex);

        animator.Play("walk"); // restartIfSame: false → no-op
        Assert.Equal(expected: 1, actual: animator.CurrentFrameIndex);
        Assert.Equal(expected: ["go"], actual: fired);

        animator.Play(name: "walk", restartIfSame: true);
        Assert.Equal(expected: 0, actual: animator.CurrentFrameIndex);
        Assert.Equal(expected: 0f, actual: animator.Time, precision: 5);
        Assert.Equal(expected: ["go", "go"], actual: fired);
    }

    [Fact]
    public void Play_UnknownClip_Throws()
    {
        var animator = new SpriteAnimator();
        Assert.Throws<ArgumentException>(() => animator.Play("nope"));
    }

    // ── Variable durations ───────────────────────────────────────────────────

    [Fact]
    public void VariableFrameDurations_AreHonored()
    {
        var clip = new SpriteClip(name: "held", frames: Frames(3), durations: [0.05f, 0.2f, 0.05f]);
        clip.AddEvent(frameIndex: 1, eventName: "mid");
        clip.AddEvent(frameIndex: 2, eventName: "tail");
        Assert.Equal(expected: 0.3f, actual: clip.Duration, precision: 5);

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("held");
        animator.Tick(0.06f); // past frame 0's short 0.05 s
        Assert.Equal(expected: 1, actual: animator.CurrentFrameIndex);
        animator.Tick(0.18f); // 0.19 s into the long 0.2 s frame — still held
        Assert.Equal(expected: 1, actual: animator.CurrentFrameIndex);
        animator.Tick(0.02f);
        Assert.Equal(expected: 2, actual: animator.CurrentFrameIndex);
        Assert.Equal(expected: ["mid", "tail"], actual: fired);
    }

    // ── Event queue polling ──────────────────────────────────────────────────

    [Fact]
    public void ConsumeEvents_DrainsTheQueue_AndClearsTheResultsList()
    {
        var clip = new SpriteClip(name: "combo", frames: Frames(2), fps: 10f);
        clip.AddEvent(frameIndex: 0, eventName: "a");
        clip.AddEvent(frameIndex: 0, eventName: "b"); // multiple events per frame, add order kept
        clip.AddEvent(frameIndex: 1, eventName: "c");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add; // both delivery paths see every event

        animator.Play("combo");
        animator.Tick(0.15f);

        var results = new List<string> { "junk" };
        Assert.Equal(expected: 3, actual: animator.ConsumeEvents(results));
        Assert.Equal(expected: ["a", "b", "c"], actual: results);
        Assert.Equal(expected: ["a", "b", "c"], actual: fired);

        Assert.Equal(expected: 0, actual: animator.ConsumeEvents(results));
        Assert.Empty(results);
    }

    // ── Guards ───────────────────────────────────────────────────────────────

    [Fact]
    public void Tick_NoClip_EmptyClip_AndNonPositiveDt_AreNoOps()
    {
        var animator = new SpriteAnimator();
        animator.Tick(0.1f); // no clip
        Assert.Null(animator.CurrentClip);
        Assert.Equal(expected: SpriteFrame.Full, actual: animator.CurrentFrame);
        Assert.Equal(expected: 0f, actual: animator.Time);

        animator.AddClip(new SpriteClip(name: "empty", frames: [], fps: 10f));
        animator.Play("empty");
        animator.Tick(0.1f); // empty clip
        Assert.Equal(expected: SpriteFrame.Full, actual: animator.CurrentFrame);

        animator.AddClip(new SpriteClip(name: "walk", frames: Frames(3), fps: 10f));
        animator.Play("walk");
        animator.Tick(0.05f);
        float time = animator.Time;
        animator.Tick(0f);
        animator.Tick(-1f);
        Assert.Equal(expected: time, actual: animator.Time);
        Assert.Equal(expected: 0, actual: animator.CurrentFrameIndex);
    }

    [Fact]
    public void SpriteClip_RejectsInvalidConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpriteClip(
                name: "c",
                frames: Frames(2),
                fps: 0f
            )
        );
        Assert.Throws<ArgumentException>(() => new SpriteClip(
                name: "c",
                frames: Frames(2),
                durations: [0.1f]
            )
        );
        Assert.Throws<ArgumentException>(() => new SpriteClip(
                name: "c",
                frames: Frames(2),
                durations: [0.1f, 0f]
            )
        );

        var clip = new SpriteClip(name: "c", frames: Frames(2), fps: 10f);
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.AddEvent(
                frameIndex: 2,
                eventName: "x"
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.AddEvent(
                frameIndex: -1,
                eventName: "x"
            )
        );
    }

    // ── Zero allocation ──────────────────────────────────────────────────────

    [Fact]
    public void Tick_SteadyState_AllocatesZero()
    {
        var animator =
            new SpriteAnimator([new SpriteClip(name: "run", frames: Frames(8), fps: 60f)]);
        animator.FrameEvent += static _ => { };
        animator.Play("run");

        // Warm up past tiered JIT; the loop wraps many times over 200 ticks.
        for (int i = 0; i < 200; i++) Frame(animator);

        const int ticks = 500;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < ticks; i++) Frame(animator);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            condition: allocated == 0,
            userMessage: $"SpriteAnimator.Tick allocated {allocated} B over {ticks} ticks " +
                         $"({allocated / (double)ticks:F2} B/tick); expected 0."
        );

        static void Frame(SpriteAnimator animator)
        {
            animator.Tick(1f / 90f);
            _ = animator.CurrentFrame;
            _ = animator.Time;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SpriteFrame[] Frames(int count)
    {
        var frames = new SpriteFrame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = new SpriteFrame(
                U0: i * 0.1f,
                V0: 0f,
                U1: (i * 0.1f) + 0.1f,
                V1: 1f,
                PixelWidth: 16,
                PixelHeight: 16
            );
        }

        return frames;
    }
}
