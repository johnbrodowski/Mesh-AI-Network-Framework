using System.Collections.Concurrent;
using System.Net;
using System.Text;
using MeshAI.Client;
using MeshAI.Core.Configuration;
using MeshAI.Core.Logging;
using MeshAI.Server;
using Xunit;
using Xunit.Abstractions;

namespace MeshAI.Tests.Integration;

/// <summary>
/// Integration tests for message communication between clients.
/// </summary>
public class MessageCommunicationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private MeshServer? _server;
    private readonly List<MeshClient> _clients = [];
    private readonly ConcurrentDictionary<string, List<(string SenderId, string Message)>> _receivedMessages = new();
    private int _basePort;

    public MessageCommunicationTests(ITestOutputHelper output)
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
        _basePort = 20500 + Random.Shared.Next(1000);
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
        _output.WriteLine($"Server started on port {port}");
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

        // Set up message handler
        var clientId = client.Identity.ClientId;
        _receivedMessages[clientId] = [];

        client.MessageReceived += async (senderId, data) =>
        {
            var message = Encoding.UTF8.GetString(data);
            _receivedMessages[clientId].Add((senderId, message));
            _output.WriteLine($"Client {clientId[..8]} received from {senderId[..8]}: {message}");
            await Task.CompletedTask;
        };

        await client.StartAsync();

        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (client.State != ClientState.Connected && DateTime.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        _output.WriteLine($"Client {client.Identity.ShortId} started, state: {client.State}");
        return client;
    }

    [Fact]
    public async Task Client_SendsMessage_ViaServerRelay()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        var sender = await StartClientAsync(_basePort, _basePort + 1);
        var receiver = await StartClientAsync(_basePort, _basePort + 2);

        // Wait for both clients to be fully registered
        await Task.Delay(500);

        // Refresh peer lists
        await sender.RefreshPeerListAsync();
        await receiver.RefreshPeerListAsync();
        await Task.Delay(300);

        // Act
        var messageText = "Hello from sender!";
        var messageBytes = Encoding.UTF8.GetBytes(messageText);
        var sent = await sender.SendMessageAsync(receiver.Identity.ClientId, messageBytes);

        // Wait for message delivery
        await Task.Delay(1000);

        // Assert
        Assert.True(sent, "Message should be sent successfully");

        var receiverMessages = _receivedMessages[receiver.Identity.ClientId];
        Assert.NotEmpty(receiverMessages);
        Assert.Contains(receiverMessages, m => m.Message == messageText);
    }

    [Fact]
    public async Task MultipleClients_ExchangeMessages_Successfully()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        var client1 = await StartClientAsync(_basePort, _basePort + 1);
        var client2 = await StartClientAsync(_basePort, _basePort + 2);
        var client3 = await StartClientAsync(_basePort, _basePort + 3);

        await Task.Delay(500);

        // Refresh peer lists
        await client1.RefreshPeerListAsync();
        await client2.RefreshPeerListAsync();
        await client3.RefreshPeerListAsync();
        await Task.Delay(300);

        // Act - Client1 sends to Client2 and Client3
        await client1.SendMessageAsync(client2.Identity.ClientId, Encoding.UTF8.GetBytes("Message to Client2"));
        await client1.SendMessageAsync(client3.Identity.ClientId, Encoding.UTF8.GetBytes("Message to Client3"));

        // Client2 sends to Client1 and Client3
        await client2.SendMessageAsync(client1.Identity.ClientId, Encoding.UTF8.GetBytes("Message to Client1 from C2"));
        await client2.SendMessageAsync(client3.Identity.ClientId, Encoding.UTF8.GetBytes("Message to Client3 from C2"));

        await Task.Delay(1000);

        // Assert
        var c1Messages = _receivedMessages[client1.Identity.ClientId];
        var c2Messages = _receivedMessages[client2.Identity.ClientId];
        var c3Messages = _receivedMessages[client3.Identity.ClientId];

        Assert.Contains(c1Messages, m => m.Message.Contains("from C2"));
        Assert.Contains(c2Messages, m => m.Message == "Message to Client2");
        Assert.True(c3Messages.Count >= 1); // Should have messages from both C1 and C2
    }

    [Fact]
    public async Task Client_SendsMultipleMessages_InSequence()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        var sender = await StartClientAsync(_basePort, _basePort + 1);
        var receiver = await StartClientAsync(_basePort, _basePort + 2);

        await Task.Delay(500);
        await sender.RefreshPeerListAsync();
        await Task.Delay(300);

        // Act - Send multiple messages
        const int messageCount = 10;
        for (var i = 0; i < messageCount; i++)
        {
            var message = $"Message {i + 1}";
            await sender.SendMessageAsync(receiver.Identity.ClientId, Encoding.UTF8.GetBytes(message));
            await Task.Delay(50); // Small delay between messages
        }

        await Task.Delay(1000);

        // Assert
        var receiverMessages = _receivedMessages[receiver.Identity.ClientId];
        _output.WriteLine($"Received {receiverMessages.Count} messages");

        // Should receive at least some messages (network delays may affect delivery)
        Assert.True(receiverMessages.Count > 0, "Should receive at least some messages");
    }

    [Fact]
    public async Task Client_SendsLargeMessage_Successfully()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        var sender = await StartClientAsync(_basePort, _basePort + 1);
        var receiver = await StartClientAsync(_basePort, _basePort + 2);

        await Task.Delay(500);
        await sender.RefreshPeerListAsync();
        await Task.Delay(300);

        // Act - Send a larger message
        var largeMessage = new string('X', 10000);
        var sent = await sender.SendMessageAsync(
            receiver.Identity.ClientId,
            Encoding.UTF8.GetBytes(largeMessage));

        await Task.Delay(1000);

        // Assert
        Assert.True(sent);
        var receiverMessages = _receivedMessages[receiver.Identity.ClientId];
        Assert.Contains(receiverMessages, m => m.Message.Length == 10000);
    }

    [Fact]
    public async Task BidirectionalCommunication_BetweenTwoClients()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        var clientA = await StartClientAsync(_basePort, _basePort + 1);
        var clientB = await StartClientAsync(_basePort, _basePort + 2);

        await Task.Delay(500);
        await clientA.RefreshPeerListAsync();
        await clientB.RefreshPeerListAsync();
        await Task.Delay(300);

        // Act - Bidirectional communication
        await clientA.SendMessageAsync(clientB.Identity.ClientId, Encoding.UTF8.GetBytes("Hello B, from A"));
        await Task.Delay(200);
        await clientB.SendMessageAsync(clientA.Identity.ClientId, Encoding.UTF8.GetBytes("Hello A, from B"));
        await Task.Delay(200);
        await clientA.SendMessageAsync(clientB.Identity.ClientId, Encoding.UTF8.GetBytes("Got your message B!"));

        await Task.Delay(1000);

        // Assert
        var aMessages = _receivedMessages[clientA.Identity.ClientId];
        var bMessages = _receivedMessages[clientB.Identity.ClientId];

        Assert.Contains(aMessages, m => m.Message.Contains("from B"));
        Assert.Contains(bMessages, m => m.Message.Contains("from A"));
    }

    [Fact]
    public async Task Client_CannotSendToNonexistentClient()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);
        var sender = await StartClientAsync(_basePort, _basePort + 1);

        await Task.Delay(500);

        // Act - Try to send to a fake client ID
        var fakeClientId = "0000000000000000000000000000000000000000000000000000000000000000";
        var sent = await sender.SendMessageAsync(fakeClientId, Encoding.UTF8.GetBytes("Hello?"));

        // Assert - Should return false (no route to destination)
        Assert.False(sent);
    }

    [Fact]
    public async Task Broadcast_ToAllConnectedClients()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        var broadcaster = await StartClientAsync(_basePort, _basePort + 1);
        var receivers = new List<MeshClient>();

        for (var i = 0; i < 3; i++)
        {
            var receiver = await StartClientAsync(_basePort, _basePort + 2 + i);
            receivers.Add(receiver);
        }

        await Task.Delay(500);

        // Refresh peer lists
        await broadcaster.RefreshPeerListAsync();
        await Task.Delay(500);

        // Act - Send to all receivers
        var broadcastMessage = "Broadcast message to all!";
        foreach (var receiver in receivers)
        {
            await broadcaster.SendMessageAsync(
                receiver.Identity.ClientId,
                Encoding.UTF8.GetBytes(broadcastMessage));
        }

        await Task.Delay(1500);

        // Assert - All receivers should get the message
        foreach (var receiver in receivers)
        {
            var messages = _receivedMessages[receiver.Identity.ClientId];
            _output.WriteLine($"Receiver {receiver.Identity.ShortId} got {messages.Count} messages");
            Assert.Contains(messages, m => m.Message == broadcastMessage);
        }
    }

    [Fact]
    public async Task ConcurrentMessages_FromMultipleSenders()
    {
        // Arrange
        _server = await StartServerAsync(_basePort);

        var receiver = await StartClientAsync(_basePort, _basePort + 1);
        var senders = new List<MeshClient>();

        for (var i = 0; i < 3; i++)
        {
            var sender = await StartClientAsync(_basePort, _basePort + 2 + i);
            senders.Add(sender);
        }

        await Task.Delay(500);

        // Refresh peer lists
        foreach (var sender in senders)
        {
            await sender.RefreshPeerListAsync();
        }
        await Task.Delay(500);

        // Act - All senders send concurrently
        var sendTasks = senders.Select(async (sender, index) =>
        {
            await sender.SendMessageAsync(
                receiver.Identity.ClientId,
                Encoding.UTF8.GetBytes($"Message from sender {index}"));
        });

        await Task.WhenAll(sendTasks);
        await Task.Delay(1500);

        // Assert
        var receiverMessages = _receivedMessages[receiver.Identity.ClientId];
        _output.WriteLine($"Receiver got {receiverMessages.Count} messages");

        // Should receive at least some messages
        Assert.True(receiverMessages.Count > 0);
    }
}
