namespace Zigote.Render2D;

public enum SpriteLoopMode
{
    Loop = 0,
    Once = 1,
    PingPong = 2,
}

/// <summary>
///     An ordered run of sprite frames with per-frame durations, a loop mode, and optional named
///     events attached to frames (footsteps, hitbox windows). Pure data —
///     <see cref="SpriteAnimator" />
///     plays it.
/// </summary>
public sealed class SpriteClip
{
    private readonly float[] _durations;
    private readonly List<string>?[] _events;
    private readonly SpriteFrame[] _frames;
    private readonly float[] _starts;

    public SpriteClip(string name, IReadOnlyList<SpriteFrame> frames, float fps,
        SpriteLoopMode loop = SpriteLoopMode.Loop, string? nextClip = null)
        : this(
            name,
            frames,
            UniformDurations(frames.Count, fps),
            loop,
            nextClip
        )
    {
    }

    public SpriteClip(string name, IReadOnlyList<SpriteFrame> frames,
        IReadOnlyList<float> durations,
        SpriteLoopMode loop = SpriteLoopMode.Loop, string? nextClip = null)
    {
        if (durations.Count != frames.Count)
            throw new ArgumentException(
                $"Clip '{name}' has {frames.Count} frames but {durations.Count} durations.",
                nameof(durations)
            );

        Name = name;
        Loop = loop;
        NextClip = nextClip;
        _frames = [.. frames];
        _durations = [.. durations];
        _events = new List<string>?[_frames.Length];
        _starts = new float[_frames.Length];

        // A zero-duration frame would stall the animator's frame-advance loop, so reject at build
        // time — clips come from import tooling, not the hot path.
        var total = 0f;
        for (var i = 0; i < _durations.Length; i++)
        {
            if (_durations[i] <= 0f)
                throw new ArgumentException(
                    $"Clip '{name}' frame {i} has non-positive duration {_durations[i]}.",
                    nameof(durations)
                );
            _starts[i] = total;
            total += _durations[i];
        }

        Duration = total;
    }

    public string Name { get; }
    public SpriteLoopMode Loop { get; set; }

    /// <summary>Clip to auto-play when a <see cref="SpriteLoopMode.Once" /> clip completes.</summary>
    public string? NextClip { get; set; }

    public IReadOnlyList<SpriteFrame> Frames => _frames;
    public IReadOnlyList<float> Durations => _durations;
    public int FrameCount => _frames.Length;

    /// <summary>Total clip length in seconds (one forward pass).</summary>
    public float Duration { get; }

    public SpriteFrame FrameAt(int index)
    {
        return _frames[index];
    }

    public float DurationAt(int index)
    {
        return _durations[index];
    }

    /// <summary>Start time of a frame on the clip's forward timeline.</summary>
    internal float FrameStart(int index)
    {
        return _starts[index];
    }

    public void AddEvent(int frameIndex, string eventName)
    {
        if ((uint)frameIndex >= (uint)_frames.Length)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        (_events[frameIndex] ??= []).Add(eventName);
    }

    /// <summary>Events attached to a frame in add order; null when the frame has none.</summary>
    public IReadOnlyList<string>? EventsAt(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)_frames.Length)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return _events[frameIndex];
    }

    private static float[] UniformDurations(int count, float fps)
    {
        if (fps <= 0f)
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "fps must be positive.");
        var durations = new float[count];
        Array.Fill(durations, 1f / fps);
        return durations;
    }
}
