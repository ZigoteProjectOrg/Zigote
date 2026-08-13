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

    public EcsWorld() => _world = NativeEngine.EcsWorldCreate();

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
        if (_componentIds.TryGetValue(key: typeof(T), value: out ulong id)) return id;
        byte[] nameBytes = Encoding.UTF8.GetBytes(typeof(T).FullName! + "\0");
        fixed (byte* p = nameBytes)
        {
            id = NativeEngine.EcsComponentRegister(
                world: _world,
                name: p,
                size: (nuint)Unsafe.SizeOf<T>(),
                alignment: AlignOf<T>()
            );
        }

        return _componentIds[typeof(T)] = id;
    }

    private static nuint AlignOf<T>() where T : unmanaged => AlignOfSize((nuint)Unsafe.SizeOf<T>());

    private static nuint AlignOfSize(nuint sz) =>
        sz switch { >= 8 => 8, >= 4 => 4, >= 2 => 2, _ => 1 };

    // ── Entity lifecycle ────────────────────────────────────────────────────────

    public Entity CreateEntity()
    {
        var e = new Entity(NativeEngine.EcsEntityCreate(_world));
        EntityCount++;
        return e;
    }

    public unsafe Entity CreateEntity(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            var e = new Entity(NativeEngine.EcsEntityCreateNamed(world: _world, name: p));
            EntityCount++;
            return e;
        }
    }

    public void DestroyEntity(Entity e)
    {
        if (!IsAlive(e)) return;
        NativeEngine.EcsEntityDestroy(world: _world, entity: e.Raw);
        EntityCount--;
    }

    public bool IsAlive(Entity e) =>
        e.Raw != 0 && NativeEngine.EcsEntityIsAlive(world: _world, entity: e.Raw) != 0;

    // ── Component operations ────────────────────────────────────────────────────

    /// <summary>Add the component type (zero-initialised) without setting a value.</summary>
    public void Add<T>(Entity e) where T : unmanaged => NativeEngine.EcsAdd(
        world: _world,
        entity: e.Raw,
        component: ComponentId<T>()
    );

    /// <summary>Add (if absent) and set the component value.</summary>
    public unsafe void Set<T>(Entity e, in T c) where T : unmanaged
    {
        ulong id = ComponentId<T>();
        fixed (T* p = &c)
        {
            NativeEngine.EcsSet(
                world: _world,
                entity: e.Raw,
                component: id,
                data: (byte*)p,
                size: (nuint)Unsafe.SizeOf<T>()
            );
        }
    }

    /// <summary>Convenience overload: <c>Add(e, value)</c> = <c>Set(e, value)</c>.</summary>
    public void Add<T>(Entity e, in T c) where T : unmanaged => Set(e: e, c: c);

    public bool Has<T>(Entity e) where T : unmanaged => NativeEngine.EcsHas(
        world: _world,
        entity: e.Raw,
        component: ComponentId<T>()
    ) != 0;

    /// <summary>
    ///     True if the entity has <typeparamref name="T" /> on ITSELF (not merely inherited via a
    ///     <c>(IsA, prefab)</c> link). Distinguishes a prefab-instance override from an inherited value.
    /// </summary>
    public bool Owns<T>(Entity e) where T : unmanaged => NativeEngine.EcsOwns(
        world: _world,
        entity: e.Raw,
        component: ComponentId<T>()
    ) != 0;

    /// <summary>
    ///     Returns a <see cref="Span{T}" /> into flecs native storage.
    ///     Do NOT store the ref across any operation that could restructure the archetype
    ///     (add/remove/destroy on the same entity). Use <see cref="Defer" /> for safe batched mutations.
    /// </summary>
    public unsafe ref T Get<T>(Entity e) where T : unmanaged
    {
        byte* ptr = NativeEngine.EcsGetMut(
            world: _world,
            entity: e.Raw,
            component: ComponentId<T>()
        );
        if (ptr == null)
            throw new InvalidOperationException($"Entity {e} has no component {typeof(T).Name}.");
        return ref Unsafe.AsRef<T>(ptr);
    }

    public unsafe bool TryGet<T>(Entity e, out T value) where T : unmanaged
    {
        byte* ptr = NativeEngine.EcsGet(world: _world, entity: e.Raw, component: ComponentId<T>());
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
        NativeEngine.EcsRemove(world: _world, entity: e.Raw, component: ComponentId<T>());
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
        if (_componentIds.TryGetValue(key: t, value: out ulong id)) return id;
        UIntPtr size = (nuint)Marshal.SizeOf(t);
        byte[] nameBytes = Encoding.UTF8.GetBytes(t.FullName! + "\0");
        fixed (byte* p = nameBytes)
        {
            id = NativeEngine.EcsComponentRegister(
                world: _world,
                name: p,
                size: size,
                alignment: AlignOfSize(size)
            );
        }

        return _componentIds[t] = id;
    }

    public bool Has(Entity e, Type t) => NativeEngine.EcsHas(
        world: _world,
        entity: e.Raw,
        component: ComponentId(t)
    ) != 0;

    public bool Owns(Entity e, Type t) => NativeEngine.EcsOwns(
        world: _world,
        entity: e.Raw,
        component: ComponentId(t)
    ) != 0;

    /// <summary>
    ///     Remove a component by runtime Type. On a prefab instance this reverts an override to
    ///     inherited.
    /// </summary>
    public bool Remove(Entity e, Type t)
    {
        if (!Owns(e: e, t: t))
        {
            return
                false; // only an OWNED component can be removed; inherited ones aren't "present" to remove
        }

        NativeEngine.EcsRemove(world: _world, entity: e.Raw, component: ComponentId(t));
        return true;
    }

    /// <summary>Read a component as a boxed struct of <paramref name="t" /> (null if absent).</summary>
    public unsafe object? GetBoxed(Entity e, Type t)
    {
        byte* ptr = NativeEngine.EcsGet(world: _world, entity: e.Raw, component: ComponentId(t));
        return ptr == null ? null : Marshal.PtrToStructure(ptr: (IntPtr)ptr, structureType: t);
    }

    /// <summary>Add (if absent) and set a component from a boxed struct of <paramref name="t" />.</summary>
    public unsafe void SetBoxed(Entity e, Type t, object value)
    {
        int size = Marshal.SizeOf(t);
        var buf = size <= 256 ? stackalloc byte[size] : new byte[size];
        fixed (byte* p = buf)
        {
            Marshal.StructureToPtr(structure: value, ptr: (IntPtr)p, fDeleteOld: false);
            NativeEngine.EcsSet(
                world: _world,
                entity: e.Raw,
                component: ComponentId(t),
                data: p,
                size: (nuint)size
            );
        }
    }

    // ── Systems pipeline ────────────────────────────────────────────────────────

    /// <summary>Run all OnUpdate (and dependent) systems once. Returns false to signal shutdown.</summary>
    public bool Progress(float dt = 0f) => NativeEngine.EcsProgress(world: _world, dt: dt) != 0;

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
        ulong id = ComponentId<T1>();
        ulong q = NativeEngine.EcsQueryCreate(world: _world, components: &id, count: 1);
        return new Query<T1>(world: _world, handle: q);
    }

    public unsafe Query<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged
    {
        ulong* ids = stackalloc ulong[] {
            ComponentId<T1>(),
            ComponentId<T2>(),
        };
        ulong q = NativeEngine.EcsQueryCreate(world: _world, components: ids, count: 2);
        return new Query<T1, T2>(world: _world, handle: q);
    }

    public unsafe Query<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        ulong* ids = stackalloc ulong[] {
            ComponentId<T1>(),
            ComponentId<T2>(),
            ComponentId<T3>(),
        };
        ulong q = NativeEngine.EcsQueryCreate(world: _world, components: ids, count: 3);
        return new Query<T1, T2, T3>(world: _world, handle: q);
    }

    public unsafe Query<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        ulong* ids = stackalloc ulong[] {
            ComponentId<T1>(),
            ComponentId<T2>(),
            ComponentId<T3>(),
            ComponentId<T4>(),
        };
        ulong q = NativeEngine.EcsQueryCreate(world: _world, components: ids, count: 4);
        return new Query<T1, T2, T3, T4>(world: _world, handle: q);
    }

    private Query<T1> CachedQuery<T1>() where T1 : unmanaged
    {
        var key = (ComponentId<T1>(), 0UL, 0UL, 0UL);
        if (_queryCache.TryGetValue(key: key, value: out var cached)) return (Query<T1>)cached;
        var q = Query<T1>();
        _queryCache[key] = q;
        return q;
    }

    private Query<T1, T2> CachedQuery<T1, T2>() where T1 : unmanaged where T2 : unmanaged
    {
        var key = (ComponentId<T1>(), ComponentId<T2>(), 0UL, 0UL);
        if (_queryCache.TryGetValue(key: key, value: out var cached)) return (Query<T1, T2>)cached;
        var q = Query<T1, T2>();
        _queryCache[key] = q;
        return q;
    }

    private Query<T1, T2, T3> CachedQuery<T1, T2, T3>()
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        var key = (ComponentId<T1>(), ComponentId<T2>(), ComponentId<T3>(), 0UL);
        if (_queryCache.TryGetValue(key: key, value: out var cached))
            return (Query<T1, T2, T3>)cached;
        var q = Query<T1, T2, T3>();
        _queryCache[key] = q;
        return q;
    }

    private Query<T1, T2, T3, T4> CachedQuery<T1, T2, T3, T4>()
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        var key = (ComponentId<T1>(), ComponentId<T2>(), ComponentId<T3>(), ComponentId<T4>());
        if (_queryCache.TryGetValue(key: key, value: out var cached))
            return (Query<T1, T2, T3, T4>)cached;
        var q = Query<T1, T2, T3, T4>();
        _queryCache[key] = q;
        return q;
    }

    /// <summary>Convenience: iterate every entity that has <typeparamref name="T1" />.</summary>
    public void ForEach<T1>(Action<Span<T1>> body) where T1 : unmanaged =>
        CachedQuery<T1>().Each(body);

    /// <summary>
    ///     Convenience: iterate every entity that has both <typeparamref name="T1" /> and
    ///     <typeparamref name="T2" />.
    /// </summary>
    public void ForEach<T1, T2>(Action<Span<T1>, Span<T2>> body)
        where T1 : unmanaged where T2 : unmanaged =>
        CachedQuery<T1, T2>().Each(body);

    /// <summary>Convenience: iterate every entity that has all three component types.</summary>
    public void ForEach<T1, T2, T3>(Action<Span<T1>, Span<T2>, Span<T3>> body)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged =>
        CachedQuery<T1, T2, T3>().Each(body);

    /// <summary>Convenience: iterate every entity that has all four component types.</summary>
    public void ForEach<T1, T2, T3, T4>(Action<Span<T1>, Span<T2>, Span<T3>, Span<T4>> body)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged =>
        CachedQuery<T1, T2, T3, T4>().Each(body);

    // ── System registration ─────────────────────────────────────────────────────

    public unsafe void RegisterSystem<T1>(string name, EcsPhase phase, Action<Span<T1>> body)
        where T1 : unmanaged
    {
        ulong id1 = ComponentId<T1>();
        ulong cookie = EcsSystemTable.Register(iterPtr =>
            {
                int n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                body(
                    new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    )
                );
            }
        );
        ulong phaseId = NativeEngine.EcsBuiltinPhase((byte)phase);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsSystemCreate(
                world: _world,
                name: namePtr,
                phase: phaseId,
                components: &id1,
                count: 1,
                callback: (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                ctx: cookie
            );
        }

        SystemCount++;
    }

    public unsafe void RegisterSystem<T1, T2>(string name, EcsPhase phase,
        Action<Span<T1>, Span<T2>> body)
        where T1 : unmanaged where T2 : unmanaged
    {
        ulong id1 = ComponentId<T1>();
        ulong id2 = ComponentId<T2>();
        ulong cookie = EcsSystemTable.Register(iterPtr =>
            {
                int n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                body(
                    arg1: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    ),
                    arg2: new Span<T2>(
                        pointer: (T2*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 1,
                            size: (nuint)Unsafe.SizeOf<T2>()
                        ),
                        length: n
                    )
                );
            }
        );
        ulong phaseId = NativeEngine.EcsBuiltinPhase((byte)phase);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        ulong* ids = stackalloc ulong[] {
            id1,
            id2,
        };
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsSystemCreate(
                world: _world,
                name: namePtr,
                phase: phaseId,
                components: ids,
                count: 2,
                callback: (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                ctx: cookie
            );
        }

        SystemCount++;
    }

    public unsafe void RegisterSystem<T1, T2, T3>(string name, EcsPhase phase,
        Action<Span<T1>, Span<T2>, Span<T3>> body)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        ulong id1 = ComponentId<T1>();
        ulong id2 = ComponentId<T2>();
        ulong id3 = ComponentId<T3>();
        ulong cookie = EcsSystemTable.Register(iterPtr =>
            {
                int n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                body(
                    arg1: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    ),
                    arg2: new Span<T2>(
                        pointer: (T2*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 1,
                            size: (nuint)Unsafe.SizeOf<T2>()
                        ),
                        length: n
                    ),
                    arg3: new Span<T3>(
                        pointer: (T3*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 2,
                            size: (nuint)Unsafe.SizeOf<T3>()
                        ),
                        length: n
                    )
                );
            }
        );
        ulong phaseId = NativeEngine.EcsBuiltinPhase((byte)phase);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        ulong* ids = stackalloc ulong[] {
            id1,
            id2,
            id3,
        };
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsSystemCreate(
                world: _world,
                name: namePtr,
                phase: phaseId,
                components: ids,
                count: 3,
                callback: (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                ctx: cookie
            );
        }

        SystemCount++;
    }

    public unsafe void RegisterSystem<T1, T2, T3, T4>(string name, EcsPhase phase,
        Action<Span<T1>, Span<T2>, Span<T3>, Span<T4>> body)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        ulong id1 = ComponentId<T1>();
        ulong id2 = ComponentId<T2>();
        ulong id3 = ComponentId<T3>();
        ulong id4 = ComponentId<T4>();
        ulong cookie = EcsSystemTable.Register(iterPtr =>
            {
                int n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                body(
                    arg1: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    ),
                    arg2: new Span<T2>(
                        pointer: (T2*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 1,
                            size: (nuint)Unsafe.SizeOf<T2>()
                        ),
                        length: n
                    ),
                    arg3: new Span<T3>(
                        pointer: (T3*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 2,
                            size: (nuint)Unsafe.SizeOf<T3>()
                        ),
                        length: n
                    ),
                    arg4: new Span<T4>(
                        pointer: (T4*)NativeEngine.EcsIterFieldFromPtr(
                            iterPtr: iterPtr,
                            termIndex: 3,
                            size: (nuint)Unsafe.SizeOf<T4>()
                        ),
                        length: n
                    )
                );
            }
        );
        ulong phaseId = NativeEngine.EcsBuiltinPhase((byte)phase);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        ulong* ids = stackalloc ulong[] {
            id1,
            id2,
            id3,
            id4,
        };
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsSystemCreate(
                world: _world,
                name: namePtr,
                phase: phaseId,
                components: ids,
                count: 4,
                callback: (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                ctx: cookie
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
        ulong id1 = ComponentId<T1>();
        ulong cookie = EcsSystemTable.Register(iterPtr =>
            {
                int n = NativeEngine.EcsIterCountFromPtr(iterPtr);
                if (n == 0) return;
                var ents = new ReadOnlySpan<Entity>(
                    pointer: (Entity*)NativeEngine.EcsIterEntitiesFromPtr(iterPtr),
                    length: n
                );
                var data = new Span<T1>(
                    pointer: (T1*)NativeEngine.EcsIterFieldFromPtr(
                        iterPtr: iterPtr,
                        termIndex: 0,
                        size: (nuint)Unsafe.SizeOf<T1>()
                    ),
                    length: n
                );
                body(arg1: ents, arg2: data);
            }
        );
        ulong eventId = evt switch {
            EcsEvent.OnAdd => NativeEngine.EcsEventOnadd(),
            EcsEvent.OnRemove => NativeEngine.EcsEventOnremove(),
            _ => NativeEngine.EcsEventOnset(),
        };
        byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* namePtr = nameBytes)
        {
            NativeEngine.EcsObserverCreate(
                world: _world,
                name: namePtr,
                evt: eventId,
                components: &id1,
                count: 1,
                callback: (nuint)(delegate* unmanaged[Cdecl]<nuint, void>)&EcsSystemTable.Dispatch,
                ctx: cookie
            );
        }

        SystemCount++;
    }

    /// <summary>Tell flecs a component mutated via <see cref="Get{T}" /> changed — fires OnSet observers.</summary>
    public void Modified<T>(Entity e) where T : unmanaged => NativeEngine.EcsModified(
        world: _world,
        entity: e.Raw,
        component: ComponentId<T>()
    );

    // ── Threading / pipeline tuning ──────────────────────────────────────────────

    /// <summary>
    ///     Worker-thread count for <see cref="Progress" /> (0/1 = single-threaded). NOTE: individual
    ///     systems must additionally be flagged multi-threaded to parallelise — that flag is not yet
    ///     exposed by <see cref="RegisterSystem{T1}" />, so this only spins up the thread pool today.
    /// </summary>
    public void SetThreads(int threads) =>
        NativeEngine.EcsSetThreads(world: _world, threads: threads);

    public void SetTargetFps(float fps) => NativeEngine.EcsSetTargetFps(world: _world, fps: fps);

    // ── Relationships / hierarchy / prefabs ────────────────────────────────────

    public void SetParent(Entity child, Entity parent) => NativeEngine.EcsSetParent(
        world: _world,
        child: child.Raw,
        parent: parent.Raw
    );

    public Entity GetParent(Entity child) =>
        new(NativeEngine.EcsGetParent(world: _world, child: child.Raw));

    /// <summary>Add <c>(IsA, baseEntity)</c> relationship — child inherits base's components.</summary>
    public void IsA(Entity e, Entity baseEntity) => NativeEngine.EcsIsA(
        world: _world,
        e: e.Raw,
        baseEntity: baseEntity.Raw
    );

    /// <summary>
    ///     Mark a component as SHARED across prefab instances (<c>(OnInstantiate, Inherit)</c>) instead of
    ///     copied. Required for true prefab inheritance: instances then resolve the value through
    ///     <c>(IsA, prefab)</c>, editing the prefab propagates, and <see cref="Owns{T}" /> reports
    ///     overrides.
    ///     Idempotent; set once per component type before instantiating.
    /// </summary>
    public void MakeInheritable<T>() where T : unmanaged => MakeInheritableId(ComponentId<T>());

    public void MakeInheritable(Type t) => MakeInheritableId(ComponentId(t));

    private void MakeInheritableId(ulong componentId)
    {
        NativeEngine.EcsAddPair(
            world: _world,
            e: componentId,
            relation: NativeEngine.EcsBuiltinOninstantiate(),
            target: NativeEngine.EcsBuiltinInherit()
        );
    }

    public unsafe Entity NewPrefab(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p =
                   bytes) return new Entity(NativeEngine.EcsNewPrefab(world: _world, name: p));
    }

    public Entity Instantiate(Entity prefab) =>
        new(NativeEngine.EcsInstantiate(world: _world, prefab: prefab.Raw));
}

