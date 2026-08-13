using Xunit;
using Zigote.Render2D;

namespace Zigote.Tests;

public class SpriteAnimationTests
{
    // ── Tick: frame stepping + event delivery ────────────────────────────────

    [Fact]
    public void Tick_LargeDt_EntersEverySkippedFrame_AndFiresTheirEventsInOrder()
    {
        var clip = new SpriteClip("run", Frames(6), 10f);
        clip.AddEvent(0, "start");
        clip.AddEvent(1, "step");
        clip.AddEvent(2, "windup");
        clip.AddEvent(4, "hitbox");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("run");
        Assert.Equal(["start"], fired); // entering frame 0 via Play fires immediately

        // 0.45 s at 10 fps crosses frames 1, 2, 3, 4 in one Tick — none may be skipped.
        animator.Tick(0.45f);

        Assert.Equal(4, animator.CurrentFrameIndex);
        Assert.Equal(Frames(6)[4], animator.CurrentFrame);
        Assert.Equal(["start", "step", "windup", "hitbox"], fired);
        Assert.False(animator.IsFinished);
    }

    [Fact]
    public void Loop_WrapReFiresEventsOnTheNextPass()
    {
        var clip = new SpriteClip("cycle", Frames(3), 10f);
        clip.AddEvent(0, "loopstart");
        clip.AddEvent(2, "end");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("cycle");
        animator.Tick(0.35f); // f1, f2 (end), wrap to f0 (loopstart again)

        Assert.Equal(0, animator.CurrentFrameIndex);
        Assert.Equal(["loopstart", "end", "loopstart"], fired);
        Assert.Equal(0.05f, animator.Time, 4); // Time wrapped with the frames
    }

    [Fact]
    public void Once_ClampsOnLastFrame_AndSetsIsFinished()
    {
        var clip = new SpriteClip(
            "die",
            Frames(3),
            10f,
            SpriteLoopMode.Once
        );
        clip.AddEvent(2, "done");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("die");
        animator.Tick(1f);

        Assert.True(animator.IsFinished);
        Assert.Equal(2, animator.CurrentFrameIndex);
        Assert.Equal(clip.Duration, animator.Time, 5);
        Assert.Equal(["done"], fired);

        animator.Tick(0.5f); // finished → no-op, no re-fire
        Assert.Equal(2, animator.CurrentFrameIndex);
        Assert.Equal(clip.Duration, animator.Time, 5);
        Assert.Equal(["done"], fired);
    }

    [Fact]
    public void Once_WithNextClip_TransitionsAndFiresItsFrameZeroEvents()
    {
        var attack = new SpriteClip(
            "attack",
            Frames(2),
            10f,
            SpriteLoopMode.Once,
            "idle"
        );
        attack.AddEvent(1, "swing");
        var idle = new SpriteClip("idle", Frames(2), 10f);
        idle.AddEvent(0, "idle-start");

        var animator = new SpriteAnimator([attack, idle]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("attack");
        animator.Tick(0.25f); // 0.2 s finishes attack, 0.05 s residual carries into idle

        Assert.Equal("idle", animator.CurrentClip);
        Assert.Equal(0, animator.CurrentFrameIndex);
        Assert.False(animator.IsFinished);
        Assert.Equal(["swing", "idle-start"], fired);
        Assert.Equal(0.05f, animator.Time, 4);
    }

    [Fact]
    public void PingPong_TurnsWithoutDoubleFiringEndpoints_AndFiresOnTheReversePass()
    {
        var clip = new SpriteClip(
            "sway",
            Frames(3),
            10f,
            SpriteLoopMode.PingPong
        );
        clip.AddEvent(0, "e0");
        clip.AddEvent(1, "e1");
        clip.AddEvent(2, "e2");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("sway");
        var indexes = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            animator.Tick(0.1f);
            indexes.Add(animator.CurrentFrameIndex);
        }

        Assert.Equal([1, 2, 1, 0, 1, 2], indexes);
        Assert.Equal(["e0", "e1", "e2", "e1", "e0", "e1", "e2"], fired);
    }

    [Fact]
    public void PingPong_LargeDt_CrossesTheTurnInOneTick()
    {
        var clip = new SpriteClip(
            "sway",
            Frames(3),
            10f,
            SpriteLoopMode.PingPong
        );
        clip.AddEvent(0, "e0");
        clip.AddEvent(1, "e1");
        clip.AddEvent(2, "e2");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("sway");
        animator.Tick(0.35f); // f1, f2, turn, back to f1 — the endpoint fires exactly once

        Assert.Equal(1, animator.CurrentFrameIndex);
        Assert.Equal(["e0", "e1", "e2", "e1"], fired);
    }

    // ── Play semantics ───────────────────────────────────────────────────────

