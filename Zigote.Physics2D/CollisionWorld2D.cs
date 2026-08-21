using Zigote.Core.Math3D;

namespace Zigote.Physics2D;

/// <summary>
///     Pure-C# 2D collision world: axis-aligned boxes + circles over a uniform-grid broadphase, with
///     overlap / raycast / swept-AABB queries and one-way-platform semantics. No dynamics — bodies are
///     positioned by the caller (<see cref="SetPosition" />); the <see cref="CharacterController2D" />
///     does its own sweep-and-slide on top. Deterministic, zero native code, headless-testable.
/// </summary>
public sealed class CollisionWorld2D(float cellSize = 2f)
{
    /// <summary>
    ///     How far a mover's bottom may start *below* a one-way platform's top and still be blocked by it.
    ///     2× the controller's default skin width, so a character resting a skin-gap above the platform
    ///     (or penetrating it by up to one skin) is still supported, while anything genuinely inside or
    ///     under the platform falls through.
    /// </summary>
    public const float OneWayEpsilon = 0.02f;

    private const float DirEpsilon = 1e-8f;

    private readonly List<uint> _candidates = [];
    private readonly float _cellSize = cellSize > 0f ? cellSize : 2f;
    private readonly Dictionary<long, List<uint>> _cells = new();
    private readonly Dictionary<uint, Collider> _colliders = new();
    private uint _nextId = 1;
    private int _stamp;

    public int Count => _colliders.Count;

    // ── Colliders ────────────────────────────────────────────────────────────

    public ColliderHandle AddBox(Vec2 center, Vec2 halfExtents, uint layer = 1,
        bool isTrigger = false,
        bool oneWayUp = false, object? userData = null)
    {
        var col = new Collider {
            Shape = ColliderShape2D.Box,
            Center = center,
            HalfExtents = halfExtents,
            Layer = layer,
            IsTrigger = isTrigger,
            OneWayUp = oneWayUp,
            UserData = userData,
        };
        return Insert(col);
    }

    public ColliderHandle AddCircle(Vec2 center, float radius, uint layer = 1,
        bool isTrigger = false,
        object? userData = null)
    {
        var col = new Collider {
            Shape = ColliderShape2D.Circle,
            Center = center,
            HalfExtents = new Vec2(x: radius, y: radius),
            Radius = radius,
            Layer = layer,
            IsTrigger = isTrigger,
            UserData = userData,
        };
        return Insert(col);
    }

    public void Remove(ColliderHandle handle)
    {
        if (!_colliders.TryGetValue(key: handle.Id, value: out var col)) return;
        RemoveFromCells(id: handle.Id, col: col);
        _colliders.Remove(handle.Id);
    }

    public bool IsAlive(ColliderHandle handle) => _colliders.ContainsKey(handle.Id);

    public Vec2 GetPosition(ColliderHandle handle) =>
        _colliders.TryGetValue(key: handle.Id, value: out var col) ? col.Center : Vec2.Zero;

    public void SetPosition(ColliderHandle handle, Vec2 position)
    {
        if (!_colliders.TryGetValue(key: handle.Id, value: out var col)) return;
        col.MoveDelta += position - col.Center;
        col.Center = position;
        Reindex(id: handle.Id, col: col);
    }

    /// <summary>
    ///     Movement accumulated by <see cref="SetPosition" /> since the last <see cref="BeginStep" /> —
    ///     how riders are
    ///     carried.
    /// </summary>
    public Vec2 GetMoveDelta(ColliderHandle handle) =>
        _colliders.TryGetValue(key: handle.Id, value: out var col) ? col.MoveDelta : Vec2.Zero;

    /// <summary>Call once per fixed tick, before moving platforms, to reset every collider's move delta.</summary>
    public void BeginStep()
    {
        foreach (var col in _colliders.Values) col.MoveDelta = Vec2.Zero;
    }

    public object? GetUserData(ColliderHandle handle) =>
        _colliders.TryGetValue(key: handle.Id, value: out var col) ? col.UserData : null;

    public uint GetLayer(ColliderHandle handle) =>
        _colliders.TryGetValue(key: handle.Id, value: out var col) ? col.Layer : 0;

    public void SetLayer(ColliderHandle handle, uint layer)
    {
        if (_colliders.TryGetValue(key: handle.Id, value: out var col)) col.Layer = layer;
    }

