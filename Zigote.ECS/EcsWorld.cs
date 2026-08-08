using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Zigote.Core.Native;

namespace Zigote.Ecs;

/// <summary>
///     A flecs ECS world. Entities, components, queries, systems, observers, relationships, and
///     prefabs
///     are all backed by native flecs (via <c>zigote_ecs_*</c> FFI). Worlds are independent of the
///     3D engine; no window or GPU init is required.
/// </summary>
public sealed class EcsWorld : IDisposable
{
    private readonly Dictionary<Type, ulong> _componentIds = new();

    // ForEach reuses one native query per component-id set (ecs_query_init per call is ~µs and the
    // flagship samples call ForEach every frame). Unused slots in the key are 0 (never a valid id).
    private readonly Dictionary<(ulong, ulong, ulong, ulong), IDisposable> _queryCache = new();
    private readonly ulong _world;
    private bool _disposed;

    public EcsWorld()
    {
        _world = NativeEngine.EcsWorldCreate();
    }

    // ── Entity count (C#-tracked; user-created entities only) ──────────────────
    public int EntityCount { get; private set; }

    /// <summary>
    ///     Registered systems + observers (C#-tracked). Zero means nothing entity-side can write
    ///     components outside direct <see cref="Set{T}" /> calls — hosts use this to skip
    ///     entity→node mirroring work when no ECS pass can have run.
    /// </summary>
    public int SystemCount { get; private set; }

    // ── Pair / relationship primitives ──────────────────────────────────────────

