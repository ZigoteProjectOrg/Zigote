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
                position: new Vec3(x: 1, y: 2, z: 3),
                frequencyHz: 440f,
                durationSeconds: 0.25f,
                volume: 0.7f,
                wave: SoundWave.Square,
                minDistance: 2f,
                maxDistance: 60f,
                rolloff: 1.5f
            );

            Assert.Equal(expected: new Vec3(x: 1, y: 2, z: 3), actual: fake.LastPosition);
            Assert.Equal(expected: 440f, actual: fake.LastFrequency);
            Assert.Equal(expected: SoundWave.Square, actual: fake.LastWave);
            Assert.Equal(expected: 60f, actual: fake.LastMaxDistance);
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

            Audio.SetSpatial(sound: h, enabled: true);
            Audio.SetPosition(sound: h, position: new Vec3(x: 4, y: 0, z: -5));
            Audio.SetAttenuation(
                sound: h,
                minDistance: 1f,
                maxDistance: 50f,
                rolloff: 1f
            );
            Audio.Play(h);

            Assert.Equal(expected: h, actual: fake.LastPlayed);
            Assert.True(fake.LastSpatial);
            Assert.Equal(expected: new Vec3(x: 4, y: 0, z: -5), actual: fake.LastSourcePosition);

            Audio.Stop(h);
            Audio.Destroy(h);
            Assert.Equal(expected: h, actual: fake.LastDestroyed);
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
            Audio.SetListener(
                position: new Vec3(x: 0, y: 1, z: 0),
                forward: Vec3.Forward,
                up: Vec3.Up
            );
            Assert.Equal(expected: new Vec3(x: 0, y: 1, z: 0), actual: fake.ListenerPosition);
            Assert.Equal(expected: Vec3.Forward, actual: fake.ListenerForward);
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
        Audio.PlayToneAt(position: Vec3.Zero, frequencyHz: 440f);
        Audio.PlayUiTone(880f);
        Audio.SetListener(position: Vec3.Zero, forward: Vec3.Forward, up: Vec3.Up);
        var h = Audio.CreateTone(100f);
        Audio.Play(h);
        Audio.SetPosition(sound: h, position: Vec3.One);

        Assert.False(Audio.IsAvailable);
        Assert.False(h.IsValid); // CreateTone yields SoundHandle.None with no backend
        Assert.False(Audio.IsPlaying(h));
    }

    [Fact]
    public void AudioSource_Node_Properties_Round_Trip_Through_Save_Load()
    {
        var graph = new SceneGraph();
        var src = new SceneNode(name: "Engine Hum", kind: NodeKind.AudioSource) {
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

        string path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"zigote_audio_{Guid.NewGuid():N}.scene"
        );
        try
        {
            graph.Save(path);
            var loaded = SceneGraph.Load(path);

            var node = loaded.Root.Children.Single(n => n.Kind == NodeKind.AudioSource);
            Assert.Equal(expected: "Engine Hum", actual: node.Name);
            Assert.Equal(expected: 3, actual: node.AudioWaveform);
            Assert.Equal(expected: 123.5f, actual: node.AudioFrequency);
            Assert.Equal(expected: 0.42f, actual: node.AudioVolume);
            Assert.Equal(expected: 1.5f, actual: node.AudioPitch);
            Assert.False(node.AudioLoop);
            Assert.False(node.AudioAutoPlay);
            Assert.True(node.AudioSpatial);
            Assert.Equal(expected: 2.5f, actual: node.AudioMinDistance);
            Assert.Equal(expected: 77f, actual: node.AudioMaxDistance);
            Assert.Equal(expected: 1.25f, actual: node.AudioRolloff);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
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
        private uint _next = 1;

        public void SetListener(Vec3 position, Vec3 forward, Vec3 up)
        {
            ListenerPosition = position;
            ListenerForward = forward;
        }

        public void SetMasterVolume(float volume) { }

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

        public SoundHandle CreateTone(float frequencyHz, SoundWave wave) => new(_next++);

        public SoundHandle CreateFile(string path, bool streaming) => new(_next++);

        public void Play(SoundHandle sound) => LastPlayed = sound;

        public void Stop(SoundHandle sound) { }

        public void Destroy(SoundHandle sound) => LastDestroyed = sound;

        public void SetVolume(SoundHandle sound, float volume) { }

        public void SetPitch(SoundHandle sound, float pitch) { }

        public void SetLooping(SoundHandle sound, bool looping) { }

        public void SetSpatial(SoundHandle sound, bool enabled) => LastSpatial = enabled;

        public void SetPosition(SoundHandle sound, Vec3 position) => LastSourcePosition = position;

        public void SetVelocity(SoundHandle sound, Vec3 velocity) { }

        public void SetAttenuation(SoundHandle sound, float minDistance, float maxDistance,
            float rolloff) { }

        public bool IsPlaying(SoundHandle sound) => false;

        public AudioBus CreateBus() => new(_next++);

        public void SetBusVolume(AudioBus bus, float volume) { }

        public void SetBusPitch(AudioBus bus, float pitch) { }

        public void SetBus(SoundHandle sound, AudioBus bus) { }

        public void StopAll() { }
    }
}
