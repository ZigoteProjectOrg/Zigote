using Zigote.Core.Math3D;

namespace Zigote.Network;

/// <summary>
///     A ready-made replicated transform: position, rotation and scale as <see cref="NetVar{T}" />s,
///     with
///     built-in client-side interpolation. The server writes <see cref="Transform" /> (or the
///     individual
///     vars); remote clients read <see cref="InterpolatedPosition" /> /
///     <see cref="InterpolatedRotation" />,
///     which trail server state by the interpolation delay for smooth motion. Subclass it to add more
///     replicated fields, or use it directly. The owning client typically ignores interpolation and
///     predicts
///     instead (see the prediction helpers).
/// </summary>
public class NetworkTransform : NetObject
{
    private readonly SnapshotInterpolator<Vec3> _posInterp = new(static (a, b, t) => a.Lerp(b, t));

    private readonly SnapshotInterpolator<Quat> _rotInterp =
        new(static (a, b, t) => Quat.Slerp(a, b, t));

    public readonly NetVar<Vec3> NetPosition = NetVars.Vec3();
    public readonly NetVar<Quat> NetRotation = NetVars.Quat(Quat.Identity);
    public readonly NetVar<Vec3> NetScale = NetVars.Vec3(Vec3.One);

    public NetworkTransform()
    {
        Register(NetPosition);
        Register(NetRotation);
        Register(NetScale);
    }

    /// <summary>
    ///     When true (default), remote reads use the jitter-hiding interpolation buffer; when false they
    ///     snap to the
    ///     latest value.
    /// </summary>
    public bool Interpolated { get; set; } = true;

    public Vec3 InterpolatedPosition { get; private set; }
    public Quat InterpolatedRotation { get; private set; } = Quat.Identity;
    public Vec3 InterpolatedScale { get; private set; } = Vec3.One;

    public override Vec3 RelevancePosition => NetPosition.Value;

    /// <summary>
    ///     Authoring shortcut — the server sets this; reading returns the raw (non-interpolated)
    ///     values.
    /// </summary>
    public Transform3D Transform
    {
        get => new(NetPosition.Value, NetRotation.Value, NetScale.Value);
        set
        {
            NetPosition.Value = value.Position;
            NetRotation.Value = value.Rotation;
            NetScale.Value = value.Scale;
        }
    }

    public override void OnNetworkSpawn()
    {
        InterpolatedPosition = NetPosition.Value;
        InterpolatedRotation = NetRotation.Value;
        InterpolatedScale = NetScale.Value;
    }

    protected internal override void OnStateApplied(double serverTime)
    {
        _posInterp.Push(serverTime, NetPosition.Value);
        _rotInterp.Push(serverTime, NetRotation.Value);
        InterpolatedScale = NetScale.Value;
    }

    public override void Interpolate(double renderTime)
    {
        if (!Interpolated || IsServer)
        {
            InterpolatedPosition = NetPosition.Value;
            InterpolatedRotation = NetRotation.Value;
            InterpolatedScale = NetScale.Value;
            return;
        }

        if (_posInterp.TrySample(renderTime, out var p)) InterpolatedPosition = p;
        if (_rotInterp.TrySample(renderTime, out var r)) InterpolatedRotation = r;
        _posInterp.Prune(renderTime);
        _rotInterp.Prune(renderTime);
    }
}