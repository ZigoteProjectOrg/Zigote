using Xunit;
using Zigote.Audioplayer;
using Zigote.Core.Engine;

namespace Zigote.Tests;

/// <summary>
///     The player without a sound card. <see cref="FakeAudioApi" /> is a decoder that never decodes:
///     ids, cursors and durations are bookkeeping, and time only moves when a test says
///     <see cref="FakeAudioApi.Advance" />. This is what the <see cref="IAudioApi" /> seam was for.
/// </summary>
public class AudioPlayerTests
{
    private static AudioSource[] Three()
    {
        return
        [
            AudioSource.File("a.flac"),
            AudioSource.File("b.flac"),
            AudioSource.File("c.flac"),
        ];
    }

    [Fact]
    public void Loading_A_Queue_Parks_It_Ready()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);

        player.SetAudioSources(Three());
        Assert.Equal(0, player.CurrentIndex.Value);
        Assert.Equal(PlaybackState.Ready, player.State.Value);
        Assert.Equal(TimeSpan.FromSeconds(10), player.Duration.Value);
        Assert.False(player.IsPlaying);

        player.Play();
        Assert.Equal(PlaybackState.Playing, player.State.Value);
        Assert.True(api.IsPlaying(api.LastCreated));
    }

    [Fact]
    public void Tick_Publishes_Position_And_Advances_At_End()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Play();

        api.Advance(3f);
        player.Tick();
        Assert.Equal(TimeSpan.FromSeconds(3), player.Position.Value);
        Assert.Equal(0.3, player.Progress, 3);

        api.Advance(7f); // the 10s file is done
        player.Tick();
        Assert.Equal(1, player.CurrentIndex.Value);
        Assert.Equal(PlaybackState.Playing, player.State.Value);
    }

    [Fact]
    public void Gapless_Arms_The_Successor_And_Adopts_It()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Play();

        api.Advance(8.5f); // inside the 2s arming window
        player.Tick();
        var armed = Assert.Single(api.Scheduled); // created and scheduled, not started by polling

        api.Advance(1.5f);
        player.Tick();
        Assert.Equal(1, player.CurrentIndex.Value);
        Assert.DoesNotContain(armed, api.Destroyed); // adopted, not thrown away and recreated
        Assert.Equal(TimeSpan.Zero, player.Position.Value);
    }

    [Fact]
    public void Speed_Drives_The_Mixer_Rate_And_The_Arming_Window()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Play();
        player.Speed.Value = 2.0;

        Assert.Equal(2f, api.RateOf[api.LastCreated]);

        api.Advance(7f); // 3s of source left, but only 1.5s of wall clock at 2x
        player.Tick();
        Assert.Single(api.Scheduled);
    }

    [Fact]
    public void Pause_Stops_And_Restores_The_Cursor()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSource(AudioSource.File("a.flac"));
        player.Play();

        api.Advance(4f);
        player.Tick();
        player.TogglePlayPause();

        Assert.Equal(PlaybackState.Paused, player.State.Value);
        Assert.False(api.IsPlaying(api.LastCreated));
        Assert.Equal(4f, api.Cursor(api.LastCreated), 3); // stop rewound it; the pause seeked back

        player.TogglePlayPause();
        Assert.Equal(PlaybackState.Playing, player.State.Value);
        Assert.Equal(4f, api.Cursor(api.LastCreated), 3);
    }

    [Fact]
    public void A_Stale_Cursor_After_A_Seek_Never_Moves_The_Bar_Backwards()
    {
        var api = new FakeAudioApi { LagSeeks = true };
        using var player = new AudioPlayer(api);
        player.SetAudioSource(AudioSource.File("a.flac"));
        player.Play();
        api.Advance(8f);
        player.Tick();

        player.Seek(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position.Value);

        player.Tick(); // decoder still reports 8s
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position.Value);

        api.Advance(0f); // the seek lands
        player.Tick();
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position.Value);
    }

    [Fact]
    public void LoopOne_Repeats_And_LoopAll_Wraps()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Loop.Value = LoopMode.One;
        player.Play();

        api.Advance(10f);
        player.Tick();
        Assert.Equal(0, player.CurrentIndex.Value);
        Assert.Equal(TimeSpan.Zero, player.Position.Value);

        player.Loop.Value = LoopMode.All;
        player.Seek(TimeSpan.Zero, 2); // last item
        api.Advance(10f);
        player.Tick();
        Assert.Equal(0, player.CurrentIndex.Value);
    }

    [Fact]
    public void End_Of_Queue_Ends_Once_And_Replays_On_Play()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Seek(TimeSpan.Zero, 2);
        player.Play();

        api.Advance(10f);
        player.Tick();
        Assert.Equal(PlaybackState.Ended, player.State.Value);

        player.Tick(); // Ended is terminal, not a loop that re-fires
        Assert.Equal(2, player.CurrentIndex.Value);

        player.Play();
        Assert.Equal(PlaybackState.Playing, player.State.Value);
        Assert.Equal(TimeSpan.Zero, player.Position.Value);
    }

    [Fact]
    public void Clip_Seeks_To_Start_And_Ends_At_End()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSources([
            AudioSource.Clip("rip.flac", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)),
            AudioSource.File("b.flac"),
        ]);
        player.Play();

        Assert.Equal(TimeSpan.FromSeconds(3), player.Duration.Value);
        Assert.Equal(2f, api.Cursor(api.LastCreated), 3);

        api.Advance(2f);
        player.Tick();
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position.Value); // clip-relative, not 4s

        api.Advance(1f);
        player.Tick();
        Assert.Equal(1, player.CurrentIndex.Value); // stopped at the clip's end, not the file's
    }

    [Fact]
    public void Editing_The_Queue_Does_Not_Interrupt_The_Current_Item()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        var items = Three();
        player.SetAudioSources(items);
        player.Seek(TimeSpan.Zero, 1);
        player.Play();
        api.Advance(3f);
        player.Tick();
        var sound = api.LastCreated;

        // Drop the first track: "b" is now index 0 and must keep playing from 3s.
        player.SetAudioSources(items[1..]);

        Assert.Equal(0, player.CurrentIndex.Value);
        Assert.Equal(sound, api.LastCreated);
        Assert.True(api.IsPlaying(sound));
        Assert.Equal(TimeSpan.FromSeconds(3), player.Position.Value);
    }

    [Fact]
    public void A_Duplicated_Track_Stays_Two_Entries()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        var track = AudioSource.File("a.flac");
        var again = AudioSource.File("a.flac"); // same file, distinct queue entry
        player.SetAudioSources([track, again]);
        player.Seek(TimeSpan.Zero, 1);
        player.Play();
        api.Advance(2f);
        player.Tick();

        player.SetAudioSources([track, again, AudioSource.File("c.flac")]);

        Assert.Equal(1, player.CurrentIndex.Value); // the second entry, not the first
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position.Value);
    }

    [Fact]
    public void Shuffle_Keeps_The_Current_Item_And_Covers_Every_Item()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Seek(TimeSpan.Zero, 1);
        player.Shuffle.Value = true;
        player.Reshuffle(1234);
        player.Play();

        Assert.Equal(1, player.CurrentIndex.Value); // still on "b"
        Assert.Equal(3, player.EffectiveIndices.Count);

        var visited = new List<int> { player.CurrentIndex.Value };
        for (var i = 0; i < 2; i++)
        {
            Assert.True(player.HasNext);
            player.SeekToNext();
            visited.Add(player.CurrentIndex.Value);
        }

        Assert.False(player.HasNext);
        Assert.Equal([0, 1, 2], visited.Order().ToArray());
    }

    [Fact]
    public void SeekToPrevious_Restarts_The_Track_Once_It_Is_Under_Way()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Seek(TimeSpan.Zero, 1);
        player.Play();

        api.Advance(5f); // past MaxSeekToPreviousPosition
        player.Tick();
        player.SeekToPrevious();
        Assert.Equal(1, player.CurrentIndex.Value);
        Assert.Equal(TimeSpan.Zero, player.Position.Value);

        api.Advance(1f); // under it
        player.Tick();
        player.SeekToPrevious();
        Assert.Equal(0, player.CurrentIndex.Value);
        Assert.False(player.HasPrevious);
    }

    [Fact]
    public void Stream_Buffering_Has_Hysteresis()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSource(AudioSource.Stream());
        player.Play();
        Assert.Equal(PlaybackState.Opening, player.State.Value);

        api.StreamState = AudioStreamState.Playing;
        player.Tick();
        Assert.Equal(PlaybackState.Buffering, player.State.Value);

        api.Buffered = 4f;
        player.Tick();
        Assert.Equal(PlaybackState.Playing, player.State.Value);
        Assert.Equal(TimeSpan.FromSeconds(4), player.BufferedPosition.Value);

        api.Buffered = 0f;
        player.Tick();
        Assert.Equal(PlaybackState.Buffering, player.State.Value);

        api.Buffered = 0.2f; // a trickle is not a recovery — no flicker back to Playing
        player.Tick();
        Assert.Equal(PlaybackState.Buffering, player.State.Value);

        api.Buffered = 0.6f;
        player.Tick();
        Assert.Equal(PlaybackState.Playing, player.State.Value);

        Assert.Equal(3, player.Push([1, 2, 3]));
    }

    [Fact]
    public void Unsupported_Stream_Fails_With_A_Reason()
    {
        var api = new FakeAudioApi { StreamState = AudioStreamState.Unsupported };
        using var player = new AudioPlayer(api);
        player.SetAudioSource(AudioSource.Stream());
        player.Play();
        player.Tick();

        Assert.Equal(PlaybackState.Failed, player.State.Value);
        Assert.NotNull(player.Error.Value);
    }

    [Fact]
    public void A_Dead_File_Is_Skipped_Rather_Than_Halting_The_Queue()
    {
        var api = new FakeAudioApi { Missing = { "b.flac" } };
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Play();

        api.Advance(10f);
        player.Tick();

        Assert.Equal(2, player.CurrentIndex.Value); // "b" skipped, "c" playing
        Assert.Equal(PlaybackState.Playing, player.State.Value);
    }

    [Fact]
    public void A_Queue_Of_Dead_Files_Fails_Once()
    {
        var api = new FakeAudioApi { Missing = { "a.flac", "b.flac", "c.flac" } };
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());

        Assert.Equal(PlaybackState.Failed, player.State.Value);
        Assert.Contains("c.flac", player.Error.Value!);
        Assert.Equal(3, api.OpenAttempts); // one pass, no spin
    }

    [Fact]
    public void Volume_Mute_And_ReplayGain_Fold_Into_One_Gain()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSource(AudioSource.File("quiet.flac", gainDb: -6f));
        var sound = api.LastCreated;

        Assert.Equal(0.501f, api.VolumeOf[sound], 3);

        player.Volume.Value = 0.5f;
        Assert.Equal(0.251f, api.VolumeOf[sound], 3);

        player.Muted.Value = true;
        Assert.Equal(0f, api.VolumeOf[sound]);

        player.Muted.Value = false;
        Assert.Equal(0.251f, api.VolumeOf[sound], 3);
    }

    [Fact]
    public void Equalizer_Routes_Every_Item_Including_The_Armed_One()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        using var eq = Equalizer.TenBand(api);
        eq.SetGain(3, 6f);

        player.Equalizer = eq;
        player.SetAudioSources(Three());
        player.Play();
        Assert.Equal(eq.Id, api.EqOf[api.LastCreated]);
        Assert.Equal(6f, api.EqBands[(eq.Id, 3)].GainDb);

        api.Advance(9f);
        player.Tick();
        Assert.Equal(eq.Id, api.EqOf[api.Scheduled[0]]);

        eq.Enabled = false;
        Assert.False(api.EqEnabled[eq.Id]);
    }

    [Fact]
    public void Stop_Releases_The_Decoder_And_Keeps_The_Place()
    {
        var api = new FakeAudioApi();
        using var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Seek(TimeSpan.Zero, 1);
        player.Play();
        var sound = api.LastCreated;

        player.Stop();
        Assert.Equal(PlaybackState.Idle, player.State.Value);
        Assert.Contains(sound, api.Destroyed);
        player.Tick(); // nothing loaded: must not throw or resurrect anything
        Assert.Equal(PlaybackState.Idle, player.State.Value);

        player.Play();
        Assert.Equal(1, player.CurrentIndex.Value);
        Assert.Equal(PlaybackState.Playing, player.State.Value);
    }

    [Fact]
    public void Dispose_Is_Idempotent_And_Everything_After_It_Is_A_No_Op()
    {
        var api = new FakeAudioApi();
        var player = new AudioPlayer(api);
        player.SetAudioSources(Three());
        player.Play();
        var sound = api.LastCreated;

        player.Dispose();
        player.Dispose();

        Assert.Contains(sound, api.Destroyed);
        Assert.Equal(PlaybackState.Idle, player.State.Value);

        player.Play();
        player.Tick();
        player.SeekToNext();
        Assert.Equal(PlaybackState.Idle, player.State.Value);
        Assert.Equal(-1, player.CurrentIndex.Value);
    }

    /// <summary>
    ///     An in-memory <see cref="IAudioApi" />. Sounds are 10 seconds long, cursors move only when a
    ///     test advances them, and a scheduled start is remembered rather than honoured — the player is
    ///     what is under test, not the mixer.
    /// </summary>
    private sealed class FakeAudioApi : IAudioApi
    {
        private readonly Dictionary<uint, float> _cursor = [];
        private readonly Dictionary<uint, float> _pendingSeek = [];
        private readonly HashSet<uint> _playing = [];
        private readonly HashSet<uint> _streams = [];
        private uint _nextId;

        public List<uint> Destroyed { get; } = [];
        public List<uint> Scheduled { get; } = [];
        public Dictionary<uint, uint> EqOf { get; } = [];
        public Dictionary<(uint Eq, int Band), EqualizerBand> EqBands { get; } = [];
        public Dictionary<uint, bool> EqEnabled { get; } = [];
        public Dictionary<uint, float> VolumeOf { get; } = [];
        public Dictionary<uint, float> RateOf { get; } = [];
        public uint LastCreated { get; private set; }
        public int OpenAttempts { get; private set; }

        /// <summary>Paths that fail to open — a file that moved out from under the library.</summary>
        public HashSet<string> Missing { get; } = [];

        /// <summary>Make seeks land a tick late, the way a real decoder refilling its buffer does.</summary>
        public bool LagSeeks { get; init; }

        public AudioStreamState StreamState { get; set; } = AudioStreamState.Connecting;
        public float Buffered { get; set; }

        public int OutputRate => 48000;

        public uint CreateFile(string path, bool streaming)
        {
            OpenAttempts++;
            if (Missing.Contains(path)) return 0;
            return LastCreated = Track();
        }

        public uint CreateStream()
        {
            var id = Track();
            _streams.Add(id);
            return LastCreated = id;
        }

        public void Destroy(uint id)
        {
            Destroyed.Add(id);
            _cursor.Remove(id);
            _playing.Remove(id);
        }

        public void Play(uint id)
        {
            _playing.Add(id);
        }

        public void Stop(uint id)
        {
            _playing.Remove(id);
            _cursor[id] = 0f;
        }

        public void Seek(uint id, float seconds)
        {
            if (LagSeeks) _pendingSeek[id] = seconds;
            else _cursor[id] = seconds;
        }

        public void ScheduleStart(uint id, float secondsFromNow)
        {
            Scheduled.Add(id);
            _playing.Add(id);
        }

        public float Cursor(uint id)
        {
            return _cursor.GetValueOrDefault(id, -1f);
        }

        public float Duration(uint id)
        {
            return _streams.Contains(id) ? -1f : 10f;
        }

        public bool IsPlaying(uint id)
        {
            return _playing.Contains(id);
        }

        public bool AtEnd(uint id)
        {
            return _streams.Contains(id) ? StreamState == AudioStreamState.Ended : Cursor(id) >= 10f;
        }

        public int StreamPush(uint id, ReadOnlySpan<byte> bytes)
        {
            return bytes.Length;
        }

        public void StreamFinish(uint id)
        {
            StreamState = AudioStreamState.Ended;
        }

        public AudioStreamState StreamStatus(uint id)
        {
            return StreamState;
        }

        public float StreamBuffered(uint id)
        {
            return Buffered;
        }

        public void SetVolume(uint id, float volume)
        {
            VolumeOf[id] = volume;
        }

        public void SetRate(uint id, float rate)
        {
            RateOf[id] = rate;
        }

        public void SetSpatial(uint id, bool enabled)
        {
        }

        public void SetEq(uint id, uint eqId)
        {
            EqOf[id] = eqId;
        }

        public uint EqCreate(int bandCount)
        {
            var id = ++_nextId;
            EqEnabled[id] = true;
            return id;
        }

        public void EqSetBand(uint eqId, int index, AudioBandKind kind, float freqHz, float gainDb,
            float q)
        {
            EqBands[(eqId, index)] = new EqualizerBand(kind, freqHz, gainDb, q);
        }

        public void EqSetEnabled(uint eqId, bool enabled)
        {
            EqEnabled[eqId] = enabled;
        }

        public void EqDestroy(uint eqId)
        {
            EqEnabled.Remove(eqId);
        }

        public int Reopen(int sampleRateHz)
        {
            return 48000;
        }

        public float[] DecodeFile(string path, out int channels, out int sampleRate)
        {
            channels = 2;
            sampleRate = 48000;
            return [];
        }

        /// <summary>Land any pending seek, then move every playing cursor forward — the test's clock.</summary>
        public void Advance(float seconds)
        {
            foreach (var (id, target) in _pendingSeek) _cursor[id] = target;
            _pendingSeek.Clear();

            foreach (var id in _playing.ToArray())
                _cursor[id] = MathF.Max(0f, _cursor.GetValueOrDefault(id)) + seconds;
        }

        private uint Track()
        {
            var id = ++_nextId;
            _cursor[id] = 0f;
            return id;
        }
    }
}