/// <summary>
///     Archetype chunk iterator over a single component type.
/// </summary>
public sealed class Query<T1>(ulong world, ulong handle) : IDisposable
    where T1 : unmanaged
{
    public void Dispose() => NativeEngine.EcsQueryDestroy(handle);

    public unsafe void Each(Action<Span<T1>> fn)
    {
        ulong it = NativeEngine.EcsQueryIter(world: world, query: handle);
        bool completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                int n = NativeEngine.EcsIterCount(it);
                fn(
                    new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it: it, completed: completed);
        }
    }

    public unsafe void Each(Action<ReadOnlySpan<Entity>, Span<T1>> fn)
    {
        ulong it = NativeEngine.EcsQueryIter(world: world, query: handle);
        bool completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                int n = NativeEngine.EcsIterCount(it);
                fn(
                    arg1: new ReadOnlySpan<Entity>(
                        pointer: (Entity*)NativeEngine.EcsIterEntities(it),
                        length: n
                    ),
                    arg2: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it: it, completed: completed);
        }
    }
}

/// <summary>
///     Archetype chunk iterator over two component types.
/// </summary>
public sealed class Query<T1, T2>(ulong world, ulong handle) : IDisposable
    where T1 : unmanaged where T2 : unmanaged
{
    public void Dispose() => NativeEngine.EcsQueryDestroy(handle);

    public unsafe void Each(Action<Span<T1>, Span<T2>> fn)
    {
        ulong it = NativeEngine.EcsQueryIter(world: world, query: handle);
        bool completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                int n = NativeEngine.EcsIterCount(it);
                fn(
                    arg1: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    ),
                    arg2: new Span<T2>(
                        pointer: (T2*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 1,
                            size: (nuint)Unsafe.SizeOf<T2>()
                        ),
                        length: n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it: it, completed: completed);
        }
    }

    public unsafe void Each(Action<ReadOnlySpan<Entity>, Span<T1>, Span<T2>> fn)
    {
        ulong it = NativeEngine.EcsQueryIter(world: world, query: handle);
        bool completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                int n = NativeEngine.EcsIterCount(it);
                fn(
                    arg1: new ReadOnlySpan<Entity>(
                        pointer: (Entity*)NativeEngine.EcsIterEntities(it),
                        length: n
                    ),
                    arg2: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    ),
                    arg3: new Span<T2>(
                        pointer: (T2*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 1,
                            size: (nuint)Unsafe.SizeOf<T2>()
                        ),
                        length: n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it: it, completed: completed);
        }
    }
}