    public ColliderShape2D GetShape(ColliderHandle handle) =>
        _colliders.TryGetValue(key: handle.Id, value: out var col)
            ? col.Shape
            : ColliderShape2D.Box;

    /// <summary>Box half-extents; for circles, (radius, radius).</summary>
    public Vec2 GetHalfExtents(ColliderHandle handle) =>
        _colliders.TryGetValue(key: handle.Id, value: out var col) ? col.HalfExtents : Vec2.Zero;

    public float GetRadius(ColliderHandle handle) =>
        _colliders.TryGetValue(key: handle.Id, value: out var col) ? col.Radius : 0f;

    public bool IsTrigger(ColliderHandle handle) =>
        _colliders.TryGetValue(key: handle.Id, value: out var col) && col.IsTrigger;

    public bool IsOneWay(ColliderHandle handle) =>
        _colliders.TryGetValue(key: handle.Id, value: out var col) && col.OneWayUp;

    /// <summary>
    ///     Every query-relevant field in ONE lookup — the controller's slide/penetration loops were
    ///     paying up to five dictionary probes per contact per tick through the single-field getters.
    /// </summary>
    public bool TryGetInfo(ColliderHandle handle, out ColliderInfo info)
    {
        if (_colliders.TryGetValue(key: handle.Id, value: out var col))
        {
            info = new ColliderInfo(
                Shape: col.Shape,
                Center: col.Center,
                HalfExtents: col.HalfExtents,
                Radius: col.Radius,
                OneWayUp: col.OneWayUp
            );
            return true;
        }

        info = default;
        return false;
    }

    /// <summary>One collider's query-relevant fields, snapshotted by <see cref="TryGetInfo" />.</summary>
    public readonly record struct ColliderInfo(
        ColliderShape2D Shape,
        Vec2 Center,
        Vec2 HalfExtents,
        float Radius,
        bool OneWayUp);

    // ── Overlap queries ──────────────────────────────────────────────────────

    /// <summary>
    ///     Colliders overlapping (strictly — touching doesn't count) the query box, appended after
    ///     clearing
    ///     <paramref name="results" />.
    /// </summary>
    public int OverlapBox(Vec2 center, Vec2 halfExtents, uint mask, List<ColliderHandle> results,
        bool includeTriggers = true)
    {
        results.Clear();
        GatherCandidates(min: center - halfExtents, max: center + halfExtents);
        for (int i = 0; i < _candidates.Count; i++)
        {
            uint id = _candidates[i];
            var col = _colliders[id];
            if ((col.Layer & mask) == 0) continue;
            if (!includeTriggers && col.IsTrigger) continue;
            bool hit = col.Shape == ColliderShape2D.Box
                ? BoxOverlapsBox(
                    ca: center,
                    ha: halfExtents,
                    cb: col.Center,
                    hb: col.HalfExtents
                )
                : BoxOverlapsCircle(
                    boxCenter: center,
                    boxHalf: halfExtents,
                    circleCenter: col.Center,
                    radius: col.Radius
                );
            if (hit) results.Add(new ColliderHandle(id));
        }

        return results.Count;
    }

    public int OverlapCircle(Vec2 center, float radius, uint mask, List<ColliderHandle> results,
        bool includeTriggers = true)
    {
        results.Clear();
        var r = new Vec2(x: radius, y: radius);
        GatherCandidates(min: center - r, max: center + r);
        for (int i = 0; i < _candidates.Count; i++)
        {
            uint id = _candidates[i];
            var col = _colliders[id];
            if ((col.Layer & mask) == 0) continue;
            if (!includeTriggers && col.IsTrigger) continue;
            bool hit;
            if (col.Shape == ColliderShape2D.Box)
            {
                hit = BoxOverlapsCircle(
                    boxCenter: col.Center,
                    boxHalf: col.HalfExtents,
                    circleCenter: center,
                    radius: radius
                );
            }
            else
            {
                float sum = radius + col.Radius;
                hit = (col.Center - center).LengthSq() < sum * sum;
            }

            if (hit) results.Add(new ColliderHandle(id));
        }

        return results.Count;
    }

