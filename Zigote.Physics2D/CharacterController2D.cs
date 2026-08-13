using Zigote.Core.Math3D;

namespace Zigote.Physics2D;

/// <summary>
///     Kinematic AABB character for platformers/top-down games: sweep-and-slide over a
///     <see cref="CollisionWorld2D" /> with walkable-slope grounding, ground snapping, one-way
///     platform
///     drop-through, moving-platform carry, and trigger enter/exit reporting. The game owns
///     integration —
///     add gravity/jumps to <see cref="Velocity" /> each tick, then call <see cref="Move" /> once per
///     fixed
///     tick. Deterministic and allocation-free in steady state.
/// </summary>
public sealed class CharacterController2D(CollisionWorld2D world, Vec2 halfExtents)
{
    private const float MinMoveSq = 1e-12f;
    private readonly List<ColliderHandle> _dropScratch = [];

    private readonly HashSet<ColliderHandle> _droppedOneWay = [];
    private readonly List<ColliderHandle> _entered = [];
    private readonly List<ColliderHandle> _exited = [];
    private readonly List<ColliderHandle> _scratch = [];

    public uint CollisionMask = 0xFFFFFFFF;
    public float GroundSnapDistance = 0.2f;
    public int MaxSlideIterations = 4;
    public float MaxSlopeDegrees = 50f;

    public Vec2 Position;
    public float SkinWidth = 0.01f;
    public Vec2 Velocity;
    private Func<ColliderHandle, bool>? _sweepIgnore;
    private HashSet<ColliderHandle> _triggersNow = [];
    private HashSet<ColliderHandle> _triggersPrev = [];

    public Vec2 HalfExtents => halfExtents;
    public bool IsGrounded { get; private set; }
    public Vec2 GroundNormal { get; private set; } = Vec2.Up;
    public ColliderHandle GroundCollider { get; private set; }
    public bool IsOnWall { get; private set; }
    public bool IsOnCeiling { get; private set; }
    public float TimeSinceGrounded { get; private set; }
    public IReadOnlyList<ColliderHandle> TriggersEntered => _entered;
    public IReadOnlyList<ColliderHandle> TriggersExited => _exited;

    /// <summary>
    ///     Ignore the one-way platforms currently under/overlapping the character so the next Moves fall
    ///     through them. The ignore set auto-clears per platform once the character is fully clear of it.
    /// </summary>
    public void DropThrough()
    {
        if (IsGrounded && GroundCollider.IsValid && world.IsOneWay(GroundCollider))
            _droppedOneWay.Add(GroundCollider);

        // Also catch platforms the box merely rests near (skin gap) — probe a slightly grown box.
        var margin = Vec2.Splat(SkinWidth * 2f);
        int count = world.OverlapBox(
            center: Position,
            halfExtents: halfExtents + margin,
            mask: CollisionMask,
            results: _scratch,
            includeTriggers: false
        );
        for (int i = 0; i < count; i++)
        {
            if (world.IsOneWay(_scratch[i]))
                _droppedOneWay.Add(_scratch[i]);
        }
    }

