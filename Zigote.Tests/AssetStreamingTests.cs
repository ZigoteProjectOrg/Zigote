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

        var h = m.Acquire(id: id, loader: loader);
        Assert.True(h.IsValid);
        Assert.True(h.IsLoading);
        Assert.Null(h.Value);

        PumpUntil<FakeAsset>(m: m, done: () => h.IsLoaded);

        Assert.True(h.IsLoaded);
        Assert.NotNull(h.Value);
        Assert.Equal(expected: $"/content/{id}.bin", actual: h.Value!.FromPath);
        Assert.Equal(expected: 1, actual: loader.Loads);
        Assert.Equal(expected: 1, actual: loader.Applies);
        Assert.False(m.WantsFrame);
    }

    [Fact]
    public void EmptyId_YieldsNoneHandle_NoLoad()
    {
        var m = Manager(out var loader);
        var h = m.Acquire(id: AssetId.Empty, loader: loader);

        Assert.False(h.IsValid);
        Assert.Equal(expected: AssetLoadState.Unloaded, actual: h.State);
        Assert.Equal(expected: 0, actual: loader.Loads);
        Assert.Equal(expected: 0, actual: m.Count);
    }

    [Fact]
    public void ConcurrentAcquires_ShareOneLoad_AndRefCount()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();
        loader.Gate.Reset(); // hold the load so both acquires land while it is in flight

        var a = m.Acquire(id: id, loader: loader);
        var b = m.Acquire(id: id, loader: loader);
        Assert.Equal(expected: 1, actual: m.Count); // deduped to one entry

        loader.Gate.Set();
        PumpUntil<FakeAsset>(m: m, done: () => a.IsLoaded);

        Assert.True(a.IsLoaded);
        Assert.True(b.IsLoaded);
        Assert.Same(expected: a.Value, actual: b.Value);
        Assert.Equal(expected: 1, actual: loader.Loads); // one disk load fed both requesters
    }

    [Fact]
    public void ReleasedToZero_StaysResident_UntilEvicted_ThenUnloads()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();

        var h = m.Acquire(id: id, loader: loader);
        PumpUntil<FakeAsset>(m: m, done: () => h.IsLoaded);

        m.Release(h);
        Assert.Equal(expected: 1, actual: m.Count); // weakly retained
        Assert.Equal(expected: 0, actual: loader.Unloads); // not yet unloaded

        int evicted = m.EvictUnreferenced();
        Assert.Equal(expected: 1, actual: evicted);
        Assert.Equal(expected: 1, actual: loader.Unloads);
        Assert.Equal(expected: 0, actual: m.Count);

        // Re-acquire after eviction reloads from scratch.
        var h2 = m.Acquire(id: id, loader: loader);
        PumpUntil<FakeAsset>(m: m, done: () => h2.IsLoaded);
        Assert.Equal(expected: 2, actual: loader.Loads);
    }

    [Fact]
    public void StillReferenced_IsNotEvicted()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();
        var h = m.Acquire(id: id, loader: loader);
        PumpUntil<FakeAsset>(m: m, done: () => h.IsLoaded);

        Assert.Equal(expected: 0, actual: m.EvictUnreferenced()); // refcount 1 → survives
        Assert.Equal(expected: 1, actual: m.Count);
        Assert.Equal(expected: 0, actual: loader.Unloads);
    }

    [Fact]
    public void CancelledBeforePump_IsNotApplied()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();
        loader.Gate.Reset(); // hold load in flight

        var h = m.Acquire(id: id, loader: loader);
        m.Release(h); // drop the only ref while loading

        loader.Gate.Set();
        PumpUntil<FakeAsset>(m: m, done: () => !m.WantsFrame); // drain the completion

        Assert.False(h.IsLoaded);
        Assert.Equal(expected: 0, actual: loader.Applies); // payload dropped, never applied
    }

    [Fact]
    public void LoaderThrow_MarksFailed_WithError()
    {
        var m = Manager(out var loader);
        loader.ThrowOnLoad = true;
        var id = AssetId.New();

        var h = m.Acquire(id: id, loader: loader);
        PumpUntil<FakeAsset>(m: m, done: () => h.IsFailed);

        Assert.True(h.IsFailed);
        Assert.Equal(expected: "boom", actual: h.Error);
        Assert.Equal(expected: 0, actual: loader.Applies);
    }

    [Fact]
    public void UnresolvablePath_FailsSynchronously_NoWorker()
    {
        var m = Manager(loader: out var loader, resolve: _ => null);
        var id = AssetId.New();

        var h = m.Acquire(id: id, loader: loader);

        Assert.True(h.IsFailed);
        Assert.Contains(expectedSubstring: "could not be resolved", actualString: h.Error);
        Assert.Equal(expected: 0, actual: loader.Loads);
        Assert.False(m.WantsFrame);
    }

    [Fact]
    public void WantsFrame_TrueWhileInFlight_FalseWhenSettled()
    {
        var m = Manager(out var loader);
        loader.Gate.Reset();
        var id = AssetId.New();

        var h = m.Acquire(id: id, loader: loader);
        Assert.True(m.WantsFrame); // load in flight

        loader.Gate.Set();
        PumpUntil<FakeAsset>(m: m, done: () => h.IsLoaded);
        Assert.False(m.WantsFrame);
    }

    [Fact]
    public void Pump_RespectsApplyBudget()
    {
        var m = Manager(out var loader);
        loader.Gate.Reset();
        var ids = Enumerable.Range(start: 0, count: 5).Select(_ => AssetId.New()).ToArray();
        var handles = ids.Select(id => m.Acquire(id: id, loader: loader)).ToArray();

        loader.Gate.Set();
        // Wait for all five worker completions to be queued (inFlight drains to 0).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (m.WantsFrame && loader.Applies == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(1);

        m.Pump(frame: 1, maxApplies: 2);
        Assert.Equal(
            expected: 2,
            actual: handles.Count(h => h.IsLoaded)
        ); // only 2 applied this frame

        PumpUntil<FakeAsset>(m: m, done: () => handles.All(h => h.IsLoaded));
        Assert.Equal(expected: 5, actual: handles.Count(h => h.IsLoaded));
    }

    // ── Identity / path layer now in Core ───────────────────────────────────────

    [Fact]
    public void Registry_RegisterIsIdempotent_AndRoundTrips()
    {
        var reg = new AssetRegistry();
        var a = reg.Register("meshes/rock.zmesh");
        var b = reg.Register("meshes/rock.zmesh");
        Assert.Equal(expected: a, actual: b);
        Assert.Equal(expected: "meshes/rock.zmesh", actual: reg.Resolve(a));

        string tmp = Path.GetTempFileName();
        try
        {
            reg.Save(tmp);
            var loaded = AssetRegistry.Load(tmp);
            Assert.Equal(expected: "meshes/rock.zmesh", actual: loaded.Resolve(a));
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
        reg.RenamePath(oldPath: "a/old.png", newPath: "b/new.png");
        Assert.Equal(expected: id, actual: reg.Find("b/new.png"));
        Assert.Equal(expected: "b/new.png", actual: reg.Resolve(id));
    }

    [Theory]
    [InlineData("#cube", true)]
    [InlineData("#sphere", true)]
    [InlineData("meshes/x.zmesh", false)]
    [InlineData("", false)]
    public void AssetPath_DetectsBuiltins(string path, bool expected) => Assert.Equal(
        expected: expected,
        actual: AssetPath.IsBuiltinPrimitive(path)
    );

    [Fact]
    public void AssetPath_NormalisesAbsoluteUnderRootToRelative()
    {
        string root = Path.Combine(path1: Path.GetTempPath(), path2: "zigproj");
        string abs = Path.Combine(path1: root, path2: "textures", path3: "stone.png");
        string rel = AssetPath.ToRelative(path: abs, contentRoot: root);
        Assert.Equal(expected: "textures/stone.png", actual: rel);
    }

    [Fact]
    public void AssetPath_BuiltinAndRelativePassThrough()
    {
        Assert.Equal(
            expected: "#quad",
            actual: AssetPath.ToRelative(path: "#quad", contentRoot: "/anything")
        );
        Assert.Equal(
            expected: "textures/x.png",
            actual: AssetPath.ToRelative(path: "textures\\x.png", contentRoot: null)
        );
    }

    // ── FileBytesLoader (real off-thread file read) ─────────────────────────────

    [Fact]
    public void FileBytesLoader_StreamsFileContentsOffThread()
    {
        string tmp = Path.GetTempFileName();
        byte[] payload = new byte[] {
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
        };
        File.WriteAllBytes(path: tmp, bytes: payload);
        try
        {
            var reg = new AssetRegistry();
            var id = reg.Register(tmp);
            var m = new AssetManager(i => reg.Resolve(i));

            var h = m.Acquire(id: id, loader: FileBytesLoader.Instance);
            PumpUntil<byte[]>(m: m, done: () => h.IsLoaded);

            Assert.True(h.IsLoaded);
            Assert.Equal(expected: payload, actual: h.Value);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void FileBytesLoader_MissingFile_Fails()
    {
        string missing = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "zigote-nope-" + Guid.NewGuid().ToString("N") + ".zmesh"
        );
        var m = new AssetManager(_ => missing);
        var h = m.Acquire(id: AssetId.New(), loader: FileBytesLoader.Instance);
        PumpUntil<byte[]>(m: m, done: () => h.IsFailed);

        Assert.True(h.IsFailed);
        Assert.NotNull(h.Error);
    }
    // ── In-flight loads vs the table being emptied under them ───────────────────

    [Fact]
    public void Clear_WhileLoading_DropsTheCompletion_InsteadOfLeakingIt()
    {
        var m = Manager(out var loader);
        loader.Gate.Reset(); // hold the worker inside LoadOffThread

        var handle = m.Acquire(id: AssetId.New(), loader: loader);
        m.Clear(); // project closed while the load is still running
        loader.Gate.Set(); // ... and now it finishes

        // Applying here would build a resident value on a record the table no longer holds — nothing
        // could ever unload it, because evict and clear both work off the table.
        PumpUntil<FakeAsset>(m: m, done: () => loader.Applies > 0);

        Assert.Equal(expected: 0, actual: loader.Applies);
        Assert.False(handle.IsLoaded);
        Assert.Equal(expected: 0, actual: m.Count);
    }

    [Fact]
    public void Evict_WhileLoading_DropsTheCompletion_InsteadOfLeakingIt()
    {
        var m = Manager(out var loader);
        loader.Gate.Reset();

        var id = AssetId.New();
        var handle = m.Acquire(id: id, loader: loader);
        m.Release(handle); // no references left, so eviction may take the record
        Assert.Equal(expected: 1, actual: m.EvictUnreferenced());

        loader.Gate.Set();
        PumpUntil<FakeAsset>(m: m, done: () => loader.Applies > 0);

        Assert.Equal(expected: 0, actual: loader.Applies);
        Assert.Equal(expected: 0, actual: m.Count);
    }

    [Fact]
    public void ReAcquire_AfterClear_LoadsAgain_AndResolves()
    {
        var m = Manager(out var loader);
        var id = AssetId.New();

        var first = m.Acquire(id: id, loader: loader);
        PumpUntil<FakeAsset>(m: m, done: () => first.IsLoaded);
        Assert.True(first.IsLoaded);

        m.Clear();

        // A detached record must not poison the id: the next acquire builds a fresh one.
        var second = m.Acquire(id: id, loader: loader);
        PumpUntil<FakeAsset>(m: m, done: () => second.IsLoaded);
        Assert.True(second.IsLoaded);
        Assert.Equal(expected: 2, actual: loader.Applies);
    }

    // ── A resident asset + a controllable loader ────────────────────────────────

    private sealed class FakeAsset
    {
        public required string FromPath;
    }

    // ── load-gate fan-out ───────────────────────────────────────────────────────

    [Fact]
    public void BeginLoad_BoundsConcurrentLoads_AllStillComplete()
    {
        // The gate caps blocking LoadOffThread work at Clamp(ProcessorCount/2, 2, 8) — a burst of
        // Acquires must not become one thread-pool item per asset all doing disk I/O at once.
        int cap = Math.Clamp(value: Environment.ProcessorCount / 2, min: 2, max: 8);
        var loader = new ConcurrencyProbeLoader();
        var m = new AssetManager(id => $"/content/{id}.bin");

        var handles = new AssetHandle<FakeAsset>[cap * 4];
        for (int i = 0; i < handles.Length; i++)
            handles[i] = m.Acquire(id: AssetId.New(), loader: loader);

        PumpUntil<FakeAsset>(m: m, done: () => Array.TrueForAll(
            array: handles,
            match: h => h.IsLoaded
        ));

        Assert.All(collection: handles, action: h => Assert.True(h.IsLoaded));
        Assert.True(
            condition: loader.MaxConcurrent <= cap,
            userMessage: $"observed {loader.MaxConcurrent} concurrent loads, cap is {cap}"
        );
    }

    private sealed class ConcurrencyProbeLoader : IAssetLoader<FakeAsset>
    {
        private int _concurrent;
        public int MaxConcurrent;

        public object LoadOffThread(AssetId id, string path)
        {
            int now = Interlocked.Increment(ref _concurrent);
            InterlockedMax(location: ref MaxConcurrent, value: now);
            Thread.Sleep(10); // stand-in for blocking disk I/O so the burst overlaps
            Interlocked.Decrement(ref _concurrent);
            return path;
        }

        public FakeAsset Apply(AssetId id, object payload) =>
            new() { FromPath = (string)payload };

        public void Unload(AssetId id, FakeAsset value) { }

        private static void InterlockedMax(ref int location, int value)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref location);
                if (value <= seen) return;
            } while (Interlocked.CompareExchange(
                         location1: ref location,
                         value: value,
                         comparand: seen
                     ) != seen);
        }
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

        public void Unload(AssetId id, FakeAsset value) => Unloads++;
    }
}
