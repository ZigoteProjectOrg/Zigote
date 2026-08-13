using Xunit;
using Zigote.Core.Math3D;
using Zigote.Scripting;

namespace Zigote.Tests;

/// <summary>Headless tests for the Music layer (crossfade / stop-fade / duck) over a recording fake.</summary>
public class MusicTests
{
    [Fact]
    public void Play_WithoutBackend_IsANoOp()
    {
        Audio.Backend = null;
        Music.Play("music/theme.ogg");
        Assert.False(Music.IsPlaying);
        Assert.Null(Music.CurrentTrack);
    }

    [Fact]
    public void Play_StartsStreamingNonSpatialLooping()
    {
        var fake = new RecordingAudio();
        Audio.Backend = fake;
        try
        {
            Music.Play(path: "music/theme.ogg", crossfadeSeconds: 0f);

            var track = fake.Sounds.Single();
            Assert.True(track.Streaming);
            Assert.False(track.Spatial);
            Assert.True(track.Looping);
            Assert.True(track.Playing);
            Assert.Equal(expected: 1f, actual: track.Volume, precision: 3);
            Assert.Equal(expected: "music/theme.ogg", actual: Music.CurrentTrack);
        }
        finally
        {
            Music.Reset();
            Audio.Backend = null;
        }
    }

    [Fact]
    public void Play_SameTrack_IsANoOp()
    {
        var fake = new RecordingAudio();
        Audio.Backend = fake;
        try
        {
            Music.Play(path: "music/theme.ogg", crossfadeSeconds: 0f);
            Music.Play(path: "music/theme.ogg", crossfadeSeconds: 0f);
            Assert.Single(fake.Sounds);
        }
        finally
        {
            Music.Reset();
            Audio.Backend = null;
        }
    }

    [Fact]
    public void Crossfade_RampsBothTracks_ThenFreesTheOldOne()
    {
        var fake = new RecordingAudio();
        Audio.Backend = fake;
        try
        {
            Music.Play(path: "a.ogg", crossfadeSeconds: 0f);
            var a = fake.Sounds[0];

            Music.Play("b.ogg");
            var b = fake.Sounds[1];
            Assert.Equal(expected: 0f, actual: b.Volume, precision: 3); // incoming starts silent

            Music.Tick(0.5f);
            Assert.Equal(expected: 0.5f, actual: b.Volume, precision: 2);
            Assert.Equal(expected: 0.5f, actual: a.Volume, precision: 2);
            Assert.False(a.Destroyed);

            Music.Tick(0.6f); // fade completes
            Assert.Equal(expected: 1f, actual: b.Volume, precision: 2);
            Assert.True(a.Destroyed);
            Assert.Equal(expected: "b.ogg", actual: Music.CurrentTrack);
        }
        finally
        {
            Music.Reset();
            Audio.Backend = null;
        }
    }

    [Fact]
    public void Stop_FadesOut_ThenFrees()
    {
        var fake = new RecordingAudio();
        Audio.Backend = fake;
        try
        {
            Music.Play(path: "a.ogg", crossfadeSeconds: 0f);
            var a = fake.Sounds[0];

            Music.Stop(0.5f);
            Assert.False(Music.IsPlaying);
            Assert.False(a.Destroyed); // still fading

            Music.Tick(0.25f);
            Assert.Equal(expected: 0.5f, actual: a.Volume, precision: 2);
            Music.Tick(0.3f);
            Assert.True(a.Destroyed);
        }
        finally
        {
            Music.Reset();
            Audio.Backend = null;
        }
    }

    [Fact]
    public void Duck_LowersAndRestoresSmoothly()
    {
        var fake = new RecordingAudio();
        Audio.Backend = fake;
        try
        {
            Music.Play(path: "a.ogg", crossfadeSeconds: 0f);
            var a = fake.Sounds[0];
            Music.DuckVolume = 0.25f;
            Music.DuckSeconds = 0.5f;

            Music.Ducked = true;
            Music.Tick(0.25f); // halfway down: duck level ~0.5 of the way 1 → 0.25
            Assert.InRange(actual: a.Volume, low: 0.55f, high: 0.75f);
            Music.Tick(0.5f);
            Assert.Equal(expected: 0.25f, actual: a.Volume, precision: 2);

            Music.Ducked = false;
            Music.Tick(1f);
            Assert.Equal(expected: 1f, actual: a.Volume, precision: 2);
        }
        finally
        {
            Music.Reset();
            Audio.Backend = null;
        }
    }