/// <summary>
///     Archetype chunk iterator over three component types.
/// </summary>
public sealed class Query<T1, T2, T3>(ulong world, ulong handle) : IDisposable
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    public void Dispose() => NativeEngine.EcsQueryDestroy(handle);

    public unsafe void Each(Action<Span<T1>, Span<T2>, Span<T3>> fn)
    {
        ulong it = NativeEngine.EcsQueryIter(world: world, query: handle);
        bool completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                int n = NativeEngine.EcsIterCount(it);
                fn(
                    arg1: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    ),
                    arg2: new Span<T2>(
                        pointer: (T2*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 1,
                            size: (nuint)Unsafe.SizeOf<T2>()
                        ),
                        length: n
                    ),
                    arg3: new Span<T3>(
                        pointer: (T3*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 2,
                            size: (nuint)Unsafe.SizeOf<T3>()
                        ),
                        length: n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it: it, completed: completed);
        }
    }

    public unsafe void Each(Action<ReadOnlySpan<Entity>, Span<T1>, Span<T2>, Span<T3>> fn)
    {
        ulong it = NativeEngine.EcsQueryIter(world: world, query: handle);
        bool completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                int n = NativeEngine.EcsIterCount(it);
                fn(
                    arg1: new ReadOnlySpan<Entity>(
                        pointer: (Entity*)NativeEngine.EcsIterEntities(it),
                        length: n
                    ),
                    arg2: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    ),
                    arg3: new Span<T2>(
                        pointer: (T2*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 1,
                            size: (nuint)Unsafe.SizeOf<T2>()
                        ),
                        length: n
                    ),
                    arg4: new Span<T3>(
                        pointer: (T3*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 2,
                            size: (nuint)Unsafe.SizeOf<T3>()
                        ),
                        length: n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it: it, completed: completed);
        }
    }
}