    // ── Raycast ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Closest hit along the (internally normalized) ray. A ray starting inside a collider does not
    ///     hit
    ///     it. One-way platforms are hit only from above going down: direction.Y &lt; 0, the origin
    ///     at-or-above
    ///     the platform top (− <see cref="OneWayEpsilon" />), and the entry face is the top.
    /// </summary>
    public bool Raycast(Vec2 origin, Vec2 direction, float maxDistance, uint mask, out RayHit2D hit,
        bool includeTriggers = false)
    {
        hit = default;
        float len = direction.Length();
        if (len < DirEpsilon || maxDistance <= 0f) return false;
        var dir = direction * (1f / len);

        var end = origin + (dir * maxDistance);
        GatherCandidates(
            min: new Vec2(x: MathF.Min(x: origin.X, y: end.X), y: MathF.Min(x: origin.Y, y: end.Y)),
            max: new Vec2(x: MathF.Max(x: origin.X, y: end.X), y: MathF.Max(x: origin.Y, y: end.Y))
        );

        float bestT = float.PositiveInfinity;
        for (int i = 0; i < _candidates.Count; i++)
        {
            uint id = _candidates[i];
            var col = _colliders[id];
            if ((col.Layer & mask) == 0) continue;
            if (!includeTriggers && col.IsTrigger) continue;

            float t;
            Vec2 normal;
            if (col.Shape == ColliderShape2D.Box)
            {
                if (col.OneWayUp &&
                    (dir.Y >= 0f || origin.Y < col.Center.Y + col.HalfExtents.Y - OneWayEpsilon))
                    continue;
                if (!RayVsAabb(
                        origin: origin,
                        dir: dir,
                        maxDist: maxDistance,
                        min: col.Center - col.HalfExtents,
                        max: col.Center + col.HalfExtents,
                        t: out t,
                        normal: out normal
                    )) continue;
                if (col.OneWayUp && normal.Y < 0.5f) continue;
            }
            else
            {
                if (!RayVsCircle(
                        origin: origin,
                        dir: dir,
                        maxDist: maxDistance,
                        center: col.Center,
                        radius: col.Radius,
                        t: out t,
                        normal: out normal
                    )) continue;
            }

            if (t >= bestT) continue;
            bestT = t;
            hit = new RayHit2D(
                collider: new ColliderHandle(id),
                point: origin + (dir * t),
                normal: normal,
                distance: t
            );
        }

        return bestT < float.PositiveInfinity;
    }

    // ── Swept AABB ───────────────────────────────────────────────────────────

    /// <summary>
    ///     First time-of-impact of a moving AABB. Boxes use the exact Minkowski slab test (continuous — no
    ///     tunnelling at any speed); circles use the exact Minkowski rounded-rect (expanded faces + corner
    ///     circles). A start-overlapping solid reports Time 0 with a minimal-push normal. One-way
    ///     platforms
    ///     block only when displacement.Y &lt; 0, the mover's bottom starts at-or-above the platform top
    ///     (− <see cref="OneWayEpsilon" />), and the contact normal is the top face — never sideways or
    ///     upward.
    ///     <paramref name="ignoreWhere" /> lets a caller skip an arbitrary set (e.g. dropped-through
    ///     platforms).
    /// </summary>
    public bool SweepBox(Vec2 center, Vec2 halfExtents, Vec2 displacement, uint mask,
        out SweepHit2D hit,
        ColliderHandle ignore = default, bool includeTriggers = false,
        Func<ColliderHandle, bool>? ignoreWhere = null)
    {
        hit = default;
        float dist = displacement.Length();
        if (dist < DirEpsilon) return false;
        var dir = displacement * (1f / dist);

        var end = center + displacement;
        GatherCandidates(
            min: new Vec2(
                x: MathF.Min(x: center.X, y: end.X),
                y: MathF.Min(x: center.Y, y: end.Y)
            ) - halfExtents,
            max: new Vec2(
                x: MathF.Max(x: center.X, y: end.X),
                y: MathF.Max(x: center.Y, y: end.Y)
            ) + halfExtents
        );

        float bestT = float.PositiveInfinity;
        float moverBottom = center.Y - halfExtents.Y;
        for (int i = 0; i < _candidates.Count; i++)
        {
            uint id = _candidates[i];
            if (id == ignore.Id) continue;
            var col = _colliders[id];
            if ((col.Layer & mask) == 0) continue;
            if (!includeTriggers && col.IsTrigger) continue;
            if (ignoreWhere != null && ignoreWhere(new ColliderHandle(id))) continue;

            float t;
            Vec2 normal, point;
            if (col.Shape == ColliderShape2D.Box)
            {
                if (col.OneWayUp &&
                    (displacement.Y >= 0f ||
                     moverBottom < col.Center.Y + col.HalfExtents.Y - OneWayEpsilon))
                    continue;
                var ext = col.HalfExtents + halfExtents;
                var min = col.Center - ext;
                var max = col.Center + ext;
                if (RayVsAabb(
                        origin: center,
                        dir: dir,
                        maxDist: dist,
                        min: min,
                        max: max,
                        t: out t,
                        normal: out normal
                    )) { }
                else if (PointInAabb(p: center, min: min, max: max))
                {
                    t = 0f;
                    normal = AabbPushNormal(p: center, min: min, max: max);
                }
                else
                    continue;

                if (col.OneWayUp && normal.Y < 0.5f) continue;
                point = ClampToAabb(
                    p: center + (dir * t),
                    min: col.Center - col.HalfExtents,
                    max: col.Center + col.HalfExtents
                );
            }
            else
            {
                if (!SweepVsRoundedRect(
                        origin: center,
                        dir: dir,
                        maxDist: dist,
                        center: col.Center,
                        core: halfExtents,
                        radius: col.Radius,
                        t: out t,
                        normal: out normal
                    ))
                    continue;
                point = col.Center + (normal * col.Radius);
            }

            if (t >= bestT) continue;
            bestT = t;
            hit = new SweepHit2D(
                collider: new ColliderHandle(id),
                time: t / dist,
                point: point,
                normal: normal
            );
        }

        return bestT < float.PositiveInfinity;
    }

