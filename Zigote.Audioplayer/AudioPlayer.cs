using Zigote.Core.Engine;
using Zigote.Core.State;

namespace Zigote.Audioplayer;

/// <summary>
///     A media player: a queue, a transport, and the state a UI binds to. just_audio's
///     <c>AudioPlayer</c> in C# — play/pause/seek, <see cref="LoopMode" />, shuffle, gapless advance,
///     clipping, a position/buffered/duration surface — over <see cref="IAudioApi" />, which is the
///     *only* thing it talks to. That seam is the point: everything here is a state machine, so the
///     whole player runs in CI against a fake device.
///     <para>
///         Where just_audio exposes Dart <c>Stream</c>s, this exposes <see cref="Signal{T}" />s, like
///         <c>Zigote.Videoplayer</c>. Read-only signals report (<see cref="State" />,
///         <see cref="Position" />…); the writable ones (<see cref="Volume" />, <see cref="Speed" />,
///         <see cref="Loop" />…) <b>drive</b> the player, so a bound control and a keyboard shortcut
///         take exactly the same path.
///     </para>
///     <para>
///         <b>Nothing here runs on its own.</b> The host calls <see cref="Tick" /> once per frame —
///         that is when the cursor is read, buffering is judged, the queue advances and the gapless
///         successor is armed. A player is not a thread, and a frame loop is already a heartbeat.
///         Call it from one thread; the signals it writes may be read from any.
///     </para>
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly IAudioApi _audio;
    private readonly Signal<TimeSpan> _buffered = new(TimeSpan.Zero);
    private readonly Signal<int> _currentIndex = new(-1);
    private readonly Signal<TimeSpan?> _duration = new(null);
    private readonly Signal<string?> _error = new(null);
    private readonly List<AudioSource> _items = [];

    /// <summary>Playback order as positions into <see cref="_items" />. Shuffle rewrites it; nothing else does.</summary>
    private readonly List<int> _order = [];

    private readonly Signal<TimeSpan> _position = new(TimeSpan.Zero);
    private readonly Signal<IReadOnlyList<AudioSource>> _sequence = new([]);
    private readonly Signal<PlaybackState> _state = new(PlaybackState.Idle);
    private readonly List<IDisposable> _subscriptions = [];

    private bool _disposed;
    private Equalizer? _equalizer;

    /// <summary>The armed successor: already decoding, start already scheduled. 0 when none.</summary>
    private uint _next;

    private int _nextPos = -1;
    private int _pos = -1;

    /// <summary>Source-time a seek is waiting to land on; -1 when no seek is in flight.</summary>
    private float _seekGuard = -1f;

    private int _seekGuardTicks;
    private int _shuffleSeed;
    private uint _sound;

    /// <summary>The caller asked for sound. Distinct from <see cref="State" />, which also reports buffering.</summary>
    private bool _wantPlay;

    public AudioPlayer(IAudioApi audio)
    {
        _audio = audio;
        _shuffleSeed = Random.Shared.Next();

        _subscriptions.Add(Volume.Subscribe(_ => ApplyGain()));
        _subscriptions.Add(Muted.Subscribe(_ => ApplyGain()));
        _subscriptions.Add(Speed.Subscribe(_ => ApplyRate()));
        _subscriptions.Add(Shuffle.Subscribe(_ => Reorder()));
        // A successor scheduled under the old loop mode is no longer the successor.
        _subscriptions.Add(Loop.Subscribe(mode =>
            {
                if (mode == LoopMode.One) DropNext();
            }
        ));
    }

    // ── knobs: writing these is how you drive the player ─────────────────────

    /// <summary>Gain applied to every item, on top of each source's <see cref="AudioSource.GainDb" />.</summary>
    public Signal<float> Volume { get; } = new(1f);

    /// <summary>Silence without losing the <see cref="Volume" /> the user had set.</summary>
    public Signal<bool> Muted { get; } = new(false);

    /// <summary>
    ///     Playback rate, clamped to [0.25, 4]. <b>Varispeed: pitch follows rate</b> — the engine
    ///     resamples rather than time-stretching, so this is a turntable, not a podcast app's 1.5×.
    /// </summary>
    public Signal<double> Speed { get; } = new(1.0);

    public Signal<LoopMode> Loop { get; } = new(LoopMode.Off);

    /// <summary>Play the queue in a shuffled order, keeping whatever is playing where it is.</summary>
    public Signal<bool> Shuffle { get; } = new(false);

    // ── tuning ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     How early the successor is created and scheduled. Two frames is not enough and two minutes
    ///     is two decoders open for a minute; two seconds is the compromise, and it is a knob because
    ///     the right answer depends on how slow the disk is.
    /// </summary>
    public TimeSpan GaplessLead { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Decoded audio a starved stream must gather back before it counts as playing again. Without
    ///     the margin the transport flickers Playing/Buffering once a frame on a marginal connection.
    /// </summary>
    public TimeSpan RebufferThreshold { get; set; } = TimeSpan.FromSeconds(0.5);

    /// <summary>
    ///     Past this point <see cref="SeekToPrevious" /> restarts the current item instead of going
    ///     back one — the convention every transport bar follows (and media3's default, to the second).
    /// </summary>
    public TimeSpan MaxSeekToPreviousPosition { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    ///     Chain every item is routed through, or null for dry. The player does not own it: dispose it
    ///     yourself, and detach it here first if it outlives its bands.
    /// </summary>
    public Equalizer? Equalizer
    {
        get => _equalizer;
        set
        {
            _equalizer = value;
            var id = value?.Id ?? 0;
            if (_sound != 0) _audio.SetEq(_sound, id);
            if (_next != 0) _audio.SetEq(_next, id);
        }
    }

    // ── what is true now ─────────────────────────────────────────────────────

    /// <summary>Where the player is. The whole transport UI can bind to just this.</summary>
    public IReadableSignal<PlaybackState> State => _state;

    /// <summary>Cursor within the current item, clip-relative and unaffected by <see cref="Speed" />.</summary>
    public IReadableSignal<TimeSpan> Position => _position;

    /// <summary>How far ahead the decoded audio reaches — the second, paler bar under the seek bar.</summary>
    public IReadableSignal<TimeSpan> BufferedPosition => _buffered;

    /// <summary>Length of the current item, or null when it has none (a live stream).</summary>
    public IReadableSignal<TimeSpan?> Duration => _duration;

    /// <summary>Index into <see cref="Sequence" /> of what is loaded; -1 when nothing is.</summary>
    public IReadableSignal<int> CurrentIndex => _currentIndex;

    /// <summary>The queue as it was given — not shuffle order, which is <see cref="EffectiveIndices" />.</summary>
    public IReadableSignal<IReadOnlyList<AudioSource>> Sequence => _sequence;

    /// <summary>Why the last load or stream failed, or null. Cleared by the next successful load.</summary>
    public IReadableSignal<string?> Error => _error;

    /// <summary>What is loaded right now, or null.</summary>
    public AudioSource? Current => _pos >= 0 && _pos < _order.Count ? _items[_order[_pos]] : null;

    /// <summary>Whether the transport is advancing — buffering counts, since the intent is to play.</summary>
    public bool IsPlaying => _state.Value is PlaybackState.Playing or PlaybackState.Buffering;

    /// <summary>Fraction of the way through the current item, 0–1. Zero when the length is unknown.</summary>
    public double Progress => _duration.Value is { TotalSeconds: > 0 } d
        ? Math.Clamp(_position.Value.TotalSeconds / d.TotalSeconds, 0, 1)
        : 0;

    /// <summary>Queue indices in play order — just_audio's <c>shuffleIndices</c>. This is "up next".</summary>
    public IReadOnlyList<int> EffectiveIndices => _order;

    /// <summary>What <see cref="SeekToNext" /> would play, or null at the end of the queue.</summary>
    public int? NextIndex
    {
        get
        {
            var np = NextOrderPos();
            return np >= 0 ? _order[np] : null;
        }
    }

    /// <summary>What <see cref="SeekToPrevious" /> would play, ignoring the restart-current rule.</summary>
    public int? PreviousIndex
    {
        get
        {
            if (_order.Count == 0) return null;
            if (_pos > 0) return _order[_pos - 1];
            return Loop.Value == LoopMode.All && _order.Count > 1 ? _order[^1] : null;
        }
    }

    public bool HasNext => NextIndex is not null;

    public bool HasPrevious => PreviousIndex is not null;

    /// <summary>Release the decoders and the queue. Idempotent; every call afterwards is a no-op.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();

        Unload();
        // An empty queue is what makes every other method a no-op from here: no order, no sound,
        // nothing to guard with a `_disposed` check at the top of a dozen entry points.
        _items.Clear();
        _order.Clear();
        _sequence.Value = [];
        ResetIdle();
    }

    // ── queue ────────────────────────────────────────────────────────────────

    /// <summary>Replace the queue with a single source — just_audio's <c>setAudioSource</c>.</summary>
    public void SetAudioSource(AudioSource source, TimeSpan? initialPosition = null)
    {
        SetAudioSources([source], 0, initialPosition);
    }

    /// <summary>
    ///     Replace the queue. With no <paramref name="initialIndex" />, whatever is playing keeps
    ///     playing if it is still in the list — which is what makes this the only queue-editing method
    ///     there is: append, remove or reorder around the current track and hand the list back.
    /// </summary>
    public void SetAudioSources(IEnumerable<AudioSource> sources, int? initialIndex = null,
        TimeSpan? initialPosition = null)
    {
        var currentItem = Current;
        _items.Clear();
        _items.AddRange(sources);
        _sequence.Value = [.. _items];

        var target = initialIndex ?? IndexOfSame(currentItem);
        if (target < 0 || target >= _items.Count) target = _items.Count > 0 ? 0 : -1;

        var keepPlaying = initialIndex is null && initialPosition is null && _sound != 0
                          && currentItem is not null && target >= 0 && _items[target] == currentItem;

        RebuildOrder(target);
        DropNext(); // whatever was armed may no longer be next

        if (keepPlaying)
        {
            _currentIndex.Value = target;
            return;
        }

        if (target < 0)
        {
            Unload();
            ResetIdle();
            return;
        }

        if (LoadFrom(_pos, initialPosition ?? TimeSpan.Zero, true) && _wantPlay) StartCurrent();
    }

    /// <summary>Deal a new shuffle order. Pass a <paramref name="seed" /> to make it reproducible.</summary>
    public void Reshuffle(int? seed = null)
    {
        _shuffleSeed = seed ?? Random.Shared.Next();
        Reorder();
    }

    // ── transport ────────────────────────────────────────────────────────────

    /// <summary>Start, resume, or — when the queue has run out — replay the item it ended on.</summary>
    public void Play()
    {
        if (_order.Count == 0) return;
        _wantPlay = true;

        if (_sound == 0 && !LoadFrom(_pos >= 0 ? _pos : 0, TimeSpan.Zero, true)) return;
        if (_state.Peek() == PlaybackState.Ended) SeekCurrent(TimeSpan.Zero);
        StartCurrent();
    }

    /// <summary>
    ///     Hold position. The engine only knows stop-and-rewind, so a pause is a stop plus a seek back
    ///     to where the cursor was — read before the stop, because after it there is nothing to read.
    /// </summary>
    public void Pause()
    {
        _wantPlay = false;
        DropNext();
        if (_sound == 0) return;

        var cursor = _audio.Cursor(_sound);
        _audio.Stop(_sound);
        if (cursor > 0f) _audio.Seek(_sound, cursor);
        if (_state.Peek() is not (PlaybackState.Idle or PlaybackState.Ended or PlaybackState.Failed))
            _state.Value = PlaybackState.Paused;
    }

    /// <summary>Play if paused, pause if playing — the space bar, in one call.</summary>
    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    /// <summary>Stop and release the decoder, keeping the queue and its position in it.</summary>
    public void Stop()
    {
        _wantPlay = false;
        Unload();
        _position.Value = TimeSpan.Zero;
        _buffered.Value = TimeSpan.Zero;
        _state.Value = PlaybackState.Idle;
    }

    /// <summary>
    ///     Move the cursor, optionally to another item. <paramref name="position" /> is clip-relative,
    ///     so a clipped source seeks in its own timeline like the standalone file it pretends to be.
    /// </summary>
    public void Seek(TimeSpan position, int? index = null)
    {
        if (_order.Count == 0) return;

        var target = index is { } i ? _order.IndexOf(i) : _pos;
        if (target < 0) return;

        if (target != _pos || _sound == 0)
        {
            if (LoadFrom(target, position, false) && _wantPlay) StartCurrent();
            return;
        }

        SeekCurrent(position);
    }

    /// <summary>Seek by a delta — the ±10 s keys, without the clamping arithmetic at the call site.</summary>
    public void SeekBy(TimeSpan delta)
    {
        Seek(_position.Peek() + delta);
    }

    /// <summary>Next item, wrapping only under <see cref="LoopMode.All" />. No-op at the end otherwise.</summary>
    public void SeekToNext()
    {
        var np = NextOrderPos();
        if (np >= 0) MoveTo(np, false);
    }

    /// <summary>
    ///     Restart this item, or step back one if it only just started — the double-press-to-go-back
    ///     behaviour, governed by <see cref="MaxSeekToPreviousPosition" />.
    /// </summary>
    public void SeekToPrevious()
    {
        if (_order.Count == 0) return;

        if (_position.Peek() > MaxSeekToPreviousPosition || PreviousIndex is null)
        {
            SeekCurrent(TimeSpan.Zero);
            return;
        }

        MoveTo(_pos > 0 ? _pos - 1 : _order.Count - 1, false);
    }

    // ── push streams ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Feed an <see cref="AudioSource.Stream" /> item container bytes, returning how many were
    ///     taken. A short count means its queue is full — stop reading the socket until it drains.
    /// </summary>
    public int Push(ReadOnlySpan<byte> bytes)
    {
        return _sound != 0 && Current is { IsStream: true } ? _audio.StreamPush(_sound, bytes) : 0;
    }

    /// <summary>No more bytes are coming. What is buffered plays out, then the queue advances.</summary>
    public void FinishStream()
    {
        if (_sound != 0 && Current is { IsStream: true }) _audio.StreamFinish(_sound);
    }

    // ── the frame ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Pump the player: publish the cursor, judge buffering, advance the queue, arm the successor.
    ///     Cheap and allocation-free, and the signals are equality-gated, so a paused player notifies
    ///     nobody.
    /// </summary>
    public void Tick()
    {
        if (_sound == 0 || Current is not { } src) return;
        if (_state.Peek() is PlaybackState.Ended or PlaybackState.Failed) return;

        var cursor = _audio.Cursor(_sound);
        if (cursor >= 0f && SeekSettled(cursor))
            _position.Value = TimeSpan.FromSeconds(
                MathF.Max(0f, cursor - (float)src.Start.TotalSeconds));

        if (src.IsStream)
        {
            if (!TickStream()) return;
        }
        else
        {
            // A streamed file's length is unknown until its header is parsed; publish it once it is.
            if (_duration.Peek() is null) _duration.Value = DurationOf(_sound, src);
            _buffered.Value = _duration.Peek() ?? _position.Peek();
            if (_state.Peek() == PlaybackState.Opening)
                _state.Value = _wantPlay ? PlaybackState.Playing : PlaybackState.Ready;
        }

        var elapsed = _position.Peek();
        var duration = _duration.Peek();
        if (_audio.AtEnd(_sound) || (duration is { } d && elapsed >= d))
        {
            OnItemEnd();
            return;
        }

        if (_wantPlay && _next == 0 && Loop.Value != LoopMode.One && duration is { } total)
        {
            var lead = (float)((total - elapsed).TotalSeconds / Rate());
            var np = NextOrderPos();
            if (lead <= GaplessLead.TotalSeconds && np >= 0) ArmNext(np, lead);
        }
    }

    /// <summary>Push-stream health. False when it took the player somewhere the caller must not continue from.</summary>
    private bool TickStream()
    {
        var ahead = MathF.Max(0f, _audio.StreamBuffered(_sound));
        _buffered.Value = _position.Peek() + TimeSpan.FromSeconds(ahead);

        switch (_audio.StreamStatus(_sound))
        {
            case AudioStreamState.Connecting:
                _state.Value = PlaybackState.Opening;
                return true;

            case AudioStreamState.Unsupported:
                // Nothing downstream can decode this. Advancing would just spin on the next frame.
                _wantPlay = false;
                Fail("unsupported stream format");
                return false;

            case AudioStreamState.Ended:
                return true; // AtEnd advances the queue on this same tick

            default:
                if (!_wantPlay) return true;
                var state = _state.Peek();
                if (ahead <= 0f) _state.Value = PlaybackState.Buffering;
                else if (state == PlaybackState.Buffering)
                {
                    if (ahead >= RebufferThreshold.TotalSeconds)
                        _state.Value = PlaybackState.Playing;
                }
                else if (state != PlaybackState.Playing)
                {
                    _state.Value = PlaybackState.Playing;
                }

                return true;
        }
    }

    private void OnItemEnd()
    {
        if (Loop.Value == LoopMode.One)
        {
            SeekCurrent(TimeSpan.Zero);
            if (_wantPlay) StartCurrent();
            return;
        }

        var np = NextOrderPos();
        if (np < 0)
        {
            DropNext();
            _wantPlay = false;
            _position.Value = _duration.Peek() ?? _position.Peek();
            _state.Value = PlaybackState.Ended;
            return;
        }

        if (_next != 0 && _nextPos == np) AdoptNext();
        else MoveTo(np, true);
    }

    // ── sources ──────────────────────────────────────────────────────────────

    private void MoveTo(int orderPos, bool auto)
    {
        if (LoadFrom(orderPos, TimeSpan.Zero, auto) && _wantPlay) StartCurrent();
    }

    /// <summary>
    ///     Load <paramref name="orderPos" />, or — when <paramref name="skipFailures" /> — the first
    ///     item after it that opens. One dead file in a library should cost a gap, not the rest of the
    ///     queue; a queue where every file is dead stops after one pass rather than spinning.
    /// </summary>
    private bool LoadFrom(int orderPos, TimeSpan offset, bool skipFailures)
    {
        for (var attempt = 0; attempt <= _order.Count; attempt++)
        {
            if (Load(orderPos, offset)) return true;
            if (!skipFailures) return false;

            orderPos = NextOrderPos(); // Load moved _pos, so this is the failed item's successor
            if (orderPos < 0) return false;
            offset = TimeSpan.Zero;
        }

        return false;
    }

    private bool Load(int orderPos, TimeSpan offset)
    {
        Unload();
        if (orderPos < 0 || orderPos >= _order.Count) return false;

        _pos = orderPos;
        var src = _items[_order[orderPos]];
        _currentIndex.Value = _order[orderPos];
        _state.Value = PlaybackState.Opening;

        // ponytail: CreateFile parses the container header on the calling thread — a visible stutter
        // on a slow disk. Upgrade is Zigote.Core.Threading.Background, installing the id on arrival.
        _sound = src.IsStream ? _audio.CreateStream() : _audio.CreateFile(src.Path!, src.Streaming);
        if (_sound == 0)
        {
            Fail($"cannot open {src.Path ?? "stream"}");
            return false;
        }

        _error.Value = null;
        _audio.SetSpatial(_sound, false); // music is not a point in a world
        _audio.SetVolume(_sound, Gain(src));
        _audio.SetRate(_sound, Rate());
        if (_equalizer is { Id: not 0 }) _audio.SetEq(_sound, _equalizer.Id);

        if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
        var sourceTime = src.Start + offset;
        if (sourceTime > TimeSpan.Zero) _audio.Seek(_sound, (float)sourceTime.TotalSeconds);
        _seekGuard = -1f; // a fresh decoder has no stale cursor to guard against

        _position.Value = offset;
        _buffered.Value = offset;
        _duration.Value = DurationOf(_sound, src);
        _state.Value = src.IsStream ? PlaybackState.Opening : PlaybackState.Ready;
        return true;
    }

    private void StartCurrent()
    {
        if (_sound == 0) return;
        _audio.Play(_sound);
        // A stream that has not identified its container yet is still Opening; Tick promotes it.
        if (_state.Peek() != PlaybackState.Opening) _state.Value = PlaybackState.Playing;
    }

    private void Unload()
    {
        DropNext();
        if (_sound == 0) return;
        _audio.Stop(_sound);
        _audio.Destroy(_sound);
        _sound = 0;
    }

    /// <summary>
    ///     Create the successor and schedule it to start the instant this one runs out. Polling can
    ///     never be tighter than a frame; the audio clock can hit the sample, which is the whole
    ///     difference between gapless and nearly-gapless.
    /// </summary>
    private void ArmNext(int orderPos, float secondsFromNow)
    {
        var src = _items[_order[orderPos]];
        if (src.IsStream) return; // nothing to open ahead of time — the bytes have not arrived

        var id = _audio.CreateFile(src.Path!, src.Streaming);
        if (id == 0) return; // it will fail loudly through the normal path when its turn comes

        _audio.SetSpatial(id, false);
        _audio.SetVolume(id, Gain(src));
        _audio.SetRate(id, Rate());
        if (_equalizer is { Id: not 0 }) _audio.SetEq(id, _equalizer.Id);
        if (src.Start > TimeSpan.Zero) _audio.Seek(id, (float)src.Start.TotalSeconds);
        _audio.ScheduleStart(id, MathF.Max(0f, secondsFromNow));

        _next = id;
        _nextPos = orderPos;
    }

    /// <summary>The armed successor is already making sound; promote it rather than starting it again.</summary>
    private void AdoptNext()
    {
        if (_sound != 0)
        {
            _audio.Stop(_sound);
            _audio.Destroy(_sound);
        }

        _sound = _next;
        _pos = _nextPos;
        _next = 0;
        _nextPos = -1;
        _seekGuard = -1f;

        var src = _items[_order[_pos]];
        _currentIndex.Value = _order[_pos];
        _position.Value = TimeSpan.Zero;
        _buffered.Value = TimeSpan.Zero;
        _duration.Value = DurationOf(_sound, src);
        _state.Value = PlaybackState.Playing;
    }

    private void DropNext()
    {
        if (_next == 0) return;
        _audio.Stop(_next);
        _audio.Destroy(_next);
        _next = 0;
        _nextPos = -1;
    }

    private void SeekCurrent(TimeSpan position)
    {
        if (_sound == 0 || Current is not { } src) return;
        DropNext();

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (_duration.Peek() is { } d && position > d) position = d;

        var sourceTime = (float)(src.Start + position).TotalSeconds;
        _audio.Seek(_sound, sourceTime);
        _seekGuard = sourceTime;
        _seekGuardTicks = 0;
        _position.Value = position;

        if (_state.Peek() is PlaybackState.Ended or PlaybackState.Failed)
            _state.Value = _wantPlay ? PlaybackState.Playing : PlaybackState.Ready;
    }

    /// <summary>
    ///     After a seek a decoder can report the old cursor for a frame or two, and publishing it snaps
    ///     the seek bar out from under the user's finger — the most-reported bug in every media UI. The
    ///     stale reading is behind the target on a forward seek and ahead of it on a backward one, so
    ///     the test is distance, not direction: trust the cursor once it is near where it was sent, or
    ///     after a second of it refusing to go there.
    /// </summary>
    private bool SeekSettled(float cursor)
    {
        if (_seekGuard < 0f) return true;
        if (MathF.Abs(cursor - _seekGuard) > 1f && ++_seekGuardTicks <= 60) return false;

        _seekGuard = -1f;
        _seekGuardTicks = 0;
        return true;
    }

    // ── order ────────────────────────────────────────────────────────────────

    private void Reorder()
    {
        RebuildOrder(_pos >= 0 && _pos < _order.Count ? _order[_pos] : -1);
        DropNext();
    }

    /// <summary>Rebuild <see cref="_order" /> around <paramref name="centreItem" />, an index into <see cref="_items" />.</summary>
    private void RebuildOrder(int centreItem)
    {
        _order.Clear();
        for (var i = 0; i < _items.Count; i++) _order.Add(i);

        if (Shuffle.Value)
        {
            // One seed for the player's life, so editing the queue reshuffles deterministically
            // instead of dealing a brand-new order every time a track is appended.
            // ponytail: the order still shifts when the count changes. Splicing new items into the
            // existing order at random points is the upgrade if that ever grates.
            var rng = new Random(_shuffleSeed);
            for (var i = _order.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }

            // Whatever is playing goes to the front, so shuffling does not cut the current track off.
            if (centreItem >= 0)
            {
                var at = _order.IndexOf(centreItem);
                if (at > 0) (_order[0], _order[at]) = (_order[at], _order[0]);
            }
        }

        _pos = centreItem >= 0 ? _order.IndexOf(centreItem) : -1;
    }

    /// <summary>The order position that follows the current one, or -1 when the queue ends here.</summary>
    private int NextOrderPos()
    {
        if (_order.Count == 0) return -1;
        if (_pos + 1 < _order.Count) return _pos + 1;
        return Loop.Value == LoopMode.All ? 0 : -1;
    }

    /// <summary>
    ///     Find <paramref name="item" /> in the queue, by reference first — that is what keeps the same
    ///     file twice in one playlist from collapsing into one entry when the queue is edited.
    /// </summary>
    private int IndexOfSame(AudioSource? item)
    {
        if (item is null) return -1;
        var byReference = _items.FindIndex(s => ReferenceEquals(s, item));
        return byReference >= 0 ? byReference : _items.IndexOf(item);
    }

    // ── state ────────────────────────────────────────────────────────────────

    /// <summary>Clip length when the source names one, otherwise what the decoder knows (null = unknown).</summary>
    private TimeSpan? DurationOf(uint id, AudioSource src)
    {
        if (src.End is { } end)
        {
            var clipped = end - src.Start;
            return clipped > TimeSpan.Zero ? clipped : TimeSpan.Zero;
        }

        var total = id != 0 ? _audio.Duration(id) : -1f;
        if (total < 0f) return null;
        var remaining = TimeSpan.FromSeconds(total) - src.Start;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Gain actually handed to the mixer: the volume knob, mute, and the item's own tag.</summary>
    private float Gain(AudioSource src)
    {
        if (Muted.Value) return 0f;
        var volume = MathF.Max(0f, Volume.Value);
        return src.GainDb != 0f ? volume * MathF.Pow(10f, src.GainDb / 20f) : volume;
    }

    private float Rate()
    {
        return (float)Math.Clamp(Speed.Value, 0.25, 4.0);
    }

    private void ApplyGain()
    {
        if (_sound != 0 && Current is { } src) _audio.SetVolume(_sound, Gain(src));
        if (_next != 0 && _nextPos >= 0) _audio.SetVolume(_next, Gain(_items[_order[_nextPos]]));
    }

    private void ApplyRate()
    {
        if (_sound != 0) _audio.SetRate(_sound, Rate());
        // The successor's start was scheduled in wall-clock seconds at the old rate; that hand-off
        // is now wrong by however much the rate moved. Drop it and let Tick re-arm.
        DropNext();
    }

    private void Fail(string message)
    {
        _error.Value = message;
        DropNext();
        if (_sound != 0) _audio.Stop(_sound);
        _state.Value = PlaybackState.Failed;
    }

    private void ResetIdle()
    {
        _pos = -1;
        _currentIndex.Value = -1;
        _position.Value = TimeSpan.Zero;
        _buffered.Value = TimeSpan.Zero;
        _duration.Value = null;
        _wantPlay = false;
        _state.Value = PlaybackState.Idle;
    }
}