    public ulong ChildOf => NativeEngine.EcsBuiltinChildof();
    public ulong IsARelation => NativeEngine.EcsBuiltinIsa();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var q in _queryCache.Values) q.Dispose();
        _queryCache.Clear();
        NativeEngine.EcsWorldDestroy(_world);
    }

    // ── Component ID cache (per-world; flecs ids are NOT process-global) ───────

    public unsafe ulong ComponentId<T>() where T : unmanaged
    {
        if (_componentIds.TryGetValue(typeof(T), out var id)) return id;
        var nameBytes = Encoding.UTF8.GetBytes(typeof(T).FullName! + "\0");
        fixed (byte* p = nameBytes)
        {
            id = NativeEngine.EcsComponentRegister(
                _world,
                p,
                (nuint)Unsafe.SizeOf<T>(),
                AlignOf<T>()
            );
        }

        return _componentIds[typeof(T)] = id;
    }

    private static nuint AlignOf<T>() where T : unmanaged
    {
        return AlignOfSize((nuint)Unsafe.SizeOf<T>());
    }

    private static nuint AlignOfSize(nuint sz)
    {
        return sz switch { >= 8 => 8, >= 4 => 4, >= 2 => 2, _ => 1 };
    }

    // ── Entity lifecycle ────────────────────────────────────────────────────────

    public Entity CreateEntity()
    {
        var e = new Entity(NativeEngine.EcsEntityCreate(_world));
        EntityCount++;
        return e;
    }

    public unsafe Entity CreateEntity(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            var e = new Entity(NativeEngine.EcsEntityCreateNamed(_world, p));
            EntityCount++;
            return e;
        }
    }

    public void DestroyEntity(Entity e)
    {
        if (!IsAlive(e)) return;
        NativeEngine.EcsEntityDestroy(_world, e.Raw);
        EntityCount--;
    }

    public bool IsAlive(Entity e)
    {
        return e.Raw != 0 && NativeEngine.EcsEntityIsAlive(_world, e.Raw) != 0;
    }

    // ── Component operations ────────────────────────────────────────────────────

    /// <summary>Add the component type (zero-initialised) without setting a value.</summary>
    public void Add<T>(Entity e) where T : unmanaged
    {
        NativeEngine.EcsAdd(_world, e.Raw, ComponentId<T>());
    }

    /// <summary>Add (if absent) and set the component value.</summary>
    public unsafe void Set<T>(Entity e, in T c) where T : unmanaged
    {
        var id = ComponentId<T>();
        fixed (T* p = &c)
        {
            NativeEngine.EcsSet(
                _world,
                e.Raw,
                id,
                (byte*)p,
                (nuint)Unsafe.SizeOf<T>()
            );
        }
    }

    /// <summary>Convenience overload: <c>Add(e, value)</c> = <c>Set(e, value)</c>.</summary>
    public void Add<T>(Entity e, in T c) where T : unmanaged
    {
        Set(e, c);
    }

    public bool Has<T>(Entity e) where T : unmanaged
    {
        return NativeEngine.EcsHas(_world, e.Raw, ComponentId<T>()) != 0;
    }

    /// <summary>
    ///     True if the entity has <typeparamref name="T" /> on ITSELF (not merely inherited via a
    ///     <c>(IsA, prefab)</c> link). Distinguishes a prefab-instance override from an inherited value.
    /// </summary>
    public bool Owns<T>(Entity e) where T : unmanaged
    {
        return NativeEngine.EcsOwns(_world, e.Raw, ComponentId<T>()) != 0;
    }

    /// <summary>
    ///     Returns a <see cref="Span{T}" /> into flecs native storage.
    ///     Do NOT store the ref across any operation that could restructure the archetype
    ///     (add/remove/destroy on the same entity). Use <see cref="Defer" /> for safe batched mutations.
    /// </summary>
    public unsafe ref T Get<T>(Entity e) where T : unmanaged
    {
        var ptr = NativeEngine.EcsGetMut(_world, e.Raw, ComponentId<T>());
        if (ptr == null)
            throw new InvalidOperationException($"Entity {e} has no component {typeof(T).Name}.");
        return ref Unsafe.AsRef<T>(ptr);
    }

    public unsafe bool TryGet<T>(Entity e, out T value) where T : unmanaged
    {
        var ptr = NativeEngine.EcsGet(_world, e.Raw, ComponentId<T>());
        if (ptr == null)
        {
            value = default;
            return false;
        }

        value = Unsafe.ReadUnaligned<T>(ptr);
        return true;
    }

    public bool Remove<T>(Entity e) where T : unmanaged
    {
        if (!Has<T>(e)) return false;
        NativeEngine.EcsRemove(_world, e.Raw, ComponentId<T>());
        return true;
    }

    // ── Reflective (runtime-Type) component access ────────────────────────────────
    // The generic ops above need T at compile time. The editor inspector / serializer hold a runtime
    // Type (resolved from a registry by name), so they need a non-generic path. Components MUST be
    // blittable POD (no `bool`/`char`/managed fields) — flecs stores them by size, and these use the
    // marshalled layout, which matches the blittable layout only for blittable structs.

    /// <summary>Per-world flecs id for a runtime component <see cref="Type" /> (shares the generic cache).</summary>
    public unsafe ulong ComponentId(Type t)
    {
        if (_componentIds.TryGetValue(t, out var id)) return id;
        var size = (nuint)Marshal.SizeOf(t);
        var nameBytes = Encoding.UTF8.GetBytes(t.FullName! + "\0");
        fixed (byte* p = nameBytes)
        {
            id = NativeEngine.EcsComponentRegister(
                _world,
                p,
                size,
                AlignOfSize(size)
            );
        }

        return _componentIds[t] = id;
    }

    public bool Has(Entity e, Type t)
    {
        return NativeEngine.EcsHas(_world, e.Raw, ComponentId(t)) != 0;
    }

    public bool Owns(Entity e, Type t)
    {
        return NativeEngine.EcsOwns(_world, e.Raw, ComponentId(t)) != 0;
    }

    /// <summary>
    ///     Remove a component by runtime Type. On a prefab instance this reverts an override to
    ///     inherited.
    /// </summary>
    public bool Remove(Entity e, Type t)
    {
        if (!Owns(e, t))
            return
                false; // only an OWNED component can be removed; inherited ones aren't "present" to remove
        NativeEngine.EcsRemove(_world, e.Raw, ComponentId(t));
        return true;
    }

    /// <summary>Read a component as a boxed struct of <paramref name="t" /> (null if absent).</summary>
    public unsafe object? GetBoxed(Entity e, Type t)
    {
        var ptr = NativeEngine.EcsGet(_world, e.Raw, ComponentId(t));
        return ptr == null ? null : Marshal.PtrToStructure((IntPtr)ptr, t);
    }

    /// <summary>Add (if absent) and set a component from a boxed struct of <paramref name="t" />.</summary>
    public unsafe void SetBoxed(Entity e, Type t, object value)
    {
        var size = Marshal.SizeOf(t);
        var buf = size <= 256 ? stackalloc byte[size] : new byte[size];
        fixed (byte* p = buf)
        {
            Marshal.StructureToPtr(value, (IntPtr)p, false);
            NativeEngine.EcsSet(
                _world,
                e.Raw,
                ComponentId(t),
                p,
                (nuint)size
            );
        }
    }

    // ── Systems pipeline ────────────────────────────────────────────────────────

    /// <summary>Run all OnUpdate (and dependent) systems once. Returns false to signal shutdown.</summary>
    public bool Progress(float dt = 0f)
    {
        return NativeEngine.EcsProgress(_world, dt) != 0;
    }

    // ── Deferred mutations ──────────────────────────────────────────────────────

    /// <summary>
    ///     Queue all structural operations inside <paramref name="body" /> until after the block —
    ///     required when mutating entities during query iteration.
    /// </summary>
    public void Defer(Action body)
    {
        NativeEngine.EcsDeferBegin(_world);
        try
        {
            body();
        }
        finally
        {
            NativeEngine.EcsDeferEnd(_world);
        }
    }

    // ── Queries ─────────────────────────────────────────────────────────────────

    public unsafe Query<T1> Query<T1>() where T1 : unmanaged
    {
        var id = ComponentId<T1>();
        var q = NativeEngine.EcsQueryCreate(_world, &id, 1);
        return new Query<T1>(_world, q);
    }

    public unsafe Query<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged
    {
        var ids = stackalloc ulong[] {
            ComponentId<T1>(),
            ComponentId<T2>(),
        };
        var q = NativeEngine.EcsQueryCreate(_world, ids, 2);
        return new Query<T1, T2>(_world, q);
    }

    public unsafe Query<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        var ids = stackalloc ulong[] {
            ComponentId<T1>(),
            ComponentId<T2>(),
            ComponentId<T3>(),
        };
        var q = NativeEngine.EcsQueryCreate(_world, ids, 3);
        return new Query<T1, T2, T3>(_world, q);
    }

    public unsafe Query<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        var ids = stackalloc ulong[] {
            ComponentId<T1>(),
            ComponentId<T2>(),
            ComponentId<T3>(),
            ComponentId<T4>(),
        };
        var q = NativeEngine.EcsQueryCreate(_world, ids, 4);
        return new Query<T1, T2, T3, T4>(_world, q);
    }

    private Query<T1> CachedQuery<T1>() where T1 : unmanaged
    {
        var key = (ComponentId<T1>(), 0UL, 0UL, 0UL);
        if (_queryCache.TryGetValue(key, out var cached)) return (Query<T1>)cached;
        var q = Query<T1>();
        _queryCache[key] = q;
        return q;
    }

    private Query<T1, T2> CachedQuery<T1, T2>() where T1 : unmanaged where T2 : unmanaged
    {
        var key = (ComponentId<T1>(), ComponentId<T2>(), 0UL, 0UL);
        if (_queryCache.TryGetValue(key, out var cached)) return (Query<T1, T2>)cached;
        var q = Query<T1, T2>();
        _queryCache[key] = q;
        return q;
    }

    private Query<T1, T2, T3> CachedQuery<T1, T2, T3>()
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        var key = (ComponentId<T1>(), ComponentId<T2>(), ComponentId<T3>(), 0UL);
        if (_queryCache.TryGetValue(key, out var cached)) return (Query<T1, T2, T3>)cached;
        var q = Query<T1, T2, T3>();
        _queryCache[key] = q;
        return q;
    }

    private Query<T1, T2, T3, T4> CachedQuery<T1, T2, T3, T4>()
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        var key = (ComponentId<T1>(), ComponentId<T2>(), ComponentId<T3>(), ComponentId<T4>());
        if (_queryCache.TryGetValue(key, out var cached)) return (Query<T1, T2, T3, T4>)cached;
        var q = Query<T1, T2, T3, T4>();
        _queryCache[key] = q;
        return q;
    }

    /// <summary>Convenience: iterate every entity that has <typeparamref name="T1" />.</summary>
    public void ForEach<T1>(Action<Span<T1>> body) where T1 : unmanaged
    {
        CachedQuery<T1>().Each(body);
    }

    /// <summary>
    ///     Convenience: iterate every entity that has both <typeparamref name="T1" /> and
    ///     <typeparamref name="T2" />.
    /// </summary>
    public void ForEach<T1, T2>(Action<Span<T1>, Span<T2>> body)
        where T1 : unmanaged where T2 : unmanaged
    {
        CachedQuery<T1, T2>().Each(body);
    }

    /// <summary>Convenience: iterate every entity that has all three component types.</summary>
    public void ForEach<T1, T2, T3>(Action<Span<T1>, Span<T2>, Span<T3>> body)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        CachedQuery<T1, T2, T3>().Each(body);
    }

    /// <summary>Convenience: iterate every entity that has all four component types.</summary>
    public void ForEach<T1, T2, T3, T4>(Action<Span<T1>, Span<T2>, Span<T3>, Span<T4>> body)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        CachedQuery<T1, T2, T3, T4>().Each(body);
    }

    // ── System registration ─────────────────────────────────────────────────────

    public unsafe void RegisterSystem<T1>(string name, EcsPhase phase, Action<Span<T1>> body)
        where T1 : unmanaged
    {
        var id1 = ComponentId<T1>();
        var cookie = EcsSystemTable.Register(iterPtr =>
            {
                var n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                body(
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            0,
                            (nuint)Unsafe.SizeOf<T1>()
                        ),
                        n
                    )
                );
            }
        );
        var phaseId = NativeEngine.EcsBuiltinPhase((byte)phase);
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsSystemCreate(
                _world,
                namePtr,
                phaseId,
                &id1,
                1,
                (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                cookie
            );
        }

        SystemCount++;
    }

    public unsafe void RegisterSystem<T1, T2>(string name, EcsPhase phase,
        Action<Span<T1>, Span<T2>> body)
        where T1 : unmanaged where T2 : unmanaged
    {
        var id1 = ComponentId<T1>();
        var id2 = ComponentId<T2>();
        var cookie = EcsSystemTable.Register(iterPtr =>
            {
                var n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                body(
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            0,
                            (nuint)Unsafe.SizeOf<T1>()
                        ),
                        n
                    ),
                    new Span<T2>(
                        (T2*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            1,
                            (nuint)Unsafe.SizeOf<T2>()
                        ),
                        n
                    )
                );
            }
        );
        var phaseId = NativeEngine.EcsBuiltinPhase((byte)phase);
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        var ids = stackalloc ulong[] {
            id1,
            id2,
        };
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsSystemCreate(
                _world,
                namePtr,
                phaseId,
                ids,
                2,
                (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                cookie
            );
        }

        SystemCount++;
    }

    public unsafe void RegisterSystem<T1, T2, T3>(string name, EcsPhase phase,
        Action<Span<T1>, Span<T2>, Span<T3>> body)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        var id1 = ComponentId<T1>();
        var id2 = ComponentId<T2>();
        var id3 = ComponentId<T3>();
        var cookie = EcsSystemTable.Register(iterPtr =>
            {
                var n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                body(
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            0,
                            (nuint)Unsafe.SizeOf<T1>()
                        ),
                        n
                    ),
                    new Span<T2>(
                        (T2*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            1,
                            (nuint)Unsafe.SizeOf<T2>()
                        ),
                        n
                    ),
                    new Span<T3>(
                        (T3*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            2,
                            (nuint)Unsafe.SizeOf<T3>()
                        ),
                        n
                    )
                );
            }
        );
        var phaseId = NativeEngine.EcsBuiltinPhase((byte)phase);
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        var ids = stackalloc ulong[] {
            id1,
            id2,
            id3,
        };
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsSystemCreate(
                _world,
                namePtr,
                phaseId,
                ids,
                3,
                (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                cookie
            );
        }

        SystemCount++;
    }

    public unsafe void RegisterSystem<T1, T2, T3, T4>(string name, EcsPhase phase,
        Action<Span<T1>, Span<T2>, Span<T3>, Span<T4>> body)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        var id1 = ComponentId<T1>();
        var id2 = ComponentId<T2>();
        var id3 = ComponentId<T3>();
        var id4 = ComponentId<T4>();
        var cookie = EcsSystemTable.Register(iterPtr =>
            {
                var n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                body(
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            0,
                            (nuint)Unsafe.SizeOf<T1>()
                        ),
                        n
                    ),
                    new Span<T2>(
                        (T2*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            1,
                            (nuint)Unsafe.SizeOf<T2>()
                        ),
                        n
                    ),
                    new Span<T3>(
                        (T3*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            2,
                            (nuint)Unsafe.SizeOf<T3>()
                        ),
                        n
                    ),
                    new Span<T4>(
                        (T4*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr,
                            3,
                            (nuint)Unsafe.SizeOf<T4>()
                        ),
                        n
                    )
                );
            }
        );
        var phaseId = NativeEngine.EcsBuiltinPhase((byte)phase);
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        var ids = stackalloc ulong[] {
            id1,
            id2,
            id3,
            id4,
        };
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsSystemCreate(
                _world,
                namePtr,
                phaseId,
                ids,
                4,
                (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                cookie
            );
        }

        SystemCount++;
    }

    // ── Observers (change notification) ─────────────────────────────────────────

    /// <summary>
    ///     Fire <paramref name="body" /> whenever <typeparamref name="T1" /> is set/added/removed on any
    ///     entity (OnSet also fires from <see cref="Set{T}" /> and <see cref="Modified{T}" />).
    ///     The callback runs SYNCHRONOUSLY inside the mutating call — do only cheap, non-structural work
    ///     (enqueue into a buffer; never add/remove components on the same entity here). The spans are
    ///     valid only for the duration of the call.
    /// </summary>
    public unsafe void RegisterObserver<T1>(string name, EcsEvent evt,
        Action<ReadOnlySpan<Entity>, Span<T1>> body)
        where T1 : unmanaged
    {
        var id1 = ComponentId<T1>();
        var cookie = EcsSystemTable.Register(iterPtr =>
            {
                var n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                var ents = new ReadOnlySpan<Entity>(
                    (Entity*)NativeEngine.EcsIterEntitiesFromPtr(iterPtr),
                    n
                );
                var data = new Span<T1>(
                    (T1*)NativeEngine.EcsIterFieldFromPtr(iterPtr, 0, (nuint)Unsafe.SizeOf<T1>()),
                    n
                );
                body(ents, data);
            }
        );
        var eventId = evt switch {
            EcsEvent.OnAdd => NativeEngine.EcsEventOnadd(),
            EcsEvent.OnRemove => NativeEngine.EcsEventOnremove(),
            _ => NativeEngine.EcsEventOnset(),
        };
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsObserverCreate(
                _world,
                namePtr,
                eventId,
                &id1,
                1,
                (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                cookie
            );
        }

        SystemCount++;
    }

    /// <summary>Tell flecs a component mutated via <see cref="Get{T}" /> changed — fires OnSet observers.</summary>
    public void Modified<T>(Entity e) where T : unmanaged
    {
        NativeEngine.EcsModified(_world, e.Raw, ComponentId<T>());
    }

    // ── Threading / pipeline tuning ──────────────────────────────────────────────

    /// <summary>
    ///     Worker-thread count for <see cref="Progress" /> (0/1 = single-threaded). NOTE: individual
    ///     systems must additionally be flagged multi-threaded to parallelise — that flag is not yet
    ///     exposed by <see cref="RegisterSystem{T1}" />, so this only spins up the thread pool today.
    /// </summary>
    public void SetThreads(int threads)
    {
        NativeEngine.EcsSetThreads(_world, threads);
    }

    public void SetTargetFps(float fps)
    {
        NativeEngine.EcsSetTargetFps(_world, fps);
    }

    // ── Relationships / hierarchy / prefabs ────────────────────────────────────

    public void SetParent(Entity child, Entity parent)
    {
        NativeEngine.EcsSetParent(_world, child.Raw, parent.Raw);
    }

    public Entity GetParent(Entity child)
    {
        return new Entity(NativeEngine.EcsGetParent(_world, child.Raw));
    }

    /// <summary>Add <c>(IsA, baseEntity)</c> relationship — child inherits base's components.</summary>
    public void IsA(Entity e, Entity baseEntity)
    {
        NativeEngine.EcsIsA(_world, e.Raw, baseEntity.Raw);
    }

    /// <summary>
    ///     Mark a component as SHARED across prefab instances (<c>(OnInstantiate, Inherit)</c>) instead of
    ///     copied. Required for true prefab inheritance: instances then resolve the value through
    ///     <c>(IsA, prefab)</c>, editing the prefab propagates, and <see cref="Owns{T}" /> reports
    ///     overrides.
    ///     Idempotent; set once per component type before instantiating.
    /// </summary>
    public void MakeInheritable<T>() where T : unmanaged
    {
        MakeInheritableId(ComponentId<T>());
    }

    public void MakeInheritable(Type t)
    {
        MakeInheritableId(ComponentId(t));
    }

    private void MakeInheritableId(ulong componentId)
    {
        NativeEngine.EcsAddPair(
            _world,
            componentId,
            NativeEngine.EcsBuiltinOninstantiate(),
            NativeEngine.EcsBuiltinInherit()
        );
    }

    public unsafe Entity NewPrefab(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            return new Entity(NativeEngine.EcsNewPrefab(_world, p));
        }
    }

    public Entity Instantiate(Entity prefab)
    {
        return new Entity(NativeEngine.EcsInstantiate(_world, prefab.Raw));
    }
}

