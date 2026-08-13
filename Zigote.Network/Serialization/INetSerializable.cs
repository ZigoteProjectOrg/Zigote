namespace Zigote.Network;

/// <summary>
///     A type that knows how to write and read itself with the bit serializer. Implement on game messages,
///     replicated component state, and input commands. <see cref="Deserialize" /> must read fields in the
///     exact order <see cref="Serialize" /> wrote them.
/// </summary>
public interface INetSerializable
{
    void Serialize(NetWriter writer);
    void Deserialize(NetReader reader);
}