    public void Move(float dt)
    {
        _sweepIgnore ??= IsDropIgnored;
        bool startedGrounded = IsGrounded;
        // A jump must not be snapped back, but every WALK case must snap: ascending a slope carries +Y
        // velocity (slide projection) with ~zero along-normal; descending carries positive along-normal
        // (horizontal input on a tilted normal) with -Y. Only a jump has BOTH: velocity along the old
        // ground normal AND upward. Gate the snap on that conjunction of the pre-move velocity.
        bool jumpedOffGround = Velocity.Dot(GroundNormal) > 0.01f && Velocity.Y > 0.01f;
        float cosMax = MathF.Cos(MaxSlopeDegrees * (MathF.PI / 180f));

        // (a) Platform carry: ride whatever we were grounded on, both axes, before our own motion.
        if (startedGrounded && GroundCollider.IsValid && world.IsAlive(GroundCollider))
        {
            var delta = world.GetMoveDelta(GroundCollider);
            if (delta.X != 0f || delta.Y != 0f)
            {
                Position += delta;
                ResolvePenetration();
            }
        }

        IsGrounded = false;
        IsOnWall = false;
        IsOnCeiling = false;
        GroundCollider = ColliderHandle.None;

        // (b) Sweep-and-slide.
        var remaining = Velocity * dt;
        for (int iter = 0; iter < MaxSlideIterations && remaining.LengthSq() > MinMoveSq; iter++)
        {
            if (!world.SweepBox(
                    center: Position,
                    halfExtents: halfExtents,
                    displacement: remaining,
                    mask: CollisionMask,
                    hit: out var hit,
                    ignore: default,
                    includeTriggers: false,
                    ignoreWhere: _sweepIgnore
                ))
            {
                Position += remaining;
                break;
            }

            float dist = remaining.Length();
            var dir = remaining * (1f / dist);
            float travel = hit.Time * dist;
            var n = hit.Normal;

            // (e) Internal-edge (tile seam) fix: a wall-like hit whose contact sits within a couple of
            // skin widths of a box collider's top corner, while we were grounded and moving horizontally,
            // is really a corner contact — the up normal is equally valid there, so prefer it and
            // micro-lift back to a clean skin gap instead of killing horizontal motion on a ghost wall.
            float seamLift = float.NegativeInfinity;
            if (startedGrounded && MathF.Abs(n.Y) < cosMax && MathF.Abs(remaining.X) > 1e-6f
                && world.GetShape(hit.Collider) == ColliderShape2D.Box)
            {
                float top = world.GetPosition(hit.Collider).Y +
                            world.GetHalfExtents(hit.Collider).Y;
                float bottomAtImpact = Position.Y + (dir.Y * travel) - halfExtents.Y;
                if (bottomAtImpact >= top - (SkinWidth * 2f))
                {
                    n = Vec2.Up;
                    seamLift = top + halfExtents.Y + SkinWidth;
                }
            }

            // Move to the TOI, then restore the skin gap along the contact normal (not along the sweep
            // direction — a shallow grazing hit would otherwise leave no gap and re-hit at t=0 forever).
            Position += dir * travel;
            if (seamLift > float.NegativeInfinity)
                Position = new Vec2(x: Position.X, y: MathF.Max(x: Position.Y, y: seamLift));
            else
                Position += n * SkinWidth;

            ApplyContact(normal: n, collider: hit.Collider, cosMax: cosMax);

            remaining *= 1f - hit.Time;
            float into = remaining.Dot(n);
            if (into < 0f) remaining -= n * into;
            float vin = Velocity.Dot(n);
            if (vin < 0f) Velocity -= n * vin;
        }

        // (c) Ground snap: keep ascending/descending characters glued (slopes, convex curves, small
        // drops) — but never yank a jump back down (see jumpedOffGround above).
        if (startedGrounded && !IsGrounded && !jumpedOffGround && GroundSnapDistance > 0f)
        {
            if (world.SweepBox(
                    center: Position,
                    halfExtents: halfExtents,
                    displacement: new Vec2(x: 0f, y: -GroundSnapDistance),
                    mask: CollisionMask,
                    hit: out var snap,
                    ignore: default,
                    includeTriggers: false,
                    ignoreWhere: _sweepIgnore
                ) && snap.Normal.Y >= cosMax)
            {
                Position += new Vec2(x: 0f, y: -GroundSnapDistance * snap.Time);
                Position += snap.Normal * SkinWidth;
                ApplyContact(normal: snap.Normal, collider: snap.Collider, cosMax: cosMax);
                float vin = Velocity.Dot(snap.Normal);
                if (vin < 0f) Velocity -= snap.Normal * vin;
            }
        }

        UpdateDropIgnores();
        UpdateTriggers();

        if (IsGrounded) TimeSinceGrounded = 0f;
        else TimeSinceGrounded += dt;
    }