/// <summary>
///     Archetype chunk iterator over a single component type.
/// </summary>
public sealed class Query<T1>(ulong world, ulong handle) : IDisposable
    where T1 : unmanaged
{
    public void Dispose()
    {
        NativeEngine.EcsQueryDestroy(handle);
    }

    public unsafe void Each(Action<Span<T1>> fn)
    {
        var it = NativeEngine.EcsQueryIter(world, handle);
        var completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                var n = NativeEngine.EcsIterCount(it);
                fn(
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterField(it, 0, (nuint)Unsafe.SizeOf<T1>()),
                        n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it, completed);
        }
    }

    public unsafe void Each(Action<ReadOnlySpan<Entity>, Span<T1>> fn)
    {
        var it = NativeEngine.EcsQueryIter(world, handle);
        var completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                var n = NativeEngine.EcsIterCount(it);
                fn(
                    new ReadOnlySpan<Entity>((Entity*)NativeEngine.EcsIterEntities(it), n),
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterField(it, 0, (nuint)Unsafe.SizeOf<T1>()),
                        n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it, completed);
        }
    }
}

/// <summary>
///     Archetype chunk iterator over two component types.
/// </summary>
public sealed class Query<T1, T2>(ulong world, ulong handle) : IDisposable
    where T1 : unmanaged where T2 : unmanaged
{
    public void Dispose()
    {
        NativeEngine.EcsQueryDestroy(handle);
    }

    public unsafe void Each(Action<Span<T1>, Span<T2>> fn)
    {
        var it = NativeEngine.EcsQueryIter(world, handle);
        var completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                var n = NativeEngine.EcsIterCount(it);
                fn(
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterField(it, 0, (nuint)Unsafe.SizeOf<T1>()),
                        n
                    ),
                    new Span<T2>(
                        (T2*)NativeEngine.EcsIterField(it, 1, (nuint)Unsafe.SizeOf<T2>()),
                        n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it, completed);
        }
    }

    public unsafe void Each(Action<ReadOnlySpan<Entity>, Span<T1>, Span<T2>> fn)
    {
        var it = NativeEngine.EcsQueryIter(world, handle);
        var completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                var n = NativeEngine.EcsIterCount(it);
                fn(
                    new ReadOnlySpan<Entity>((Entity*)NativeEngine.EcsIterEntities(it), n),
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterField(it, 0, (nuint)Unsafe.SizeOf<T1>()),
                        n
                    ),
                    new Span<T2>(
                        (T2*)NativeEngine.EcsIterField(it, 1, (nuint)Unsafe.SizeOf<T2>()),
                        n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it, completed);
        }
    }
}

