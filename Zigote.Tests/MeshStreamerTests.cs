using Xunit;
using Zigote.Core.Assets;
using Zigote.Runtime.Scene;

namespace Zigote.Tests;

/// <summary>
///     The demand-driven mesh residency sink (<see cref="MeshStreamer" />): Want acquires a node's
///     <c>.zmesh</c> blob off-thread and uploads it once loaded (main thread), Drop releases +
///     unloads,
///     and built-in primitives are skipped. Uses a real <see cref="AssetManager" /> +
///     <see cref="FileBytesLoader" /> over a temp file — no native engine (upload/unload are
///     injected).
/// </summary>
public sealed class MeshStreamerTests
{
    [Fact]
    public void Want_AcquiresAndUploadsOnce_Drop_Unloads_ReWant_Reloads()
    {
        var tmp = Path.GetTempFileName();
        var blob = new byte[] {
            9,
            8,
            7,
            6,
            5,
        };
        File.WriteAllBytes(tmp, blob);
        try
        {
            var reg = new AssetRegistry();
            var mgr = new AssetManager(id => reg.Resolve(id)
            ); // registry stores the absolute temp path
            var uploads = new List<byte[]>();
            var unloads = 0;

            var streamer = new MeshStreamer(
                mgr,
                _ => reg.Register(tmp), // node → AssetId
                (_, bytes) => uploads.Add(bytes),
                _ => unloads++
            );

            var node = new SceneNode("m", NodeKind.Mesh) {
                MeshPath = "meshes/x.zmesh",
                Handle = 1,
            };

            // First Want kicks off the async load; nothing is uploaded until it completes.
            streamer.Want(node, 1f);
            Assert.Empty(uploads);

            // Pump + re-Want until the blob is applied on the main thread.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            long frame = 0;
            while (uploads.Count == 0 && DateTime.UtcNow < deadline)
            {
                mgr.Pump(frame++);
                streamer.Want(node, 1f);
                Thread.Sleep(1);
            }

            Assert.Single(uploads);
            Assert.Equal(blob, uploads[0]);

            // Idempotent: staying in range does not re-upload.
            streamer.Want(node, 1f);
            Assert.Single(uploads);

            // Out of range: release + unload (hide).
            streamer.Drop(node);
            Assert.Equal(1, unloads);

            // Back in range: the cache entry is still resident, so it uploads again immediately.
            streamer.Want(node, 1f);
            Assert.Equal(2, uploads.Count);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void BuiltinPrimitive_And_NonMesh_AreNotStreamed()
    {
        var mgr = new AssetManager(_ => null);
        var uploads = 0;
        var streamer = new MeshStreamer(
            mgr,
            _ => AssetId.New(),
            (_, _) => uploads++,
            _ => { }
        );

        var cube = new SceneNode("c", NodeKind.Mesh) {
            MeshPath = "#cube",
            Handle = 1,
        };
        var light = new SceneNode("l", NodeKind.Light);

        Assert.False(MeshStreamer.IsStreamable(cube));
        Assert.False(MeshStreamer.IsStreamable(light));

        streamer.Want(cube, 1f);
        streamer.Want(light, 1f);
        Assert.Equal(0, uploads);
    }
}