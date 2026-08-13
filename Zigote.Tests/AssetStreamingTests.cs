using Xunit;
using Zigote.Core.Assets;

namespace Zigote.Tests;

/// <summary>
///     Headless tests for the Phase-1 streaming core (<see cref="AssetManager" /> /
///     <see cref="AssetHandle{T}" />)
///     plus the identity/path layer moved into <c>Zigote.Core</c>. No native engine, no editor.
///     The async load is made deterministic with a gated fake loader.
/// </summary>
public class AssetStreamingTests
{
    private static AssetManager Manager(out FakeLoader loader,
        Func<AssetId, string?>? resolve = null)
    {
        loader = new FakeLoader();
        return new AssetManager(resolve ?? (id => $"/content/{id}.bin"));
    }

    private static void PumpUntil<T>(AssetManager m, Func<bool> done, int frame = 1) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            m.Pump(frame);
            Thread.Sleep(1);
        }
    }

    // ── AssetManager behaviour ──────────────────────────────────────────────────

    [Fact]
    public void Acquire_StartsLoading_ThenPumpResolvesToLoaded()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();

        var h = m.Acquire(id, loader);
        Assert.True(h.IsValid);
        Assert.True(h.IsLoading);
        Assert.Null(h.Value);

        PumpUntil<FakeAsset>(m, () => h.IsLoaded);

        Assert.True(h.IsLoaded);
        Assert.NotNull(h.Value);
        Assert.Equal($"/content/{id}.bin", h.Value!.FromPath);
        Assert.Equal(1, loader.Loads);
        Assert.Equal(1, loader.Applies);
        Assert.False(m.WantsFrame);
    }

    [Fact]
    public void EmptyId_YieldsNoneHandle_NoLoad()
    {
        var m = Manager(out var loader);
        var h = m.Acquire(AssetId.Empty, loader);

        Assert.False(h.IsValid);
        Assert.Equal(AssetLoadState.Unloaded, h.State);
        Assert.Equal(0, loader.Loads);
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void ConcurrentAcquires_ShareOneLoad_AndRefCount()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();
        loader.Gate.Reset(); // hold the load so both acquires land while it is in flight

        var a = m.Acquire(id, loader);
        var b = m.Acquire(id, loader);
        Assert.Equal(1, m.Count); // deduped to one entry

        loader.Gate.Set();
        PumpUntil<FakeAsset>(m, () => a.IsLoaded);

        Assert.True(a.IsLoaded);
        Assert.True(b.IsLoaded);
        Assert.Same(a.Value, b.Value);
        Assert.Equal(1, loader.Loads); // one disk load fed both requesters
    }

    [Fact]
    public void ReleasedToZero_StaysResident_UntilEvicted_ThenUnloads()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();

        var h = m.Acquire(id, loader);
        PumpUntil<FakeAsset>(m, () => h.IsLoaded);

        m.Release(h);
        Assert.Equal(1, m.Count); // weakly retained
        Assert.Equal(0, loader.Unloads); // not yet unloaded

        var evicted = m.EvictUnreferenced();
        Assert.Equal(1, evicted);
        Assert.Equal(1, loader.Unloads);
        Assert.Equal(0, m.Count);

        // Re-acquire after eviction reloads from scratch.
        var h2 = m.Acquire(id, loader);
        PumpUntil<FakeAsset>(m, () => h2.IsLoaded);
        Assert.Equal(2, loader.Loads);
    }

    [Fact]
    public void StillReferenced_IsNotEvicted()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();
        var h = m.Acquire(id, loader);
        PumpUntil<FakeAsset>(m, () => h.IsLoaded);

        Assert.Equal(0, m.EvictUnreferenced()); // refcount 1 → survives
        Assert.Equal(1, m.Count);
        Assert.Equal(0, loader.Unloads);
    }

    [Fact]
    public void CancelledBeforePump_IsNotApplied()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();
        loader.Gate.Reset(); // hold load in flight

        var h = m.Acquire(id, loader);
        m.Release(h); // drop the only ref while loading

        loader.Gate.Set();
        PumpUntil<FakeAsset>(m, () => !m.WantsFrame); // drain the completion

        Assert.False(h.IsLoaded);
        Assert.Equal(0, loader.Applies); // payload dropped, never applied
    }

    [Fact]
    public void LoaderThrow_MarksFailed_WithError()
    {
        var m = Manager(out var loader);
        loader.ThrowOnLoad = true;
        var id = AssetId.New();

        var h = m.Acquire(id, loader);
        PumpUntil<FakeAsset>(m, () => h.IsFailed);

        Assert.True(h.IsFailed);
        Assert.Equal("boom", h.Error);
        Assert.Equal(0, loader.Applies);
    }

    [Fact]
    public void UnresolvablePath_FailsSynchronously_NoWorker()
    {
        var m = Manager(out var loader, _ => null);
        var id = AssetId.New();

        var h = m.Acquire(id, loader);

        Assert.True(h.IsFailed);
        Assert.Contains("could not be resolved", h.Error);
        Assert.Equal(0, loader.Loads);
        Assert.False(m.WantsFrame);
    }

    [Fact]
    public void WantsFrame_TrueWhileInFlight_FalseWhenSettled()
    {
        var m = Manager(out var loader);
        loader.Gate.Reset();
        var id = AssetId.New();

        var h = m.Acquire(id, loader);
        Assert.True(m.WantsFrame); // load in flight

        loader.Gate.Set();
        PumpUntil<FakeAsset>(m, () => h.IsLoaded);
        Assert.False(m.WantsFrame);
    }

    [Fact]
    public void Pump_RespectsApplyBudget()
    {
        var m = Manager(out var loader);
        loader.Gate.Reset();
        var ids = Enumerable.Range(0, 5).Select(_ => AssetId.New()).ToArray();
        var handles = ids.Select(id => m.Acquire(id, loader)).ToArray();

        loader.Gate.Set();
        // Wait for all five worker completions to be queued (inFlight drains to 0).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (m.WantsFrame && loader.Applies == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(1);

        m.Pump(1, 2);
        Assert.Equal(2, handles.Count(h => h.IsLoaded)); // only 2 applied this frame

        PumpUntil<FakeAsset>(m, () => handles.All(h => h.IsLoaded));
        Assert.Equal(5, handles.Count(h => h.IsLoaded));
    }

    // ── Identity / path layer now in Core ───────────────────────────────────────

    [Fact]
    public void Registry_RegisterIsIdempotent_AndRoundTrips()
    {
        var reg = new AssetRegistry();
        var a = reg.Register("meshes/rock.zmesh");
        var b = reg.Register("meshes/rock.zmesh");
        Assert.Equal(a, b);
        Assert.Equal("meshes/rock.zmesh", reg.Resolve(a));

        var tmp = Path.GetTempFileName();
        try
        {
            reg.Save(tmp);
            var loaded = AssetRegistry.Load(tmp);
            Assert.Equal("meshes/rock.zmesh", loaded.Resolve(a));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Registry_RenamePreservesId()
    {
        var reg = new AssetRegistry();
        var id = reg.Register("a/old.png");
        reg.RenamePath("a/old.png", "b/new.png");
        Assert.Equal(id, reg.Find("b/new.png"));
        Assert.Equal("b/new.png", reg.Resolve(id));
    }

    [Theory]
    [InlineData("#cube", true)]
    [InlineData("#sphere", true)]
    [InlineData("meshes/x.zmesh", false)]
    [InlineData("", false)]
    public void AssetPath_DetectsBuiltins(string path, bool expected)
    {
        Assert.Equal(expected, AssetPath.IsBuiltinPrimitive(path));
    }

    [Fact]
    public void AssetPath_NormalisesAbsoluteUnderRootToRelative()
    {
        var root = Path.Combine(Path.GetTempPath(), "zigproj");
        var abs = Path.Combine(root, "textures", "stone.png");
        var rel = AssetPath.ToRelative(abs, root);
        Assert.Equal("textures/stone.png", rel);
    }

    [Fact]
    public void AssetPath_BuiltinAndRelativePassThrough()
    {
        Assert.Equal("#quad", AssetPath.ToRelative("#quad", "/anything"));
        Assert.Equal("textures/x.png", AssetPath.ToRelative("textures\\x.png", null));
    }

    // ── FileBytesLoader (real off-thread file read) ─────────────────────────────

    [Fact]
    public void FileBytesLoader_StreamsFileContentsOffThread()
    {
        var tmp = Path.GetTempFileName();
        var payload = new byte[] {
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
        };
        File.WriteAllBytes(tmp, payload);
        try
        {
            var reg = new AssetRegistry();
            var id = reg.Register(tmp);
            var m = new AssetManager(i => reg.Resolve(i));

            var h = m.Acquire(id, FileBytesLoader.Instance);
            PumpUntil<byte[]>(m, () => h.IsLoaded);

            Assert.True(h.IsLoaded);
            Assert.Equal(payload, h.Value);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void FileBytesLoader_MissingFile_Fails()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "zigote-nope-" + Guid.NewGuid().ToString("N") + ".zmesh"
        );
        var m = new AssetManager(_ => missing);
        var h = m.Acquire(AssetId.New(), FileBytesLoader.Instance);
        PumpUntil<byte[]>(m, () => h.IsFailed);

        Assert.True(h.IsFailed);
        Assert.NotNull(h.Error);
    }
    // ── In-flight loads vs the table being emptied under them ───────────────────

    [Fact]
    public void Clear_WhileLoading_DropsTheCompletion_InsteadOfLeakingIt()
    {
        var m = Manager(out var loader);
        loader.Gate.Reset(); // hold the worker inside LoadOffThread

        var handle = m.Acquire(AssetId.New(), loader);
        m.Clear(); // project closed while the load is still running
        loader.Gate.Set(); // ... and now it finishes

        // Applying here would build a resident value on a record the table no longer holds — nothing
        // could ever unload it, because evict and clear both work off the table.
        PumpUntil<FakeAsset>(m, () => loader.Applies > 0);

        Assert.Equal(0, loader.Applies);
        Assert.False(handle.IsLoaded);
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void Evict_WhileLoading_DropsTheCompletion_InsteadOfLeakingIt()
    {
        var m = Manager(out var loader);
        loader.Gate.Reset();

        var id = AssetId.New();
        var handle = m.Acquire(id, loader);
        m.Release(handle); // no references left, so eviction may take the record
        Assert.Equal(1, m.EvictUnreferenced());

        loader.Gate.Set();
        PumpUntil<FakeAsset>(m, () => loader.Applies > 0);

        Assert.Equal(0, loader.Applies);
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void ReAcquire_AfterClear_LoadsAgain_AndResolves()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();

        var first = m.Acquire(id, loader);
        PumpUntil<FakeAsset>(m, () => first.IsLoaded);
        Assert.True(first.IsLoaded);

        m.Clear();

        // A detached record must not poison the id: the next acquire builds a fresh one.
        var second = m.Acquire(id, loader);
        PumpUntil<FakeAsset>(m, () => second.IsLoaded);
        Assert.True(second.IsLoaded);
        Assert.Equal(2, loader.Applies);
    }

    // ── A resident asset + a controllable loader ────────────────────────────────

    private sealed class FakeAsset
    {
        public required string FromPath;
    }

    private sealed class FakeLoader : IAssetLoader<FakeAsset>
    {
        public readonly ManualResetEventSlim Gate = new(true); // open = loads run immediately
        public int Applies;
        public int Loads;
        public bool ThrowOnLoad;
        public int Unloads;

        public object LoadOffThread(AssetId id, string path)
        {
            Gate.Wait();
            Interlocked.Increment(ref Loads);
            if (ThrowOnLoad) throw new InvalidOperationException("boom");
            return path;
        }

        public FakeAsset Apply(AssetId id, object payload)
        {
            Applies++;
            return new FakeAsset { FromPath = (string)payload };
        }

        public void Unload(AssetId id, FakeAsset value)
        {
            Unloads++;
        }
    }
}
