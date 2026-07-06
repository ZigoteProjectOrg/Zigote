namespace Zigote.Network;

/// <summary>
///     Serializes typed <see cref="INetMessage" />s onto a connection and dispatches received ones to
///     the
///     registered handler for their type. Sits on <see cref="NetChannel.Message" />. Registering a
///     handler
///     also registers the type, so a handler is enough to both send and receive a message.
/// </summary>
public sealed class MessageRouter(MessageRegistry registry)
{
    private readonly Dictionary<ushort, Action<NetConnection, INetMessage>> _handlers = new();

    public void Handle<T>(Action<NetConnection, T> handler) where T : INetMessage, new()
    {
        var id = registry.Register<T>();
        _handlers[id] = (conn, msg) => handler(conn, (T)msg);
    }

    public void Send<T>(NetConnection conn, T message, DeliveryMethod delivery)
        where T : INetMessage, new()
    {
        var id = registry.Register<T>();
        var writer = conn.BeginSend(NetChannel.Message);
        writer.WriteUInt16(id);
        message.Serialize(writer);
        conn.EndSend(delivery);
    }

    internal void Dispatch(NetConnection conn, NetReader reader)
    {
        var id = reader.ReadUInt16();
        var message = registry.Create(id);
        if (message is null) return;

        message.Deserialize(reader);
        if (reader.Overflow) return;
        if (_handlers.TryGetValue(id, out var handler)) handler(conn, message);
    }
}