/// <summary>
///     Archetype chunk iterator over four component types.
/// </summary>
public sealed class Query<T1, T2, T3, T4>(ulong world, ulong handle) : IDisposable
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    public void Dispose() => NativeEngine.EcsQueryDestroy(handle);

    public unsafe void Each(Action<Span<T1>, Span<T2>, Span<T3>, Span<T4>> fn)
    {
        ulong it = NativeEngine.EcsQueryIter(world: world, query: handle);
        bool completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                int n = NativeEngine.EcsIterCount(it);
                fn(
                    arg1: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    ),
                    arg2: new Span<T2>(
                        pointer: (T2*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 1,
                            size: (nuint)Unsafe.SizeOf<T2>()
                        ),
                        length: n
                    ),
                    arg3: new Span<T3>(
                        pointer: (T3*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 2,
                            size: (nuint)Unsafe.SizeOf<T3>()
                        ),
                        length: n
                    ),
                    arg4: new Span<T4>(
                        pointer: (T4*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 3,
                            size: (nuint)Unsafe.SizeOf<T4>()
                        ),
                        length: n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it: it, completed: completed);
        }
    }

    public unsafe void Each(
        Action<ReadOnlySpan<Entity>, Span<T1>, Span<T2>, Span<T3>, Span<T4>> fn)
    {
        ulong it = NativeEngine.EcsQueryIter(world: world, query: handle);
        bool completed = false;
        try
        {
            while (NativeEngine.EcsQueryNext(it) != 0)
            {
                int n = NativeEngine.EcsIterCount(it);
                fn(
                    arg1: new ReadOnlySpan<Entity>(
                        pointer: (Entity*)NativeEngine.EcsIterEntities(it),
                        length: n
                    ),
                    arg2: new Span<T1>(
                        pointer: (T1*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 0,
                            size: (nuint)Unsafe.SizeOf<T1>()
                        ),
                        length: n
                    ),
                    arg3: new Span<T2>(
                        pointer: (T2*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 1,
                            size: (nuint)Unsafe.SizeOf<T2>()
                        ),
                        length: n
                    ),
                    arg4: new Span<T3>(
                        pointer: (T3*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 2,
                            size: (nuint)Unsafe.SizeOf<T3>()
                        ),
                        length: n
                    ),
                    arg5: new Span<T4>(
                        pointer: (T4*)NativeEngine.EcsIterField(
                            iter: it,
                            termIndex: 3,
                            size: (nuint)Unsafe.SizeOf<T4>()
                        ),
                        length: n
                    )
                );
            }

            completed = true;
        }
        finally
        {
            QueryIterRelease.Release(it: it, completed: completed);
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
        ulong id = (ulong)Interlocked.Increment(ref _nextId);
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
            ulong cookie = NativeEngine.EcsIterCtx(iterPtr);
            if (!_slots.TryGetValue(key: cookie, value: out var slot)) return;
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
        catch { }
    }

    private sealed class Slot(Action<nuint> handler)
    {
        internal readonly Action<nuint> Handler = handler;
        internal bool Faulted;
    }
}
