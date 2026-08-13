// These tests exercise threading directly: the bounded Wait/WaitAll calls with explicit
// timeouts ARE the assertions (a deadlock must fail fast, not hang), so awaiting instead would
// defeat the test. Cancellation is likewise irrelevant to a wait that is already time-bounded.

#pragma warning disable xUnit1031, xUnit1051
using Xunit;
using Zigote.Core.Math3D;
using Zigote.Network;

namespace Zigote.Tests;

public class NetworkIntegrationTests
{
    private const ushort PlayerPrefab = 1;

    private static void Pump(int frames, params NetworkManager[] managers)
    {
        for (int f = 0; f < frames; f++)
        {
            foreach (var m in managers)
                m.Tick(1f / 60f);
        }
    }

    private static bool PumpUntil(Func<bool> condition, int maxFrames,
        params NetworkManager[] managers)
    {
        for (int f = 0; f < maxFrames; f++)
        {
            foreach (var m in managers) m.Tick(1f / 60f);
            if (condition()) return true;
        }

        return false;
    }

    private static (NetworkManager server, NetworkManager client) Connect(int port)
    {
        var server = new NetworkManager(new LoopbackTransport());
        var client = new NetworkManager(new LoopbackTransport());
        server.StartServer(port);
        client.StartClient(host: "localhost", port: port);
        Assert.True(
            condition: PumpUntil(
                condition: () =>
                    server.Connections.Count == 1 && client.ServerConnection is not null,
                maxFrames: 60,
                server,
                client
            ),
            userMessage: "handshake did not complete"
        );
        return (server, client);
    }

    [Fact]
    public void Client_Connects_To_Server()
    {
        int serverPeers = 0;
        var (server, client) = Connect(41001);
        server.PeerConnected += _ => serverPeers++;
        try
        {
            Assert.Single(server.Connections);
            Assert.NotNull(client.ServerConnection);
            Assert.True(server.IsServer);
            Assert.True(client.IsClient);
        }
        finally
        {
            server.Stop();
            client.Stop();
        }
    }

    [Fact]
    public void Message_Reaches_Server_Handler()
    {
        var (server, client) = Connect(41002);
        try
        {
            string? received = null;
            server.OnMessage<ChatMessage>((_, m) => received = m.Text);
            client.SendToServer(new ChatMessage { Text = "hello server" });

            Assert.True(
                PumpUntil(
                    condition: () => received is not null,
                    maxFrames: 30,
                    server,
                    client
                )
            );
            Assert.Equal(expected: "hello server", actual: received);
        }
        finally
        {
            server.Stop();
            client.Stop();
        }
    }

    [Fact]
    public void Server_Broadcast_Reaches_Client()
    {
        var (server, client) = Connect(41003);
        try
        {
            string? received = null;
            client.OnMessage<ChatMessage>((_, m) => received = m.Text);
            server.SendToAll(new ChatMessage { Text = "broadcast" });

            Assert.True(
                PumpUntil(
                    condition: () => received is not null,
                    maxFrames: 30,
                    server,
                    client
                )
            );
            Assert.Equal(expected: "broadcast", actual: received);
        }
        finally
        {
            server.Stop();
            client.Stop();
        }
    }

    [Fact]
    public void Rpc_Request_Response_Completes()
    {
        var (server, client) = Connect(41004);
        try
        {
            server.Rpc.Handle<AddRequest, AddResponse>((_, req) =>
                new AddResponse { Sum = req.A + req.B }
            );

            var task = client.CallServer<AddRequest, AddResponse>(
                new AddRequest {
                    A = 7,
                    B = 35,
                }
            );
            Assert.True(
                condition: PumpUntil(
                    condition: () => task.IsCompleted,
                    maxFrames: 60,
                    server,
                    client
                ),
                userMessage: "RPC did not complete"
            );
            Assert.Equal(expected: 42, actual: task.Result.Sum);
        }
        finally
        {
            server.Stop();
            client.Stop();
        }
    }