    [Fact]
    public void Play_SameClip_DoesNotRestart_UnlessRequested()
    {
        var clip = new SpriteClip("walk", Frames(3), 10f);
        clip.AddEvent(0, "go");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("walk");
        animator.Tick(0.15f);
        Assert.Equal(1, animator.CurrentFrameIndex);

        animator.Play("walk"); // restartIfSame: false → no-op
        Assert.Equal(1, animator.CurrentFrameIndex);
        Assert.Equal(["go"], fired);

        animator.Play("walk", true);
        Assert.Equal(0, animator.CurrentFrameIndex);
        Assert.Equal(0f, animator.Time, 5);
        Assert.Equal(["go", "go"], fired);
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
        var clip = new SpriteClip("held", Frames(3), [0.05f, 0.2f, 0.05f]);
        clip.AddEvent(1, "mid");
        clip.AddEvent(2, "tail");
        Assert.Equal(0.3f, clip.Duration, 5);

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add;

        animator.Play("held");
        animator.Tick(0.06f); // past frame 0's short 0.05 s
        Assert.Equal(1, animator.CurrentFrameIndex);
        animator.Tick(0.18f); // 0.19 s into the long 0.2 s frame — still held
        Assert.Equal(1, animator.CurrentFrameIndex);
        animator.Tick(0.02f);
        Assert.Equal(2, animator.CurrentFrameIndex);
        Assert.Equal(["mid", "tail"], fired);
    }

    // ── Event queue polling ──────────────────────────────────────────────────

    [Fact]
    public void ConsumeEvents_DrainsTheQueue_AndClearsTheResultsList()
    {
        var clip = new SpriteClip("combo", Frames(2), 10f);
        clip.AddEvent(0, "a");
        clip.AddEvent(0, "b"); // multiple events per frame, add order kept
        clip.AddEvent(1, "c");

        var animator = new SpriteAnimator([clip]);
        var fired = new List<string>();
        animator.FrameEvent += fired.Add; // both delivery paths see every event

        animator.Play("combo");
        animator.Tick(0.15f);

        var results = new List<string> { "junk" };
        Assert.Equal(3, animator.ConsumeEvents(results));
        Assert.Equal(["a", "b", "c"], results);
        Assert.Equal(["a", "b", "c"], fired);

        Assert.Equal(0, animator.ConsumeEvents(results));
        Assert.Empty(results);
    }

    // ── Guards ───────────────────────────────────────────────────────────────

    [Fact]
    public void Tick_NoClip_EmptyClip_AndNonPositiveDt_AreNoOps()
    {
        var animator = new SpriteAnimator();
        animator.Tick(0.1f); // no clip
        Assert.Null(animator.CurrentClip);
        Assert.Equal(SpriteFrame.Full, animator.CurrentFrame);
        Assert.Equal(0f, animator.Time);

        animator.AddClip(new SpriteClip("empty", [], 10f));
        animator.Play("empty");
        animator.Tick(0.1f); // empty clip
        Assert.Equal(SpriteFrame.Full, animator.CurrentFrame);

        animator.AddClip(new SpriteClip("walk", Frames(3), 10f));
        animator.Play("walk");
        animator.Tick(0.05f);
        var time = animator.Time;
        animator.Tick(0f);
        animator.Tick(-1f);
        Assert.Equal(time, animator.Time);
        Assert.Equal(0, animator.CurrentFrameIndex);
    }

    [Fact]
    public void SpriteClip_RejectsInvalidConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpriteClip("c", Frames(2), 0f));
        Assert.Throws<ArgumentException>(() => new SpriteClip("c", Frames(2), [0.1f]));
        Assert.Throws<ArgumentException>(() => new SpriteClip("c", Frames(2), [0.1f, 0f]));

        var clip = new SpriteClip("c", Frames(2), 10f);
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.AddEvent(2, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.AddEvent(-1, "x"));
    }

    // ── Zero allocation ──────────────────────────────────────────────────────

    [Fact]
    public void Tick_SteadyState_AllocatesZero()
    {
        var animator = new SpriteAnimator([new SpriteClip("run", Frames(8), 60f)]);
        animator.FrameEvent += static _ => { };
        animator.Play("run");

        // Warm up past tiered JIT; the loop wraps many times over 200 ticks.
        for (var i = 0; i < 200; i++) Frame(animator);

        const int ticks = 500;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ticks; i++) Frame(animator);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated == 0,
            $"SpriteAnimator.Tick allocated {allocated} B over {ticks} ticks " +
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
        for (var i = 0; i < count; i++)
            frames[i] = new SpriteFrame(
                i * 0.1f,
                0f,
                i * 0.1f + 0.1f,
                1f,
                16,
                16
            );
        return frames;
    }
}