    // ── Broadphase ───────────────────────────────────────────────────────────

    private ColliderHandle Insert(Collider col)
    {
        uint id = _nextId++;
        _colliders[id] = col;
        ComputeCellRange(
            col: col,
            minCx: out col.MinCx,
            minCy: out col.MinCy,
            maxCx: out col.MaxCx,
            maxCy: out col.MaxCy
        );
        AddToCells(id: id, col: col);
        return new ColliderHandle(id);
    }

    private void Reindex(uint id, Collider col)
    {
        ComputeCellRange(
            col: col,
            minCx: out int minCx,
            minCy: out int minCy,
            maxCx: out int maxCx,
            maxCy: out int maxCy
        );
        if (minCx == col.MinCx && minCy == col.MinCy && maxCx == col.MaxCx &&
            maxCy == col.MaxCy) return;
        RemoveFromCells(id: id, col: col);
        col.MinCx = minCx;
        col.MinCy = minCy;
        col.MaxCx = maxCx;
        col.MaxCy = maxCy;
        AddToCells(id: id, col: col);
    }

    private void ComputeCellRange(Collider col, out int minCx, out int minCy, out int maxCx,
        out int maxCy)
    {
        var min = col.Center - col.HalfExtents;
        var max = col.Center + col.HalfExtents;
        minCx = CellOf(min.X);
        minCy = CellOf(min.Y);
        maxCx = CellOf(max.X);
        maxCy = CellOf(max.Y);
    }

    private void AddToCells(uint id, Collider col)
    {
        for (int cx = col.MinCx; cx <= col.MaxCx; cx++)
        for (int cy = col.MinCy; cy <= col.MaxCy; cy++)
        {
            long key = KeyOf(x: cx, y: cy);
            if (!_cells.TryGetValue(key: key, value: out var list)) _cells[key] = list = [];
            list.Add(id);
        }
    }

    private void RemoveFromCells(uint id, Collider col)
    {
        for (int cx = col.MinCx; cx <= col.MaxCx; cx++)
        for (int cy = col.MinCy; cy <= col.MaxCy; cy++)
        {
            if (_cells.TryGetValue(key: KeyOf(x: cx, y: cy), value: out var list))
                list.Remove(id);
        }
    }

