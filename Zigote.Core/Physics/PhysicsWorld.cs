using Zigote.Core.Math3D;
using Zigote.Core.Native;

namespace Zigote.Core.Physics;

/// <summary>
///     High-level wrapper around the native JoltPhysics world exposed via the Zigote FFI.
///     Typical usage per frame:
///     <code>
///     world.Step(deltaTime);
///     // read transforms and sync scene nodes
///     var pos = world.GetBodyPosition(bodyId);
///     var rot = world.GetBodyRotationQuat(bodyId);
///     // or, for many bodies, one batched call: world.GetBodyTransforms(ids, xforms)
///     </code>
/// </summary>
public sealed class PhysicsWorld : IDisposable
{
    public const uint InvalidBodyId = 0xFFFF_FFFF;
    private bool _disposed;
    private ulong _engineHandle;

    private bool _initialized;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_initialized)
        {
            NativeEngine.PhysicsShutdown(_engineHandle);
            _initialized = false;
        }
    }

    /// <summary>
    ///     Initialize the physics world.
    /// </summary>
    /// <param name="engineHandle">Opaque engine handle from <see cref="Engine.ZigoteEngine.Handle" />.</param>
    /// <param name="maxBodies">Maximum simultaneous rigid bodies (default 1024).</param>
    /// <param name="numThreads">Job-system worker threads (-1 = auto-detect).</param>
    public void Initialize(ulong engineHandle, uint maxBodies = 1024, int numThreads = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;

        _engineHandle = engineHandle;
        var result = NativeEngine.PhysicsInit(_engineHandle, maxBodies, numThreads);
        if (result != ZgResult.Ok)
            throw new InvalidOperationException("zigote_physics_init failed.");

        _initialized = true;
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    /// <summary>Advance the physics simulation by <paramref name="deltaTime" /> seconds.</summary>
    /// <param name="collisionSteps">Sub-steps for collision; 1 is sufficient at 60 Hz.</param>
    public void Step(float deltaTime, int collisionSteps = 1)
    {
        EnsureReady();
        NativeEngine.PhysicsStep(_engineHandle, deltaTime, collisionSteps);
    }

    /// <summary>Set the gravity vector. Default is (0, -9.81, 0).</summary>
    public void SetGravity(Vec3 gravity)
    {
        EnsureReady();
        NativeEngine.PhysicsSetGravity(
            _engineHandle,
            gravity.X,
            gravity.Y,
            gravity.Z
        );
    }

    /// <summary>
    ///     Rebuild the broad-phase acceleration structure.
    ///     Call once after adding all static bodies, before the first <see cref="Step" />.
    /// </summary>
    public void OptimizeBroadPhase()
    {
        EnsureReady();
        NativeEngine.PhysicsOptimizeBroadphase(_engineHandle);
    }

    // ── Body lifecycle ────────────────────────────────────────────────────────

    /// <summary>
    ///     Create a rigid body from the given settings.
    ///     The body is created but NOT yet added to the simulation — call <see cref="AddBody" />.
    /// </summary>
    /// <returns>Body ID, or <see cref="InvalidBodyId" /> on failure.</returns>
    public uint CreateBody(PhysicsBodySettings settings)
    {
        EnsureReady();
        return NativeEngine.PhysicsCreateBody(
            _engineHandle,
            (byte)settings.ShapeType,
            settings.HalfExtents.X,
            settings.HalfExtents.Y,
            settings.HalfExtents.Z,
            settings.Position.X,
            settings.Position.Y,
            settings.Position.Z,
            settings.Rotation.X,
            settings.Rotation.Y,
            settings.Rotation.Z,
            (byte)settings.MotionType,
            settings.Friction,
            settings.Restitution,
            settings.GravityFactor,
            settings.Mass
        );
    }

    /// <summary>
    ///     Convenience method that creates a body AND immediately adds it to the simulation.
    /// </summary>
    public uint CreateAndAddBody(PhysicsBodySettings settings)
    {
        var id = CreateBody(settings);
        if (id != InvalidBodyId) AddBody(id);
        return id;
    }

    /// <summary>Destroy a body (removes from simulation and frees all resources).</summary>
    public void DestroyBody(uint bodyId)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsDestroyBody(_engineHandle, bodyId);
    }

    /// <summary>Add a body to the active simulation.</summary>
    public void AddBody(uint bodyId)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsAddBody(_engineHandle, bodyId);
    }

    /// <summary>Remove a body from the simulation without destroying it.</summary>
    public void RemoveBody(uint bodyId)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsRemoveBody(_engineHandle, bodyId);
    }

    // ── Transform ─────────────────────────────────────────────────────────────

    /// <summary>Read the current world-space position of a body.</summary>
    public Vec3 GetBodyPosition(uint bodyId)
    {
        EnsureReady();
        NativeEngine.PhysicsGetBodyPosition(
            _engineHandle,
            bodyId,
            out var x,
            out var y,
            out var z
        );
        return new Vec3(x, y, z);
    }

    /// <summary>Read the current world-space rotation as Euler angles (radians).</summary>
    public Vec3 GetBodyRotation(uint bodyId)
    {
        EnsureReady();
        NativeEngine.PhysicsGetBodyRotation(
            _engineHandle,
            bodyId,
            out var rx,
            out var ry,
            out var rz
        );
        return new Vec3(rx, ry, rz);
    }

    /// <summary>
    ///     Batched transform read: for each id in <paramref name="ids" /> writes 7 floats
    ///     (pos.xyz + quat.xyzw) into <paramref name="outXforms" />, which must hold
    ///     <c>ids.Length * 7</c> floats. One native call for the whole set — use this on the
    ///     per-tick sync path instead of a position + rotation call pair per body.
    /// </summary>
    public void GetBodyTransforms(ReadOnlySpan<uint> ids, Span<float> outXforms)
    {
        EnsureReady();
        if (ids.Length == 0) return;
        if (outXforms.Length < ids.Length * 7)
            throw new ArgumentException(
                "outXforms must hold 7 floats per body id.",
                nameof(outXforms)
            );
        unsafe
        {
            fixed (uint* idsPtr = ids)
            fixed (float* xformsPtr = outXforms)
            {
                NativeEngine.PhysicsGetBodyTransforms(
                    _engineHandle,
                    idsPtr,
                    (uint)ids.Length,
                    xformsPtr
                );
            }
        }
    }

    /// <summary>Teleport a body to the given position (activates it).</summary>
    public void SetBodyPosition(uint bodyId, Vec3 position)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsSetBodyPosition(
            _engineHandle,
            bodyId,
            position.X,
            position.Y,
            position.Z
        );
    }

    // ── Velocity / Forces ─────────────────────────────────────────────────────

    /// <summary>Directly set the linear velocity of a body (m/s).</summary>
    public void SetLinearVelocity(uint bodyId, Vec3 velocity)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsSetLinearVelocity(
            _engineHandle,
            bodyId,
            velocity.X,
            velocity.Y,
            velocity.Z
        );
    }

    /// <summary>Directly set the angular velocity of a body (rad/s).</summary>
    public void SetAngularVelocity(uint bodyId, Vec3 velocity)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsSetAngularVelocity(
            _engineHandle,
            bodyId,
            velocity.X,
            velocity.Y,
            velocity.Z
        );
    }

    /// <summary>Apply a continuous force to a body (N). Accumulates until the next step.</summary>
    public void AddForce(uint bodyId, Vec3 force)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsAddForce(
            _engineHandle,
            bodyId,
            force.X,
            force.Y,
            force.Z
        );
    }

    /// <summary>Apply an instantaneous impulse to a body (kg·m/s).</summary>
    public void AddImpulse(uint bodyId, Vec3 impulse)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsAddImpulse(
            _engineHandle,
            bodyId,
            impulse.X,
            impulse.Y,
            impulse.Z
        );
    }

    /// <summary>Apply a continuous torque to a body (N·m). Accumulates until the next step.</summary>
    public void AddTorque(uint bodyId, Vec3 torque)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsAddTorque(
            _engineHandle,
            bodyId,
            torque.X,
            torque.Y,
            torque.Z
        );
    }

    /// <summary>Apply a continuous force (N) at a world-space point — yields both force and torque.</summary>
    public void AddForceAtPoint(uint bodyId, Vec3 force, Vec3 worldPoint)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsAddForceAtPoint(
            _engineHandle,
            bodyId,
            force.X,
            force.Y,
            force.Z,
            worldPoint.X,
            worldPoint.Y,
            worldPoint.Z
        );
    }

    /// <summary>Read the linear velocity of a body (m/s).</summary>
    public Vec3 GetLinearVelocity(uint bodyId)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return Vec3.Zero;
        NativeEngine.PhysicsGetLinearVelocity(
            _engineHandle,
            bodyId,
            out var x,
            out var y,
            out var z
        );
        return new Vec3(x, y, z);
    }

    /// <summary>Read the angular velocity of a body (rad/s).</summary>
    public Vec3 GetAngularVelocity(uint bodyId)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return Vec3.Zero;
        NativeEngine.PhysicsGetAngularVelocity(
            _engineHandle,
            bodyId,
            out var x,
            out var y,
            out var z
        );
        return new Vec3(x, y, z);
    }

    /// <summary>Read the rotation of a body as a quaternion (lossless, unlike the Euler getter).</summary>
    public Quat GetBodyRotationQuat(uint bodyId)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return Quat.Identity;
        NativeEngine.PhysicsGetBodyRotationQuat(
            _engineHandle,
            bodyId,
            out var x,
            out var y,
            out var z,
            out var w
        );
        return new Quat(
            x,
            y,
            z,
            w
        );
    }

    /// <summary>Set the rotation of a body from a quaternion (activates it).</summary>
    public void SetBodyRotationQuat(uint bodyId, Quat rotation)
    {
        EnsureReady();
        if (bodyId == InvalidBodyId) return;
        NativeEngine.PhysicsSetBodyRotationQuat(
            _engineHandle,
            bodyId,
            rotation.X,
            rotation.Y,
            rotation.Z,
            rotation.W
        );
    }

    /// <summary>
    ///     Closest-hit ray cast against the world. Returns true on hit, filling
    ///     <paramref name="hitBody" />,
    ///     <paramref name="point" />, <paramref name="normal" /> and <paramref name="distance" />.
    /// </summary>
    public bool Raycast(Vec3 origin, Vec3 direction, float maxDistance,
        out uint hitBody, out Vec3 point, out Vec3 normal, out float distance,
        uint ignoreBody = InvalidBodyId)
    {
        EnsureReady();
        hitBody = InvalidBodyId;
        point = origin;
        normal = Vec3.Up;
        distance = maxDistance;

        var result = NativeEngine.PhysicsRaycastClosest(
            _engineHandle,
            origin.X,
            origin.Y,
            origin.Z,
            direction.X,
            direction.Y,
            direction.Z,
            maxDistance,
            ignoreBody,
            out var body,
            out var fraction,
            out var px,
            out var py,
            out var pz,
            out var nx,
            out var ny,
            out var nz
        );
        if (result == 0) return false;

        hitBody = body;
        point = new Vec3(px, py, pz);
        normal = new Vec3(nx, ny, nz);
        distance = fraction * maxDistance;
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("Call Initialize() before using PhysicsWorld.");
    }
}
