namespace Zigote.Render2D;

/// <summary>
///     Plays <see cref="SpriteClip" />s frame by frame. <see cref="Tick" /> steps through every
///     frame the elapsed time crosses — never skipping one — so frame events (hitboxes, footsteps)
///     fire exactly once per entered frame even when one large dt spans several frames. Steady-state
///     ticking with no events firing allocates zero bytes.
/// </summary>
public sealed class SpriteAnimator
{
    private readonly Dictionary<string, SpriteClip> _clips = new();
    private readonly List<string> _pending = [];

    private SpriteClip? _clip;
    private int _direction = 1;
    private float _timeInFrame;

    public SpriteAnimator()
    {
    }

    public SpriteAnimator(IEnumerable<SpriteClip> clips)
    {
        foreach (var clip in clips) AddClip(clip);
    }

    public string? CurrentClip => _clip?.Name;
    public int CurrentFrameIndex { get; private set; }

    public SpriteFrame CurrentFrame =>
        _clip == null || _clip.FrameCount == 0
            ? SpriteFrame.Full
            : _clip.FrameAt(CurrentFrameIndex);

    /// <summary>True once a <see cref="SpriteLoopMode.Once" /> clip with no NextClip has completed.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>
    ///     Playhead position on the current clip's forward 0..Duration timeline: wraps on Loop,
    ///     clamps to Duration on a finished Once, and decreases during a PingPong reverse pass.
    /// </summary>
    public float Time
    {
        get
        {
            var clip = _clip;
            if (clip == null || clip.FrameCount == 0) return 0f;
            return _direction > 0
                ? clip.FrameStart(CurrentFrameIndex) + _timeInFrame
                : clip.FrameStart(CurrentFrameIndex) + clip.DurationAt(CurrentFrameIndex) -
                  _timeInFrame;
        }
    }

    /// <summary>
    ///     Fired as frames are entered, in traversal order. Also queued for
    ///     <see cref="ConsumeEvents" />.
    /// </summary>
    public event Action<string>? FrameEvent;

    public void AddClip(SpriteClip clip)
    {
        _clips[clip.Name] = clip;
    }

    public void Play(string name, bool restartIfSame = false)
    {
        if (!_clips.TryGetValue(name, out var clip))
            throw new ArgumentException($"Unknown clip '{name}'.", nameof(name));
        if (!restartIfSame && ReferenceEquals(_clip, clip)) return;
        Start(clip);
    }

    public void Tick(float dt)
    {
        if (_clip == null || _clip.FrameCount == 0 || dt <= 0f || IsFinished) return;

        _timeInFrame += dt;
        while (_timeInFrame >= _clip.DurationAt(CurrentFrameIndex))
        {
            _timeInFrame -= _clip.DurationAt(CurrentFrameIndex);
            if (!Advance()) break;
        }
    }

    /// <summary>
    ///     Moves queued frame events into <paramref name="results" /> (cleared first) and empties
    ///     the queue. Returns the number of events delivered — the polling alternative to
    ///     <see cref="FrameEvent" />.
    /// </summary>
    public int ConsumeEvents(List<string> results)
    {
        results.Clear();
        var count = _pending.Count;
        for (var i = 0; i < count; i++) results.Add(_pending[i]);
        _pending.Clear();
        return count;
    }

    private void Start(SpriteClip clip)
    {
        _clip = clip;
        CurrentFrameIndex = 0;
        _timeInFrame = 0f;
        _direction = 1;
        IsFinished = false;
        if (clip.FrameCount > 0) FireEvents(0);
    }

    /// <summary>
    ///     Enters the next frame in the traversal. False when playback clamped (frame consumed no
    ///     time).
    /// </summary>
    private bool Advance()
    {
        var clip = _clip!;
        var last = clip.FrameCount - 1;

        switch (clip.Loop)
        {
            case SpriteLoopMode.Loop:
                CurrentFrameIndex = CurrentFrameIndex == last ? 0 : CurrentFrameIndex + 1;
                FireEvents(CurrentFrameIndex);
                return true;

            case SpriteLoopMode.Once:
                if (CurrentFrameIndex < last)
                {
                    CurrentFrameIndex++;
                    FireEvents(CurrentFrameIndex);
                    return true;
                }

                if (clip.NextClip != null && _clips.TryGetValue(clip.NextClip, out var next))
                {
                    // The residual time carries into the next clip so chained clips stay frame-exact.
                    _clip = next;
                    CurrentFrameIndex = 0;
                    _direction = 1;
                    if (next.FrameCount == 0)
                    {
                        _timeInFrame = 0f;
                        return false;
                    }

                    FireEvents(0);
                    return true;
                }

                IsFinished = true;
                _timeInFrame = clip.DurationAt(last); // clamp: Time reads as Duration
                return false;

            case SpriteLoopMode.PingPong:
            default:
                if (last == 0)
                {
                    FireEvents(0); // single-frame ping-pong degenerates to a loop wrap
                    return true;
                }

                // The endpoint frame is entered once per visit: the turn flips direction and steps
                // straight to its neighbour, never re-entering (and re-firing) the endpoint.
                if (_direction > 0 && CurrentFrameIndex == last) _direction = -1;
                else if (_direction < 0 && CurrentFrameIndex == 0) _direction = 1;
                CurrentFrameIndex += _direction;
                FireEvents(CurrentFrameIndex);
                return true;
        }
    }

    private void FireEvents(int frame)
    {
        var events = _clip!.EventsAt(frame);
        if (events == null) return;
        for (var i = 0; i < events.Count; i++)
        {
            var name = events[i];
            _pending.Add(name);
            FrameEvent?.Invoke(name);
        }
    }
}
