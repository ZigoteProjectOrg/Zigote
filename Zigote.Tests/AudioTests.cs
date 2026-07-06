using Xunit;
using Zigote.Core.Math3D;
using Zigote.Runtime.Scene;
using Zigote.Scripting;

namespace Zigote.Tests;

public class AudioTests
{
    [Fact]
    public void Forwards_Spatial_OneShot_To_Backend()
    {
        var fake = new FakeAudioBackend();
        Audio.Backend = fake;
        try
        {
            Audio.PlayToneAt(
                new Vec3(1, 2, 3),
                440f,
                0.25f,
                0.7f,
                SoundWave.Square,
                2f,
                60f,
                1.5f
            );

            Assert.Equal(new Vec3(1, 2, 3), fake.LastPosition);
            Assert.Equal(440f, fake.LastFrequency);
            Assert.Equal(SoundWave.Square, fake.LastWave);
            Assert.Equal(60f, fake.LastMaxDistance);
        }
        finally
        {
            Audio.Backend = null;
        }
    }

    [Fact]
    public void Source_Handle_Lifecycle_Forwards()
    {
        var fake = new FakeAudioBackend();
        Audio.Backend = fake;
        try
        {
            var h = Audio.CreateTone(220f);
            Assert.True(h.IsValid);

            Audio.SetSpatial(h, true);
            Audio.SetPosition(h, new Vec3(4, 0, -5));
            Audio.SetAttenuation(
                h,
                1f,
                50f,
                1f
            );
            Audio.Play(h);

            Assert.Equal(h, fake.LastPlayed);
            Assert.True(fake.LastSpatial);
            Assert.Equal(new Vec3(4, 0, -5), fake.LastSourcePosition);

            Audio.Stop(h);
            Audio.Destroy(h);
            Assert.Equal(h, fake.LastDestroyed);
        }
        finally
        {
            Audio.Backend = null;
        }
    }

    [Fact]
    public void Listener_Forwards()
    {
        var fake = new FakeAudioBackend();
        Audio.Backend = fake;
        try
        {
            Audio.SetListener(new Vec3(0, 1, 0), Vec3.Forward, Vec3.Up);
            Assert.Equal(new Vec3(0, 1, 0), fake.ListenerPosition);
            Assert.Equal(Vec3.Forward, fake.ListenerForward);
        }
        finally
        {
            Audio.Backend = null;
        }
    }

    [Fact]
    public void Is_A_Safe_NoOp_When_No_Backend()
    {
        Audio.Backend = null;
        // None of these may throw outside play mode (no host backend).
        Audio.PlayToneAt(Vec3.Zero, 440f);
        Audio.PlayUiTone(880f);
        Audio.SetListener(Vec3.Zero, Vec3.Forward, Vec3.Up);
        var h = Audio.CreateTone(100f);
        Audio.Play(h);
        Audio.SetPosition(h, Vec3.One);

        Assert.False(Audio.IsAvailable);
        Assert.False(h.IsValid); // CreateTone yields SoundHandle.None with no backend
        Assert.False(Audio.IsPlaying(h));
    }

    [Fact]
    public void AudioSource_Node_Properties_Round_Trip_Through_Save_Load()
    {
        var graph = new SceneGraph();
        var src = new SceneNode("Engine Hum", NodeKind.AudioSource) {
            AudioUseFile = false,
            AudioWaveform = 3,
            AudioFrequency = 123.5f,
            AudioVolume = 0.42f,
            AudioPitch = 1.5f,
            AudioLoop = false,
            AudioAutoPlay = false,
            AudioSpatial = true,
            AudioMinDistance = 2.5f,
            AudioMaxDistance = 77f,
            AudioRolloff = 1.25f,
        };
        graph.Root.AddChild(src);

        var path = Path.Combine(Path.GetTempPath(), $"zigote_audio_{Guid.NewGuid():N}.scene");
        try
        {
            graph.Save(path);
            var loaded = SceneGraph.Load(path);

            var node = loaded.Root.Children.Single(n => n.Kind == NodeKind.AudioSource);
            Assert.Equal("Engine Hum", node.Name);
            Assert.Equal(3, node.AudioWaveform);
            Assert.Equal(123.5f, node.AudioFrequency);
            Assert.Equal(0.42f, node.AudioVolume);
            Assert.Equal(1.5f, node.AudioPitch);
            Assert.False(node.AudioLoop);
            Assert.False(node.AudioAutoPlay);
            Assert.True(node.AudioSpatial);
            Assert.Equal(2.5f, node.AudioMinDistance);
            Assert.Equal(77f, node.AudioMaxDistance);
            Assert.Equal(1.25f, node.AudioRolloff);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        private uint _next = 1;
        public SoundHandle LastDestroyed;
        public float LastFrequency;
        public float LastMaxDistance;
        public SoundHandle LastPlayed;
        public Vec3 LastPosition;
        public Vec3 LastSourcePosition;
        public bool LastSpatial;
        public SoundWave LastWave;
        public Vec3 ListenerForward;
        public Vec3 ListenerPosition;

        public void SetListener(Vec3 position, Vec3 forward, Vec3 up)
        {
            ListenerPosition = position;
            ListenerForward = forward;
        }

        public void SetMasterVolume(float volume)
        {
        }

        public void PlayUiTone(float frequencyHz, float durationSeconds, float volume,
            SoundWave wave)
        {
            LastFrequency = frequencyHz;
            LastWave = wave;
        }

        public void PlayToneAt(Vec3 position, float frequencyHz, float durationSeconds,
            float volume, SoundWave wave,
            float minDistance, float maxDistance, float rolloff)
        {
            LastPosition = position;
            LastFrequency = frequencyHz;
            LastWave = wave;
            LastMaxDistance = maxDistance;
        }

        public SoundHandle CreateTone(float frequencyHz, SoundWave wave)
        {
            return new SoundHandle(_next++);
        }

        public SoundHandle CreateFile(string path, bool streaming)
        {
            return new SoundHandle(_next++);
        }

        public void Play(SoundHandle sound)
        {
            LastPlayed = sound;
        }

        public void Stop(SoundHandle sound)
        {
        }

        public void Destroy(SoundHandle sound)
        {
            LastDestroyed = sound;
        }

        public void SetVolume(SoundHandle sound, float volume)
        {
        }

        public void SetPitch(SoundHandle sound, float pitch)
        {
        }

        public void SetLooping(SoundHandle sound, bool looping)
        {
        }

        public void SetSpatial(SoundHandle sound, bool enabled)
        {
            LastSpatial = enabled;
        }

        public void SetPosition(SoundHandle sound, Vec3 position)
        {
            LastSourcePosition = position;
        }

        public void SetVelocity(SoundHandle sound, Vec3 velocity)
        {
        }

        public void SetAttenuation(SoundHandle sound, float minDistance, float maxDistance,
            float rolloff)
        {
        }

        public bool IsPlaying(SoundHandle sound)
        {
            return false;
        }

        public AudioBus CreateBus()
        {
            return new AudioBus(_next++);
        }

        public void SetBusVolume(AudioBus bus, float volume)
        {
        }

        public void SetBusPitch(AudioBus bus, float pitch)
        {
        }

        public void SetBus(SoundHandle sound, AudioBus bus)
        {
        }

        public void StopAll()
        {
        }
    }
}