    /// <summary>
    ///     Fills <see cref="_candidates" /> with the distinct collider ids whose grid cells intersect the
    ///     query AABB. Falls back to iterating every collider when the cell range is larger than the world
    ///     (long rays / huge sweeps), which is both correct and cheaper.
    /// </summary>
    private void GatherCandidates(Vec2 min, Vec2 max)
    {
        _candidates.Clear();
        int minCx = CellOf(min.X), maxCx = CellOf(max.X);
        int minCy = CellOf(min.Y), maxCy = CellOf(max.Y);
        long cellCount = ((long)(maxCx - minCx) + 1) * ((long)(maxCy - minCy) + 1);
        if (cellCount > _colliders.Count)
        {
            foreach (uint id in _colliders.Keys) _candidates.Add(id);
            return;
        }

        _stamp++;
        for (int cx = minCx; cx <= maxCx; cx++)
        for (int cy = minCy; cy <= maxCy; cy++)
        {
            if (!_cells.TryGetValue(key: KeyOf(x: cx, y: cy), value: out var list)) continue;
            for (int i = 0; i < list.Count; i++)
            {
                uint id = list[i];
                var col = _colliders[id];
                if (col.Stamp == _stamp) continue;
                col.Stamp = _stamp;
                _candidates.Add(id);
            }
        }
    }

    private int CellOf(float v) => (int)MathF.Floor(v / _cellSize);

    private static long KeyOf(int x, int y) => ((long)x << 32) | (uint)y;

    // ── Shape math ───────────────────────────────────────────────────────────

    private static bool BoxOverlapsBox(Vec2 ca, Vec2 ha, Vec2 cb, Vec2 hb) =>
        MathF.Abs(ca.X - cb.X) < ha.X + hb.X && MathF.Abs(ca.Y - cb.Y) < ha.Y + hb.Y;

    private static bool BoxOverlapsCircle(Vec2 boxCenter, Vec2 boxHalf, Vec2 circleCenter,
        float radius)
    {
        float qx = MathF.Max(
            x: boxCenter.X - boxHalf.X,
            y: MathF.Min(x: circleCenter.X, y: boxCenter.X + boxHalf.X)
        );
        float qy = MathF.Max(
            x: boxCenter.Y - boxHalf.Y,
            y: MathF.Min(x: circleCenter.Y, y: boxCenter.Y + boxHalf.Y)
        );
        float dx = circleCenter.X - qx;
        float dy = circleCenter.Y - qy;
        return (dx * dx) + (dy * dy) < radius * radius;
    }

    private static bool PointInAabb(Vec2 p, Vec2 min, Vec2 max) =>
        p.X > min.X && p.X < max.X && p.Y > min.Y && p.Y < max.Y;

    private static Vec2 ClampToAabb(Vec2 p, Vec2 min, Vec2 max)
    {
        return new Vec2(
            x: MathF.Max(x: min.X, y: MathF.Min(x: p.X, y: max.X)),
            y: MathF.Max(x: min.Y, y: MathF.Min(x: p.Y, y: max.Y))
        );
    }

    /// <summary>Minimal-translation face normal for a point inside an AABB (start-overlap sweeps).</summary>
    private static Vec2 AabbPushNormal(Vec2 p, Vec2 min, Vec2 max)
    {
        float left = p.X - min.X;
        float right = max.X - p.X;
        float down = p.Y - min.Y;
        float up = max.Y - p.Y;
        float best = MathF.Min(x: MathF.Min(x: left, y: right), y: MathF.Min(x: down, y: up));
        if (best == up) return Vec2.Up;
        if (best == down) return new Vec2(x: 0f, y: -1f);
        return best == right ? Vec2.Right : new Vec2(x: -1f, y: 0f);
    }

    /// <summary>
    ///     Slab test with a unit direction. Misses when the origin starts inside (callers that want
    ///     start-overlap semantics check that separately). Normal is the entry face's outward axis.
    /// </summary>
    private static bool RayVsAabb(Vec2 origin, Vec2 dir, float maxDist, Vec2 min, Vec2 max,
        out float t, out Vec2 normal)
    {
        t = 0f;
        normal = Vec2.Zero;
        float tEnter = float.NegativeInfinity;
        float tExit = float.PositiveInfinity;
        int axis = -1;

        if (MathF.Abs(dir.X) < DirEpsilon)
        {
            if (origin.X <= min.X || origin.X >= max.X) return false;
        }
        else
        {
            float inv = 1f / dir.X;
            float t1 = (min.X - origin.X) * inv;
            float t2 = (max.X - origin.X) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tEnter)
            {
                tEnter = t1;
                axis = 0;
            }

            tExit = MathF.Min(x: tExit, y: t2);
        }

        if (MathF.Abs(dir.Y) < DirEpsilon)
        {
            if (origin.Y <= min.Y || origin.Y >= max.Y) return false;
        }
        else
        {
            float inv = 1f / dir.Y;
            float t1 = (min.Y - origin.Y) * inv;
            float t2 = (max.Y - origin.Y) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tEnter)
            {
                tEnter = t1;
                axis = 1;
            }

