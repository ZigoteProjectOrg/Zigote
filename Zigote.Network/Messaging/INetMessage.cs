namespace Zigote.Network;

/// <summary>
///     A typed, routable game message. Implement <see cref="INetSerializable.Serialize" /> /
///     <see cref="INetSerializable.Deserialize" /> and give the type a parameterless constructor so the
///     receiver can instantiate it. Register handlers with <see cref="NetworkManager.OnMessage{T}" /> and
///     send with <see cref="NetworkManager.Send{T}" /> / <c>SendToAll</c> / <c>SendToServer</c>.
/// </summary>
public interface INetMessage : INetSerializable
{
}