/// <summary>
///     Archetype chunk iterator over three component types.
/// </summary>
public sealed class Query<T1, T2, T3>(ulong world, ulong handle) : IDisposable
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    public void Dispose()
    {
        NativeEngine.EcsQueryDestroy(handle);
    }

    public unsafe void Each(Action<Span<T1>, Span<T2>, Span<T3>> fn)
    {
        var it = NativeEngine.EcsQueryIter(world, handle);
        var completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                var n = NativeEngine.EcsIterCount(it);
                fn(
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterField(it, 0, (nuint)Unsafe.SizeOf<T1>()),
                        n
                    ),
                    new Span<T2>(
                        (T2*)NativeEngine.EcsIterField(it, 1, (nuint)Unsafe.SizeOf<T2>()),
                        n
                    ),
                    new Span<T3>(
                        (T3*)NativeEngine.EcsIterField(it, 2, (nuint)Unsafe.SizeOf<T3>()),
                        n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it, completed);
        }
    }

    public unsafe void Each(Action<ReadOnlySpan<Entity>, Span<T1>, Span<T2>, Span<T3>> fn)
    {
        var it = NativeEngine.EcsQueryIter(world, handle);
        var completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                var n = NativeEngine.EcsIterCount(it);
                fn(
                    new ReadOnlySpan<Entity>((Entity*)NativeEngine.EcsIterEntities(it), n),
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterField(it, 0, (nuint)Unsafe.SizeOf<T1>()),
                        n
                    ),
                    new Span<T2>(
                        (T2*)NativeEngine.EcsIterField(it, 1, (nuint)Unsafe.SizeOf<T2>()),
                        n
                    ),
                    new Span<T3>(
                        (T3*)NativeEngine.EcsIterField(it, 2, (nuint)Unsafe.SizeOf<T3>()),
                        n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it, completed);
        }
    }
}