    [Fact]
    public void Object_Spawns_And_Replicates_To_Client()
    {
        var (server, client) = Connect(41005);
        try
        {
            client.Replication.RegisterPrefab(
                prefabId: PlayerPrefab,
                factory: () => new TestPlayer()
            );

            var player = server.Replication.Spawn(obj: new TestPlayer(), prefabId: PlayerPrefab);
            player.Transform = new Transform3D(
                position: new Vec3(x: 5, y: 0, z: -3),
                rotation: Quat.Identity,
                scale: Vec3.One
            );
            player.Health.Value = 80;

            Assert.True(
                condition: PumpUntil(
                    condition: () => client.Replication.Objects.Count == 1,
                    maxFrames: 60,
                    server,
                    client
                ),
                userMessage: "spawn did not replicate"
            );

            var clientPlayer = (TestPlayer)client.Replication.Objects.Values.First();
            Assert.True(
                condition: PumpUntil(
                    condition: () => clientPlayer.Health.Value == 80,
                    maxFrames: 60,
                    server,
                    client
                ),
                userMessage: "state did not replicate"
            );
            Assert.Equal(expected: 80, actual: clientPlayer.Health.Value);
            Assert.Equal(expected: 5f, actual: clientPlayer.NetPosition.Value.X, precision: 2);
        }
        finally
        {
            server.Stop();
            client.Stop();
        }
    }

    [Fact]
    public void Var_Change_Propagates_After_Spawn()
    {
        var (server, client) = Connect(41006);
        try
        {
            client.Replication.RegisterPrefab(
                prefabId: PlayerPrefab,
                factory: () => new TestPlayer()
            );
            var player = server.Replication.Spawn(obj: new TestPlayer(), prefabId: PlayerPrefab);
            Pump(frames: 20, server, client);

            var clientPlayer = (TestPlayer)client.Replication.Objects.Values.First();
            Assert.Equal(expected: 100, actual: clientPlayer.Health.Value);

            player.Health.Value = 33;
            Assert.True(
                PumpUntil(
                    condition: () => clientPlayer.Health.Value == 33,
                    maxFrames: 60,
                    server,
                    client
                )
            );
        }
        finally
        {
            server.Stop();
            client.Stop();
        }
    }

    [Fact]
    public void Despawn_Removes_Object_On_Client()
    {
        var (server, client) = Connect(41007);
        try
        {
            client.Replication.RegisterPrefab(
                prefabId: PlayerPrefab,
                factory: () => new TestPlayer()
            );
            var player = server.Replication.Spawn(obj: new TestPlayer(), prefabId: PlayerPrefab);
            Assert.True(
                PumpUntil(
                    condition: () => client.Replication.Objects.Count == 1,
                    maxFrames: 60,
                    server,
                    client
                )
            );

            server.Replication.Despawn(player);
            Assert.True(
                condition: PumpUntil(
                    condition: () => client.Replication.Objects.Count == 0,
                    maxFrames: 60,
                    server,
                    client
                ),
                userMessage: "despawn did not replicate"
            );
        }
        finally
        {
            server.Stop();
            client.Stop();
        }
    }

    [Fact]
    public void Disconnect_Fires_Server_Event()
    {
        var (server, client) = Connect(41008);
        try
        {
            NetConnection? dropped = null;
            server.PeerDisconnected += (c, _) => dropped = c;

            client.ServerConnection!.Disconnect();
            Assert.True(
                PumpUntil(
                    condition: () => dropped is not null,
                    maxFrames: 30,
                    server,
                    client
                )
            );
        }
        finally
        {
            server.Stop();
            client.Stop();
        }
    }

    // ── Test message + object types ─────────────────────────────────────────
    private sealed class ChatMessage : INetMessage
    {
        public string Text = "";

        public void Serialize(NetWriter w) => w.WriteString(Text);

        public void Deserialize(NetReader r) => Text = r.ReadString();
    }

    private sealed class AddRequest : INetMessage
    {
        public int A, B;

        public void Serialize(NetWriter w)
        {
            w.WriteVarInt(A);
            w.WriteVarInt(B);
        }

        public void Deserialize(NetReader r)
        {
            A = r.ReadVarInt();
            B = r.ReadVarInt();
        }
    }

    private sealed class AddResponse : INetMessage
    {
        public int Sum;

        public void Serialize(NetWriter w) => w.WriteVarInt(Sum);

        public void Deserialize(NetReader r) => Sum = r.ReadVarInt();
    }

    private sealed class TestPlayer : NetworkTransform
    {
        public readonly NetVar<int> Health = NetVars.Int(100);

        public TestPlayer() => Register(Health);
    }
}

#pragma warning restore xUnit1031, xUnit1051