    private void ApplyContact(Vec2 normal, ColliderHandle collider, float cosMax)
    {
        if (normal.Y >= cosMax)
        {
            IsGrounded = true;
            GroundNormal = normal;
            GroundCollider = collider;
        }
        else if (normal.Y <= -cosMax)
            IsOnCeiling = true;
        else
            IsOnWall = true;
    }

    /// <summary>
    ///     Push out of solids (never triggers or one-ways) along the minimal axis — used after
    ///     platform carry.
    /// </summary>
    private void ResolvePenetration()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            int count = world.OverlapBox(
                center: Position,
                halfExtents: halfExtents,
                mask: CollisionMask,
                results: _scratch,
                includeTriggers: false
            );
            bool pushed = false;
            for (int i = 0; i < count; i++)
            {
                var h = _scratch[i];
                if (world.IsOneWay(h)) continue;
                var c = world.GetPosition(h);
                if (world.GetShape(h) == ColliderShape2D.Box)
                {
                    var ch = world.GetHalfExtents(h);
                    float dx = Position.X - c.X;
                    float dy = Position.Y - c.Y;
                    float px = halfExtents.X + ch.X - MathF.Abs(dx);
                    float py = halfExtents.Y + ch.Y - MathF.Abs(dy);
                    if (px <= 0f || py <= 0f) continue;
                    if (px < py)
                        Position += new Vec2(x: (dx >= 0f ? 1f : -1f) * (px + SkinWidth), y: 0f);
                    else
                        Position += new Vec2(x: 0f, y: (dy >= 0f ? 1f : -1f) * (py + SkinWidth));
                }
                else
                {
                    float r = world.GetRadius(h);
                    var q = new Vec2(
                        x: MathF.Max(
                            x: Position.X - halfExtents.X,
                            y: MathF.Min(x: c.X, y: Position.X + halfExtents.X)
                        ),
                        y: MathF.Max(
                            x: Position.Y - halfExtents.Y,
                            y: MathF.Min(x: c.Y, y: Position.Y + halfExtents.Y)
                        )
                    );
                    var d = q - c;
                    float len = d.Length();
                    if (len >= r || len < 1e-6f) continue;
                    Position += d * (1f / len) * (r - len + SkinWidth);
                }

                pushed = true;
            }

            if (!pushed) break;
        }
    }

    private bool IsDropIgnored(ColliderHandle h) =>
        _droppedOneWay.Count > 0 && _droppedOneWay.Contains(h);

    private void UpdateDropIgnores()
    {
        if (_droppedOneWay.Count == 0) return;
        var margin = Vec2.Splat(SkinWidth * 2f);
        int count = world.OverlapBox(
            center: Position,
            halfExtents: halfExtents + margin,
            mask: CollisionMask,
            results: _scratch,
            includeTriggers: false
        );
        _dropScratch.Clear();
        foreach (var h in _droppedOneWay)
        {
            bool still = false;
            for (int i = 0; i < count; i++)
            {
                if (_scratch[i] == h)
                {
                    still = true;
                    break;
                }
            }

            if (!still) _dropScratch.Add(h);
        }

        for (int i = 0; i < _dropScratch.Count; i++) _droppedOneWay.Remove(_dropScratch[i]);
    }

    private void UpdateTriggers()
    {
        _triggersNow.Clear();
        int count = world.OverlapBox(
            center: Position,
            halfExtents: halfExtents,
            mask: CollisionMask,
            results: _scratch
        );
        for (int i = 0; i < count; i++)
        {
            if (world.IsTrigger(_scratch[i]))
                _triggersNow.Add(_scratch[i]);
        }

        _entered.Clear();
        _exited.Clear();
        foreach (var h in _triggersNow)
        {
            if (!_triggersPrev.Contains(h))
                _entered.Add(h);
        }

        foreach (var h in _triggersPrev)
        {
            if (!_triggersNow.Contains(h))
                _exited.Add(h);
        }

        (_triggersPrev, _triggersNow) = (_triggersNow, _triggersPrev);
    }
}