/// <summary>
///     Archetype chunk iterator over four component types.
/// </summary>
public sealed class Query<T1, T2, T3, T4>(ulong world, ulong handle) : IDisposable
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    public void Dispose()
    {
        NativeEngine.EcsQueryDestroy(handle);
    }

    public unsafe void Each(Action<Span<T1>, Span<T2>, Span<T3>, Span<T4>> fn)
    {
        var it = NativeEngine.EcsQueryIter(world, handle);
        var completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                var n = NativeEngine.EcsIterCount(it);
                fn(
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterField(it, 0, (nuint)Unsafe.SizeOf<T1>()),
                        n
                    ),
                    new Span<T2>(
                        (T2*)NativeEngine.EcsIterField(it, 1, (nuint)Unsafe.SizeOf<T2>()),
                        n
                    ),
                    new Span<T3>(
                        (T3*)NativeEngine.EcsIterField(it, 2, (nuint)Unsafe.SizeOf<T3>()),
                        n
                    ),
                    new Span<T4>(
                        (T4*)NativeEngine.EcsIterField(it, 3, (nuint)Unsafe.SizeOf<T4>()),
                        n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it, completed);
        }
    }

    public unsafe void Each(
        Action<ReadOnlySpan<Entity>, Span<T1>, Span<T2>, Span<T3>, Span<T4>> fn)
    {
        var it = NativeEngine.EcsQueryIter(world, handle);
        var completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                var n = NativeEngine.EcsIterCount(it);
                fn(
                    new ReadOnlySpan<Entity>((Entity*)NativeEngine.EcsIterEntities(it), n),
                    new Span<T1>(
                        (T1*)NativeEngine.EcsIterField(it, 0, (nuint)Unsafe.SizeOf<T1>()),
                        n
                    ),
                    new Span<T2>(
                        (T2*)NativeEngine.EcsIterField(it, 1, (nuint)Unsafe.SizeOf<T2>()),
                        n
                    ),
                    new Span<T3>(
                        (T3*)NativeEngine.EcsIterField(it, 2, (nuint)Unsafe.SizeOf<T3>()),
                        n
                    ),
                    new Span<T4>(
                        (T4*)NativeEngine.EcsIterField(it, 3, (nuint)Unsafe.SizeOf<T4>()),
                        n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it, completed);
        }
    }
}

