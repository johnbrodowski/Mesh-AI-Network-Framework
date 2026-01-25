using System.Collections.Concurrent;
using System.Net;
using System.Text;
using MeshAI.Client;
using MeshAI.Core.Configuration;
using MeshAI.Core.Logging;
using MeshAI.Network.Routing;
using MeshAI.Server;
using Xunit;
using Xunit.Abstractions;

namespace MeshAI.Tests.Integration;

/// <summary>
/// Integration tests for relay routing functionality.
/// </summary>
public class RelayRoutingTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private MeshServer? _server;
    private readonly List<MeshClient> _clients = [];
    private readonly ConcurrentDictionary<string, List<(string SenderId, string Message)>> _receivedMessages = new();
    private int _basePort;

    public RelayRoutingTests(ITestOutputHelper output)
    {
        _output = output;

        Logger.Configure(new LogConfig
        {
            MinLevel = LogLevel.Debug,
            IncludeTimestamps = true,
            IncludeSource = true
        });
    }

    public Task InitializeAsync()
    {
        _basePort = 21500 + Random.Shared.Next(1000);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try
            {
                await client.StopAsync();
                await client.DisposeAsync();
            }
            catch { }
        }
        _clients.Clear();

        if (_server != null)
        {
            try
            {
                await _server.StopAsync();
                await _server.DisposeAsync();
            }
            catch { }
            _server = null;
        }
    }

    private async Task<MeshServer> StartServerAsync(int port)
    {
        var config = NetworkConfig.CreateServerConfig(port);
        var server = new MeshServer(config);
        server.Start();
        await Task.Delay(100);
        return server;
    }

    private async Task<MeshClient> StartClientAsync(int serverPort, int clientPort, bool canRelay = false)
    {
        var config = new NetworkConfig
        {
            ServerEndPoint = new IPEndPoint(IPAddress.Loopback, serverPort),
            ListenPort = clientPort,
            CanRelay = canRelay,
            IdentitySalt = Guid.NewGuid().ToString(),
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        };

        var client = new MeshClient(config);
        _clients.Add(client);

        var clientId = client.Identity.ClientId;
        _receivedMessages[clientId] = [];

        client.MessageReceived += async (senderId, data) =>
        {
            var message = Encoding.UTF8.GetString(data);
            _receivedMessages[clientId].Add((senderId, message));
            _output.WriteLine($"[{clientId[..8]}] Received from {senderId[..8]}: {message}");
            await Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await client.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine($"Client startup timed out on port {clientPort}");
            throw new TimeoutException($"Client failed to start within timeout");
        }

        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (client.State != ClientState.Connected &&
               client.State != ClientState.Failed &&
               DateTime.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        _output.WriteLine($"Client {client.Identity.ShortId} started (relay={canRelay}), state: {client.State}");
        return client;
    }

    [Fact]
    public async Task RelayClient_IsAvailableInRegistry()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // Act
        var relay = await StartClientAsync(_basePort, _basePort + 1, canRelay: true);
        var regular = await StartClientAsync(_basePort, _basePort + 2, canRelay: false);

        await Task.Delay(300);

        // Assert
        Assert.Equal(2, _server.Registry.ConnectedCount);
        Assert.Equal(1, _server.Registry.RelayCount);

        var relays = _server.Registry.GetRelays().ToList();
        Assert.Single(relays);
        Assert.Equal(relay.Identity.ClientId, relays[0].ClientId);
    }

    [Fact]
    public async Task Server_ProvidesRelayRoutes_ForClients()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        var relay = await StartClientAsync(_basePort, _basePort + 1, canRelay: true);
        var sender = await StartClientAsync(_basePort, _basePort + 2);
        var receiver = await StartClientAsync(_basePort, _basePort + 3);

        await Task.Delay(500);

        // Act - Request route to receiver
        await sender.RequestRouteAsync(receiver.Identity.ClientId);

        await Task.Delay(300);

        // Assert - Server should have relay available
        var relays = _server.Registry.GetRelays().ToList();
        Assert.Single(relays);
        Assert.True(relays[0].CanRelay);
    }

    [Fact]
    public async Task MultipleRelays_RegisterSuccessfully()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // Act - Start multiple relays
        var relay1 = await StartClientAsync(_basePort, _basePort + 1, canRelay: true);
        var relay2 = await StartClientAsync(_basePort, _basePort + 2, canRelay: true);
        var relay3 = await StartClientAsync(_basePort, _basePort + 3, canRelay: true);
        var regular = await StartClientAsync(_basePort, _basePort + 4, canRelay: false);

        await Task.Delay(500);

        // Assert
        Assert.Equal(4, _server.Registry.ConnectedCount);
        Assert.Equal(3, _server.Registry.RelayCount);

        var relays = _server.Registry.GetRelays().ToList();
        Assert.Equal(3, relays.Count);
        Assert.All(relays, r => Assert.True(r.CanRelay));
    }

    [Fact]
    public async Task Message_RoutedThroughServer_WhenDirectConnectionUnavailable()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        var sender = await StartClientAsync(_basePort, _basePort + 1);
        var receiver = await StartClientAsync(_basePort, _basePort + 2);

        await Task.Delay(500);
        await sender.RefreshPeerListAsync();
        await Task.Delay(300);

        // Act - Send message (will route through server since no direct peer connection)
        var messageText = "Message routed through server";
        var sent = await sender.SendMessageAsync(
            receiver.Identity.ClientId,
            Encoding.UTF8.GetBytes(messageText));

        await Task.Delay(1000);

        // Assert
        Assert.True(sent);
        var receiverMessages = _receivedMessages[receiver.Identity.ClientId];
        Assert.Contains(receiverMessages, m => m.Message == messageText);
    }

    [Fact]
    public async Task NetworkWithRelays_HandlesTraffic_Successfully()
    {
        // Arrange - Create network topology
        _server = await StartServerAsync(_basePort);

        // Two relay nodes
        var relay1 = await StartClientAsync(_basePort, _basePort + 1, canRelay: true);
        var relay2 = await StartClientAsync(_basePort, _basePort + 2, canRelay: true);

        // Regular clients
        var clientA = await StartClientAsync(_basePort, _basePort + 3);
        var clientB = await StartClientAsync(_basePort, _basePort + 4);
        var clientC = await StartClientAsync(_basePort, _basePort + 5);

        await Task.Delay(500);

        // Refresh all peer lists
        foreach (var client in _clients)
        {
            await client.RefreshPeerListAsync();
        }
        await Task.Delay(500);

        // Act - Send messages between clients
        await clientA.SendMessageAsync(clientB.Identity.ClientId, Encoding.UTF8.GetBytes("A to B"));
        await clientB.SendMessageAsync(clientC.Identity.ClientId, Encoding.UTF8.GetBytes("B to C"));
        await clientC.SendMessageAsync(clientA.Identity.ClientId, Encoding.UTF8.GetBytes("C to A"));

        await Task.Delay(1500);

        // Assert - Messages should be delivered
        Assert.Contains(_receivedMessages[clientB.Identity.ClientId], m => m.Message == "A to B");
        Assert.Contains(_receivedMessages[clientC.Identity.ClientId], m => m.Message == "B to C");
        Assert.Contains(_receivedMessages[clientA.Identity.ClientId], m => m.Message == "C to A");
    }

    [Fact]
    public async Task RouteTable_TracksRouteQuality()
    {
        // Arrange
        var routeTable = new RouteTable();

        var destination = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var relay = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        // Act - Add different route types
        routeTable.AddRoute(new Route
        {
            DestinationId = destination,
            Type = RouteType.Direct,
            QualityScore = 80
        });

        routeTable.AddRoute(new Route
        {
            DestinationId = destination,
            Type = RouteType.Relay,
            NextHopId = relay,
            Path = [relay, destination],
            QualityScore = 60
        });

        routeTable.AddRoute(new Route
        {
            DestinationId = destination,
            Type = RouteType.ServerMediated,
            QualityScore = 40
        });

        // Assert - Best route should be direct
        var bestRoute = routeTable.GetBestRoute(destination);
        Assert.NotNull(bestRoute);
        Assert.Equal(RouteType.Direct, bestRoute.Type);

        // All routes should be available
        var allRoutes = routeTable.GetRoutes(destination).ToList();
        Assert.Equal(3, allRoutes.Count);
    }

    [Fact]
    public async Task RouteTable_UpdatesQualityOnSuccess()
    {
        // Arrange
        var routeTable = new RouteTable();
        var destination = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        routeTable.AddRoute(new Route
        {
            DestinationId = destination,
            Type = RouteType.Direct,
            QualityScore = 50,
            EstimatedLatencyMs = 100
        });

        // Act - Record successes
        routeTable.RecordSuccess(destination, RouteType.Direct, 50);
        routeTable.RecordSuccess(destination, RouteType.Direct, 40);
        routeTable.RecordSuccess(destination, RouteType.Direct, 30);

        // Assert
        var route = routeTable.GetBestRoute(destination);
        Assert.NotNull(route);
        Assert.True(route.QualityScore > 50, "Quality should increase after successes");
        Assert.True(route.EstimatedLatencyMs < 100, "Latency should decrease");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task RouteTable_DegradeQualityOnFailure()
    {
        // Arrange
        var routeTable = new RouteTable();
        var destination = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        routeTable.AddRoute(new Route
        {
            DestinationId = destination,
            Type = RouteType.Direct,
            QualityScore = 80
        });

        // Act - Record failures
        routeTable.RecordFailure(destination, RouteType.Direct);
        routeTable.RecordFailure(destination, RouteType.Direct);

        // Assert
        var route = routeTable.GetBestRoute(destination);
        Assert.NotNull(route);
        Assert.True(route.QualityScore < 80, "Quality should decrease after failures");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task RouteTable_InvalidatesRoutes_OnPeerDisconnect()
    {
        // Arrange
        var routeTable = new RouteTable();

        var destination = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var relay = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        routeTable.AddRoute(new Route
        {
            DestinationId = destination,
            Type = RouteType.Relay,
            NextHopId = relay,
            Path = [relay, destination],
            QualityScore = 70
        });

        // Act - Invalidate routes through relay
        routeTable.InvalidateRoutesVia(relay);

        // Assert
        var route = routeTable.GetBestRoute(destination);
        Assert.Null(route); // Route should be invalidated

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Server_SuggestsOptimalRoute_BasedOnTopology()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // Create network with relay
        var relay = await StartClientAsync(_basePort, _basePort + 1, canRelay: true);
        var sender = await StartClientAsync(_basePort, _basePort + 2);
        var receiver = await StartClientAsync(_basePort, _basePort + 3);

        await Task.Delay(500);

        // Assert - Server should recognize relay capability
        var registeredRelay = _server.Registry.Get(relay.Identity.ClientId);
        Assert.NotNull(registeredRelay);
        Assert.True(registeredRelay.CanRelay);

        // Server should have all clients
        Assert.Equal(3, _server.Registry.ConnectedCount);
    }

    [Fact]
    public async Task Client_FallsBackToServer_WhenRelayUnavailable()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        // No relay nodes
        var sender = await StartClientAsync(_basePort, _basePort + 1);
        var receiver = await StartClientAsync(_basePort, _basePort + 2);

        await Task.Delay(500);
        await sender.RefreshPeerListAsync();
        await Task.Delay(300);

        // Act - Send message (should route through server)
        var messageText = "Fallback to server";
        var sent = await sender.SendMessageAsync(
            receiver.Identity.ClientId,
            Encoding.UTF8.GetBytes(messageText));

        await Task.Delay(1000);

        // Assert
        Assert.True(sent);
        var receiverMessages = _receivedMessages[receiver.Identity.ClientId];
        Assert.Contains(receiverMessages, m => m.Message == messageText);
    }
}