    [Fact]
    public void Reset_FreesEverything()
    {
        var fake = new RecordingAudio();
        Audio.Backend = fake;
        try
        {
            Music.Play(path: "a.ogg", crossfadeSeconds: 0f);
            Music.Play("b.ogg"); // mid-crossfade
            Music.Reset();

            Assert.All(collection: fake.Sounds, action: s => Assert.True(s.Destroyed));
            Assert.False(Music.IsPlaying);
            Assert.Null(Music.CurrentTrack);
        }
        finally
        {
            Audio.Backend = null;
        }
    }

    [Fact]
    public void Bus_RoutesNewTracks()
    {
        var fake = new RecordingAudio();
        Audio.Backend = fake;
        try
        {
            var bus = Audio.CreateBus();
            Music.Bus = bus;
            Music.Play(path: "a.ogg", crossfadeSeconds: 0f);
            Assert.Equal(expected: bus, actual: fake.Sounds[0].Bus);
        }
        finally
        {
            Music.Reset();
            Audio.Backend = null;
        }
    }

    private sealed class RecordingAudio : IAudioBackend
    {
        public readonly List<SoundState> Sounds = [];
        private uint _nextBus = 1;

        public SoundHandle CreateFile(string path, bool streaming)
        {
            Sounds.Add(new SoundState { Streaming = streaming });
            return new SoundHandle((uint)Sounds.Count);
        }

        public SoundHandle CreateTone(float frequencyHz, SoundWave wave)
        {
            Sounds.Add(new SoundState());
            return new SoundHandle((uint)Sounds.Count);
        }

        public void Play(SoundHandle sound)
        {
            if (Of(sound) is { } s) s.Playing = true;
        }

        public void Stop(SoundHandle sound)
        {
            if (Of(sound) is { } s) s.Playing = false;
        }

        public void Destroy(SoundHandle sound)
        {
            if (Of(sound) is { } s) s.Destroyed = true;
        }

        public void SetVolume(SoundHandle sound, float volume)
        {
            if (Of(sound) is { } s) s.Volume = volume;
        }

        public void SetLooping(SoundHandle sound, bool looping)
        {
            if (Of(sound) is { } s) s.Looping = looping;
        }

        public void SetSpatial(SoundHandle sound, bool enabled)
        {
            if (Of(sound) is { } s) s.Spatial = enabled;
        }

        public void SetBus(SoundHandle sound, AudioBus bus)
        {
            if (Of(sound) is { } s) s.Bus = bus;
        }

        public AudioBus CreateBus() => new(_nextBus++);

        public void SetBusVolume(AudioBus bus, float volume) { }

        public void SetBusPitch(AudioBus bus, float pitch) { }

        public void SetListener(Vec3 position, Vec3 forward, Vec3 up) { }

        public void SetMasterVolume(float volume) { }

        public void PlayUiTone(float frequencyHz, float durationSeconds, float volume,
            SoundWave wave) { }

        public void PlayToneAt(Vec3 position, float frequencyHz, float durationSeconds,
            float volume,
            SoundWave wave, float minDistance, float maxDistance, float rolloff) { }

        public void SetPitch(SoundHandle sound, float pitch) { }

        public void SetPosition(SoundHandle sound, Vec3 position) { }

        public void SetVelocity(SoundHandle sound, Vec3 velocity) { }

        public void SetAttenuation(SoundHandle sound, float minDistance, float maxDistance,
            float rolloff) { }

        public bool IsPlaying(SoundHandle sound) => Of(sound)?.Playing ?? false;

        public void StopAll() { }

        private SoundState? Of(SoundHandle h)
        {
            int index = (int)h.Id - 1;
            return index >= 0 && index < Sounds.Count ? Sounds[index] : null;
        }

        public sealed class SoundState
        {
            public AudioBus Bus = AudioBus.None;
            public bool Destroyed;
            public bool Looping;
            public bool Playing;
            public bool Spatial = true;
            public bool Streaming;
            public float Volume = 1f;
        }
    }
}