            tExit = MathF.Min(x: tExit, y: t2);
        }

        if (axis < 0 || tExit < tEnter || tExit < 0f) return false;
        if (tEnter < 0f || tEnter > maxDist) return false;

        t = tEnter;
        normal = axis == 0
            ? new Vec2(x: dir.X > 0f ? -1f : 1f, y: 0f)
            : new Vec2(x: 0f, y: dir.Y > 0f ? -1f : 1f);
        return true;
    }

    private static bool RayVsCircle(Vec2 origin, Vec2 dir, float maxDist, Vec2 center, float radius,
        out float t, out Vec2 normal)
    {
        t = 0f;
        normal = Vec2.Zero;
        var m = origin - center;
        float b = m.Dot(dir);
        float c = m.LengthSq() - (radius * radius);
        if (c < 0f) return false; // starts inside
        if (b > 0f) return false; // pointing away
        float disc = (b * b) - c;
        if (disc < 0f) return false;
        float tHit = -b - MathF.Sqrt(disc);
        if (tHit < 0f || tHit > maxDist) return false;
        t = tHit;
        normal = (origin + (dir * tHit) - center) * (1f / radius);
        return true;
    }

    /// <summary>
    ///     Exact swept-AABB-vs-circle: the Minkowski sum (circle ⊕ box) is a rounded rectangle around the
    ///     circle's center — two axis-expanded face slabs (valid only within the core span) plus four
    ///     corner circles of the circle's radius at the core corners.
    /// </summary>
    private static bool SweepVsRoundedRect(Vec2 origin, Vec2 dir, float maxDist, Vec2 center,
        Vec2 core,
        float radius, out float t, out Vec2 normal)
    {
        t = float.PositiveInfinity;
        normal = Vec2.Zero;

        // Start-overlap: closest point on the core AABB to the origin within radius.
        var q = ClampToAabb(p: origin, min: center - core, max: center + core);
        var d0 = origin - q;
        float d0Sq = d0.LengthSq();
        if (d0Sq < radius * radius)
        {
            t = 0f;
            normal = d0Sq > DirEpsilon
                ? d0 * (1f / MathF.Sqrt(d0Sq))
                : AabbPushNormal(
                    p: origin,
                    min: center - core - new Vec2(x: radius, y: radius),
                    max: center + core + new Vec2(x: radius, y: radius)
                );
            return true;
        }

        // Vertical faces (±X), valid while the crossing point is within the core's Y span.
        if (RayVsAabb(
                origin: origin,
                dir: dir,
                maxDist: maxDist,
                min: center - new Vec2(x: core.X + radius, y: core.Y),
                max: center + new Vec2(x: core.X + radius, y: core.Y),
                t: out float tf,
                normal: out var nf
            ) && nf.Y == 0f)
        {
            t = tf;
            normal = nf;
        }

        // Horizontal faces (±Y), valid within the core's X span.
        if (RayVsAabb(
                origin: origin,
                dir: dir,
                maxDist: maxDist,
                min: center - new Vec2(x: core.X, y: core.Y + radius),
                max: center + new Vec2(x: core.X, y: core.Y + radius),
                t: out tf,
                normal: out nf
            ) && nf.X == 0f && tf < t)
        {
            t = tf;
            normal = nf;
        }

        // Corner circles.
        for (int sx = -1; sx <= 1; sx += 2)
        for (int sy = -1; sy <= 1; sy += 2)
        {
            var corner = center + new Vec2(x: sx * core.X, y: sy * core.Y);
            if (RayVsCircle(
                    origin: origin,
                    dir: dir,
                    maxDist: maxDist,
                    center: corner,
                    radius: radius,
                    t: out tf,
                    normal: out nf
                ) && tf < t)
            {
                t = tf;
                normal = nf;
            }
        }

        return t < float.PositiveInfinity;
    }

    private sealed class Collider
    {
        public Vec2 Center;
        public Vec2 HalfExtents;
        public bool IsTrigger;
        public uint Layer;
        public int MaxCx, MaxCy;
        public int MinCx, MinCy;
        public Vec2 MoveDelta;
        public bool OneWayUp;
        public float Radius;
        public ColliderShape2D Shape;
        public int Stamp;
        public object? UserData;
    }
}
