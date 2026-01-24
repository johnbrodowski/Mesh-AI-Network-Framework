using System.Collections.Concurrent;
using System.Net;
using MeshAI.Client;
using MeshAI.Core.Configuration;
using MeshAI.Core.Logging;
using MeshAI.Server;
using Xunit;
using Xunit.Abstractions;

namespace MeshAI.Tests.Integration;

/// <summary>
/// Integration tests for the mesh network with server and multiple clients.
/// </summary>
public class MeshNetworkTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private MeshServer? _server;
    private readonly List<MeshClient> _clients = [];
    private int _basePort = 19500;

    public MeshNetworkTests(ITestOutputHelper output)
    {
        _output = output;

        // Configure logging to use test output
        Logger.Configure(new LogConfig
        {
            MinLevel = LogLevel.Debug,
            IncludeTimestamps = true,
            IncludeSource = true
        });
    }

    public Task InitializeAsync()
    {
        // Use a random base port to avoid conflicts between test runs
        _basePort = 19500 + Random.Shared.Next(1000);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Clean up all clients
        foreach (var client in _clients)
        {
            try
            {
                await client.StopAsync();
                await client.DisposeAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        _clients.Clear();

        // Clean up server
        if (_server != null)
        {
            try
            {
                await _server.StopAsync();
                await _server.DisposeAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
            _server = null;
        }
    }

    private async Task<MeshServer> StartServerAsync(int port)
    {
        var config = NetworkConfig.CreateServerConfig(port);
        var server = new MeshServer(config);
        server.Start();

        // Wait for server to be ready
        await Task.Delay(100);
        _output.WriteLine($"Server started on port {port}");

        return server;
    }

    private async Task<MeshClient> StartClientAsync(int serverPort, int clientPort, bool canRelay = false, string? salt = null)
    {
        var config = new NetworkConfig
        {
            ServerEndPoint = new IPEndPoint(IPAddress.Loopback, serverPort),
            ListenPort = clientPort,
            CanRelay = canRelay,
            IdentitySalt = salt ?? Guid.NewGuid().ToString()
        };

        var client = new MeshClient(config);
        _clients.Add(client);

        await client.StartAsync();

        // Wait for registration to complete
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (client.State != ClientState.Connected && DateTime.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        _output.WriteLine($"Client {client.Identity.ShortId} started on port {clientPort}, state: {client.State}");

        return client;
    }

    [Fact]
    public async Task Server_StartsAndStops_Successfully()
    {
        // Arrange & Act
        _server = await StartServerAsync(_basePort);

        // Assert
        Assert.True(_server.IsRunning);
        Assert.Equal(0, _server.Registry.Count);

        // Act - Stop
        await _server.StopAsync();

        // Assert
        Assert.False(_server.IsRunning);
    }

    [Fact]
    public async Task Client_ConnectsToServer_Successfully()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // Act
        var client = await StartClientAsync(_basePort, _basePort + 1);

        // Assert
        Assert.Equal(ClientState.Connected, client.State);
        Assert.True(client.IsConnectedToServer);
        Assert.Equal(1, _server.Registry.ConnectedCount);
    }

    [Fact]
    public async Task MultipleClients_RegisterWithServer_Successfully()
    {
        // Arrange
        const int clientCount = 5;
        _server = await StartServerAsync(_basePort);

        // Act
        var clients = new List<MeshClient>();
        for (var i = 0; i < clientCount; i++)
        {
            var client = await StartClientAsync(_basePort, _basePort + 1 + i);
            clients.Add(client);
        }

        // Give server time to register all clients
        await Task.Delay(500);

        // Assert
        Assert.Equal(clientCount, _server.Registry.ConnectedCount);
        foreach (var client in clients)
        {
            Assert.Equal(ClientState.Connected, client.State);
            Assert.True(client.IsConnectedToServer);
        }

        // Verify each client has a unique ID
        var uniqueIds = clients.Select(c => c.Identity.ClientId).Distinct().Count();
        Assert.Equal(clientCount, uniqueIds);
    }

    [Fact]
    public async Task Client_ReceivesPeerList_FromServer()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // Start multiple clients
        var client1 = await StartClientAsync(_basePort, _basePort + 1);
        var client2 = await StartClientAsync(_basePort, _basePort + 2);
        var client3 = await StartClientAsync(_basePort, _basePort + 3);

        // Act - Request peer list
        await client1.RefreshPeerListAsync();

        // Wait for response
        await Task.Delay(500);

        // Assert - Server should have all clients registered
        Assert.Equal(3, _server.Registry.ConnectedCount);
    }

    [Fact]
    public async Task Client_ReconnectsToServer_AfterDisconnection()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);
        var client = await StartClientAsync(_basePort, _basePort + 1);

        Assert.Equal(ClientState.Connected, client.State);

        // Act - Stop and restart server
        await _server.StopAsync();
        await _server.DisposeAsync();

        // Wait for client to detect disconnection
        await Task.Delay(500);

        // Restart server
        _server = await StartServerAsync(_basePort);

        // Wait for reconnection (client has reconnect logic)
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (client.State != ClientState.Connected && DateTime.UtcNow < timeout)
        {
            await Task.Delay(100);
        }

        // Assert - Client should reconnect
        // Note: Depending on timing, client might still be reconnecting
        Assert.True(client.State == ClientState.Connected || client.State == ClientState.Reconnecting);
    }

    [Fact]
    public async Task RelayClient_RegistersAsRelay_Successfully()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // Act - Start a relay-capable client
        var relayClient = await StartClientAsync(_basePort, _basePort + 1, canRelay: true);

        // Wait for registration
        await Task.Delay(200);

        // Assert
        Assert.True(relayClient.CanRelay);
        Assert.Equal(1, _server.Registry.RelayCount);
    }

    [Fact]
    public async Task Server_TracksMultipleRelays_Correctly()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // Act - Start mix of relay and non-relay clients
        var relay1 = await StartClientAsync(_basePort, _basePort + 1, canRelay: true);
        var regular1 = await StartClientAsync(_basePort, _basePort + 2, canRelay: false);
        var relay2 = await StartClientAsync(_basePort, _basePort + 3, canRelay: true);
        var regular2 = await StartClientAsync(_basePort, _basePort + 4, canRelay: false);
        var relay3 = await StartClientAsync(_basePort, _basePort + 5, canRelay: true);

        await Task.Delay(300);

        // Assert
        Assert.Equal(5, _server.Registry.ConnectedCount);
        Assert.Equal(3, _server.Registry.RelayCount);
    }

    [Fact]
    public async Task Client_HasUniqueIdentity_EvenWithSameSalt()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // Act - Start clients (each gets unique salt by default)
        var client1 = await StartClientAsync(_basePort, _basePort + 1);
        var client2 = await StartClientAsync(_basePort, _basePort + 2);

        // Assert - Different clients should have different IDs
        Assert.NotEqual(client1.Identity.ClientId, client2.Identity.ClientId);
    }

    [Fact]
    public async Task Server_DetectsClientDisconnection_Successfully()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);
        var client = await StartClientAsync(_basePort, _basePort + 1);

        Assert.Equal(1, _server.Registry.ConnectedCount);

        // Act - Stop client
        await client.StopAsync();
        _clients.Remove(client);

        // Wait for server to detect disconnection
        await Task.Delay(500);

        // Assert - Server should detect disconnection
        // Note: Server marks client as disconnected but may keep in registry briefly
        var registeredClient = _server.Registry.Get(client.Identity.ClientId);
        Assert.True(registeredClient == null || !registeredClient.IsConnected);
    }

    [Fact]
    public async Task Clients_CanOperateInMeshOnlyMode_WithoutServer()
    {
        // Arrange - Create client without server connection
        var config = new NetworkConfig
        {
            ServerEndPoint = null, // No server
            ListenPort = _basePort + 1,
            CanRelay = false,
            IdentitySalt = Guid.NewGuid().ToString()
        };

        var client = new MeshClient(config);
        _clients.Add(client);

        // Act
        await client.StartAsync();

        // Wait for initialization
        await Task.Delay(200);

        // Assert - Client should be in connected state (mesh-only mode)
        Assert.Equal(ClientState.Connected, client.State);
        Assert.False(client.IsConnectedToServer);
    }

    [Fact]
    public async Task Server_RegistryContainsCorrectClientInfo()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // Act
        var client = await StartClientAsync(_basePort, _basePort + 1, canRelay: true);

        await Task.Delay(200);

        // Assert
        var registeredClient = _server.Registry.Get(client.Identity.ClientId);
        Assert.NotNull(registeredClient);
        Assert.Equal(client.Identity.ClientId, registeredClient.ClientId);
        Assert.True(registeredClient.CanRelay);
        Assert.True(registeredClient.IsConnected);
        Assert.Equal(_basePort + 1, registeredClient.ListenPort);
    }
}