/// <summary>
///     Releases a pull-query iterator after an <c>Each</c> loop.
///     A loop that ran <c>ecs_query_next</c> to a false return was already finalized by flecs —
///     only the IterBox wrapper may be freed (a second <c>ecs_iter_fini</c> would double-free).
///     A loop interrupted by a callback exception still owns a live flecs iterator and must
///     run <c>ecs_iter_fini</c> or the iterator's native resources leak.
/// </summary>
internal static class QueryIterRelease
{
    internal static void Release(ulong it, bool completed)
    {
        if (completed) NativeEngine.EcsIterFree(it);
        else NativeEngine.EcsIterFini(it);
    }
}

/// <summary>
///     Static callback dispatch table for flecs system/observer thunks.
///     Uses a cookie (ulong ctx) stored in <see cref="ecs_iter_t.ctx" /> to route each
///     system tick to the corresponding C# delegate — the same pattern as <c>MacMenu</c>.
/// </summary>
internal static class EcsSystemTable
{
    private static long _nextId;
    private static readonly object RegisterLock = new();

    // Copy-on-write: Register swaps in a fresh dictionary so Dispatch (every system, every tick)
    // reads the current snapshot lock-free.
    private static volatile Dictionary<ulong, Slot> _slots = new();

    internal static ulong Register(Action<nuint> handler)
    {
        var id = (ulong)Interlocked.Increment(ref _nextId);
        lock (RegisterLock)
        {
            var next = new Dictionary<ulong, Slot>(_slots) { [id] = new(handler) };
            _slots = next;
        }

        return id;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void Dispatch(nuint iterPtr)
    {
        // UnmanagedCallersOnly — no exception may ever escape into native flecs.
        try
        {
            var cookie = NativeEngine.EcsIterCtx(iterPtr);
            if (!_slots.TryGetValue(cookie, out var slot)) return;
            try
            {
                slot.Handler(iterPtr);
            }
            catch (Exception ex)
            {
                if (!slot.Faulted)
                {
                    slot.Faulted = true;
                    Console.Error.WriteLine(
                        $"[EcsSystemTable] system #{cookie} threw (further exceptions from it are suppressed):\n{ex}"
                    );
                }
            }
        }
        catch
        {
        }
    }

    private sealed class Slot(Action<nuint> handler)
    {
        internal readonly Action<nuint> Handler = handler;
        internal bool Faulted;
    }
}