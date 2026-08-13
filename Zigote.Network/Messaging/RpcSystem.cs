namespace Zigote.Network;

/// <summary>
///     Request/response RPC over the reliable channel. A caller awaits a <see cref="Task{TResp}" />
///     that
///     completes when the matching response arrives (or faults on timeout); the responder registers a
///     handler
///     mapping a request type to a response. Requests are correlated by a per-call id so many can be
///     in flight.
///     Both request and response types are ordinary <see cref="INetMessage" />s sharing the message
///     registry.
/// </summary>
public sealed class RpcSystem(MessageRegistry registry, float defaultTimeout = 5f)
{
    private readonly List<uint> _expired = [];

    private readonly Dictionary<ushort, Func<NetConnection, INetMessage, INetMessage>> _handlers =
        new();

    private readonly Dictionary<uint, Pending> _pending = new();
    private uint _nextRequestId = 1;

    /// <summary>
    ///     Register the responder for request type <typeparamref name="TReq" /> producing
    ///     <typeparamref name="TResp" />.
    /// </summary>
    public void Handle<TReq, TResp>(Func<NetConnection, TReq, TResp> handler)
        where TReq : INetMessage, new()
        where TResp : INetMessage, new()
    {
        ushort reqId = registry.Register<TReq>();
        registry.Register<TResp>();
        _handlers[reqId] = (conn, msg) => handler(arg1: conn, arg2: (TReq)msg);
    }

    /// <summary>
    ///     Send a request and await the response. Faults with <see cref="TimeoutException" /> if none
    ///     arrives in time.
    /// </summary>
    public Task<TResp> Call<TReq, TResp>(NetConnection conn, TReq request, float? timeout = null)
        where TReq : INetMessage, new()
        where TResp : INetMessage, new()
    {
        ushort reqTypeId = registry.Register<TReq>();
        registry.Register<TResp>();

        uint requestId = _nextRequestId++;
        var tcs = new TaskCompletionSource<TResp>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _pending[requestId] = new Pending(
            remaining: timeout ?? defaultTimeout,
            onResponse: response => tcs.TrySetResult((TResp)response),
            onTimeout: () =>
                tcs.TrySetException(new TimeoutException($"RPC {typeof(TReq).Name} timed out."))
        );

        var writer = conn.BeginSend(NetChannel.RpcRequest);
        writer.WriteUInt32(requestId);
        writer.WriteUInt16(reqTypeId);
        request.Serialize(writer);
        conn.EndSend(DeliveryMethod.ReliableOrdered);

        return tcs.Task;
    }

    public void Update(float dt)
    {
        if (_pending.Count == 0) return;

        _expired.Clear();
        foreach ((uint id, var pending) in _pending)
        {
            pending.Remaining -= dt;
            if (pending.Remaining <= 0f) _expired.Add(id);
        }

        foreach (uint id in _expired)
        {
            if (_pending.Remove(key: id, value: out var pending))
                pending.OnTimeout();
        }
    }

    internal void HandleRequest(NetConnection conn, NetReader reader)
    {
        uint requestId = reader.ReadUInt32();
        ushort reqTypeId = reader.ReadUInt16();
        var request = registry.Create(reqTypeId);
        if (request is null) return;

        request.Deserialize(reader);
        if (reader.Overflow) return;
        if (!_handlers.TryGetValue(key: reqTypeId, value: out var handler)) return;

        var response = handler(arg1: conn, arg2: request);
        ushort respTypeId = registry.GetId(response.GetType());

        var writer = conn.BeginSend(NetChannel.RpcResponse);
        writer.WriteUInt32(requestId);
        writer.WriteUInt16(respTypeId);
        response.Serialize(writer);
        conn.EndSend(DeliveryMethod.ReliableOrdered);
    }

    internal void HandleResponse(NetConnection conn, NetReader reader)
    {
        uint requestId = reader.ReadUInt32();
        ushort respTypeId = reader.ReadUInt16();
        var response = registry.Create(respTypeId);
        if (response is null) return;

        response.Deserialize(reader);
        if (reader.Overflow) return;
        if (_pending.Remove(key: requestId, value: out var pending)) pending.OnResponse(response);
    }

    private sealed class Pending(float remaining, Action<INetMessage> onResponse, Action onTimeout)
    {
        public float Remaining = remaining;
        public Action<INetMessage> OnResponse { get; } = onResponse;
        public Action OnTimeout { get; } = onTimeout;
    }
}
