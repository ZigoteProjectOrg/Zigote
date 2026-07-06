using Xunit;
using Zigote.Core.Math3D;
using Zigote.Network;

namespace Zigote.Tests;

public class NetworkIntegrationTests
{
    private const ushort PlayerPrefab = 1;

    private static void Pump(int frames, params NetworkManager[] managers)
    {
        for (var f = 0; f < frames; f++)
            foreach (var m in managers)
                m.Tick(1f / 60f);
    }

    private static bool PumpUntil(Func<bool> condition, int maxFrames,
        params NetworkManager[] managers)
    {
        for (var f = 0; f < maxFrames; f++)
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
        client.StartClient("localhost", port);
        Assert.True(
            PumpUntil(
                () => server.Connections.Count == 1 && client.ServerConnection is not null,
                60,
                server,
                client
            ),
            "handshake did not complete"
        );
        return (server, client);
    }

    [Fact]
    public void Client_Connects_To_Server()
    {
        var serverPeers = 0;
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
                    () => received is not null,
                    30,
                    server,
                    client
                )
            );
            Assert.Equal("hello server", received);
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
                    () => received is not null,
                    30,
                    server,
                    client
                )
            );
            Assert.Equal("broadcast", received);
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
                PumpUntil(
                    () => task.IsCompleted,
                    60,
                    server,
                    client
                ),
                "RPC did not complete"
            );
            Assert.Equal(42, task.Result.Sum);
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
            client.Replication.RegisterPrefab(PlayerPrefab, () => new TestPlayer());

            var player = server.Replication.Spawn(new TestPlayer(), PlayerPrefab);
            player.Transform = new Transform3D(new Vec3(5, 0, -3), Quat.Identity, Vec3.One);
            player.Health.Value = 80;

            Assert.True(
                PumpUntil(
                    () => client.Replication.Objects.Count == 1,
                    60,
                    server,
                    client
                ),
                "spawn did not replicate"
            );

            var clientPlayer = (TestPlayer)client.Replication.Objects.Values.First();
            Assert.True(
                PumpUntil(
                    () => clientPlayer.Health.Value == 80,
                    60,
                    server,
                    client
                ),
                "state did not replicate"
            );
            Assert.Equal(80, clientPlayer.Health.Value);
            Assert.Equal(5f, clientPlayer.NetPosition.Value.X, 2);
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
            client.Replication.RegisterPrefab(PlayerPrefab, () => new TestPlayer());
            var player = server.Replication.Spawn(new TestPlayer(), PlayerPrefab);
            Pump(20, server, client);

            var clientPlayer = (TestPlayer)client.Replication.Objects.Values.First();
            Assert.Equal(100, clientPlayer.Health.Value);

            player.Health.Value = 33;
            Assert.True(
                PumpUntil(
                    () => clientPlayer.Health.Value == 33,
                    60,
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
            client.Replication.RegisterPrefab(PlayerPrefab, () => new TestPlayer());
            var player = server.Replication.Spawn(new TestPlayer(), PlayerPrefab);
            Assert.True(
                PumpUntil(
                    () => client.Replication.Objects.Count == 1,
                    60,
                    server,
                    client
                )
            );

            server.Replication.Despawn(player);
            Assert.True(
                PumpUntil(
                    () => client.Replication.Objects.Count == 0,
                    60,
                    server,
                    client
                ),
                "despawn did not replicate"
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
                    () => dropped is not null,
                    30,
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

        public void Serialize(NetWriter w)
        {
            w.WriteString(Text);
        }

        public void Deserialize(NetReader r)
        {
            Text = r.ReadString();
        }
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

        public void Serialize(NetWriter w)
        {
            w.WriteVarInt(Sum);
        }

        public void Deserialize(NetReader r)
        {
            Sum = r.ReadVarInt();
        }
    }

    private sealed class TestPlayer : NetworkTransform
    {
        public readonly NetVar<int> Health = NetVars.Int(100);

        public TestPlayer()
        {
            Register(Health);
        }
    }
}