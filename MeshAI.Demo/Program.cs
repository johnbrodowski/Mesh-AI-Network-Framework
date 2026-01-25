using System.Net;
using System.Text;
using MeshAI.Client;
using MeshAI.Core.Configuration;
using MeshAI.Core.Identity;
using MeshAI.Core.Logging;
using MeshAI.Server;

namespace MeshAI.Demo;

/// <summary>
/// Demonstration of the MeshAI hybrid mesh-server network.
///
/// This demo creates:
/// - 1 server
/// - 3 clients (Alice, Bob, Charlie)
///
/// It demonstrates:
/// - Client registration with the server
/// - Direct messaging between clients
/// - Server-mediated message routing
/// - Peer discovery
/// </summary>
public static class Program
{
    private const int ServerPort = 19500;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("  MeshAI - Hybrid Mesh-Server Network Demo");
        Console.WriteLine("=================================================");
        Console.WriteLine();

        // Configure logging
        var verbose = args.Contains("--verbose") || args.Contains("-v");
        Logger.Configure(new LogConfig
        {
            MinLevel = verbose ? LogLevel.Debug : LogLevel.Warning,
            IncludeTimestamps = true,
            IncludeSource = true
        });

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await RunDemoAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nDemo cancelled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
            if (verbose)
                Console.WriteLine(ex.StackTrace);
        }
    }

    private static async Task RunDemoAsync(CancellationToken cancellationToken)
    {
        // ==================== STEP 1: Start Server ====================
        Console.WriteLine("[1/6] Starting MeshAI Server...");

        var serverConfig = NetworkConfig.CreateServerConfig(ServerPort);
        await using var server = new MeshServer(serverConfig);
        server.Start();

        Console.WriteLine($"      Server running on port {ServerPort}");
        Console.WriteLine();

        await Task.Delay(500, cancellationToken);

        // ==================== STEP 2: Create Clients ====================
        Console.WriteLine("[2/6] Creating clients (Alice, Bob, Charlie)...");

        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, ServerPort);

        // Create three clients with different identities
        var alice = await CreateClientAsync("Alice", serverEndpoint, ServerPort + 1, canRelay: true, cancellationToken);
        var bob = await CreateClientAsync("Bob", serverEndpoint, ServerPort + 2, canRelay: false, cancellationToken);
        var charlie = await CreateClientAsync("Charlie", serverEndpoint, ServerPort + 3, canRelay: true, cancellationToken);

        Console.WriteLine();
        Console.WriteLine("      Client IDs:");
        Console.WriteLine($"        Alice:   {alice.Client.Identity.ClientId[..16]}...");
        Console.WriteLine($"        Bob:     {bob.Client.Identity.ClientId[..16]}...");
        Console.WriteLine($"        Charlie: {charlie.Client.Identity.ClientId[..16]}...");
        Console.WriteLine();

        // ==================== STEP 3: Connect to Server ====================
        Console.WriteLine("[3/6] Connecting clients to server...");

        await alice.Client.StartAsync(cancellationToken);
        await bob.Client.StartAsync(cancellationToken);
        await charlie.Client.StartAsync(cancellationToken);

        // Wait for connections and peer discovery
        await Task.Delay(1500, cancellationToken);

        Console.WriteLine($"      Server reports {server.Registry.ConnectedCount} connected clients");
        Console.WriteLine($"      Server reports {server.Registry.RelayCount} relay-capable clients");
        Console.WriteLine();

        // ==================== STEP 4: Peer Discovery ====================
        Console.WriteLine("[4/6] Requesting peer lists...");

        await alice.Client.RefreshPeerListAsync(cancellationToken);
        await bob.Client.RefreshPeerListAsync(cancellationToken);
        await charlie.Client.RefreshPeerListAsync(cancellationToken);

        await Task.Delay(1000, cancellationToken);

        Console.WriteLine($"      Alice knows {alice.Client.KnownPeers.Count()} peers");
        Console.WriteLine($"      Bob knows {bob.Client.KnownPeers.Count()} peers");
        Console.WriteLine($"      Charlie knows {charlie.Client.KnownPeers.Count()} peers");
        Console.WriteLine();

        // ==================== STEP 5: Send Messages ====================
        Console.WriteLine("[5/6] Sending messages...");
        Console.WriteLine();

        // Alice sends to Bob
        Console.WriteLine("      Alice -> Bob: \"Hello Bob, this is Alice!\"");
        var sent1 = await alice.Client.SendMessageAsync(
            bob.Client.Identity.ClientId,
            Encoding.UTF8.GetBytes("Hello Bob, this is Alice!"),
            cancellationToken);
        Console.WriteLine($"      Result: {(sent1 ? "Sent successfully" : "Failed to send")}");

        await Task.Delay(500, cancellationToken);

        // Bob sends to Charlie
        Console.WriteLine("      Bob -> Charlie: \"Hey Charlie, greetings from Bob!\"");
        var sent2 = await bob.Client.SendMessageAsync(
            charlie.Client.Identity.ClientId,
            Encoding.UTF8.GetBytes("Hey Charlie, greetings from Bob!"),
            cancellationToken);
        Console.WriteLine($"      Result: {(sent2 ? "Sent successfully" : "Failed to send")}");

        await Task.Delay(500, cancellationToken);

        // Charlie broadcasts to everyone
        Console.WriteLine("      Charlie -> Alice: \"Alice, Charlie here!\"");
        var sent3 = await charlie.Client.SendMessageAsync(
            alice.Client.Identity.ClientId,
            Encoding.UTF8.GetBytes("Alice, Charlie here!"),
            cancellationToken);
        Console.WriteLine($"      Result: {(sent3 ? "Sent successfully" : "Failed to send")}");

        await Task.Delay(1000, cancellationToken);

        // ==================== STEP 6: Show Results ====================
        Console.WriteLine();
        Console.WriteLine("[6/6] Messages received:");
        Console.WriteLine();

        Console.WriteLine($"      Alice received {alice.Messages.Count} message(s):");
        foreach (var msg in alice.Messages)
        {
            Console.WriteLine($"        From {msg.SourceId[..8]}...: \"{msg.Text}\"");
        }

        Console.WriteLine($"      Bob received {bob.Messages.Count} message(s):");
        foreach (var msg in bob.Messages)
        {
            Console.WriteLine($"        From {msg.SourceId[..8]}...: \"{msg.Text}\"");
        }

        Console.WriteLine($"      Charlie received {charlie.Messages.Count} message(s):");
        foreach (var msg in charlie.Messages)
        {
            Console.WriteLine($"        From {msg.SourceId[..8]}...: \"{msg.Text}\"");
        }

        Console.WriteLine();
        Console.WriteLine("=================================================");
        Console.WriteLine("  Demo Complete!");
        Console.WriteLine("=================================================");
        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine($"  - Server handled {server.Registry.ConnectedCount} clients");
        Console.WriteLine($"  - {alice.Messages.Count + bob.Messages.Count + charlie.Messages.Count} messages successfully delivered");
        Console.WriteLine();

        // Interactive mode
        Console.WriteLine("Press 'q' to quit, or enter commands:");
        Console.WriteLine("  status    - Show network status");
        Console.WriteLine("  peers     - Show peer connections");
        Console.WriteLine("  send      - Send a test message");
        Console.WriteLine();

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = await ReadLineAsync(cancellationToken);
            if (input == null) break;

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            switch (parts[0].ToLowerInvariant())
            {
                case "q" or "quit" or "exit":
                    return;

                case "status":
                    Console.WriteLine($"Server: {server.Registry.ConnectedCount} clients, {server.Registry.RelayCount} relays");
                    Console.WriteLine($"Alice: State={alice.Client.State}, Server={alice.Client.IsConnectedToServer}");
                    Console.WriteLine($"Bob: State={bob.Client.State}, Server={bob.Client.IsConnectedToServer}");
                    Console.WriteLine($"Charlie: State={charlie.Client.State}, Server={charlie.Client.IsConnectedToServer}");
                    break;

                case "peers":
                    Console.WriteLine($"Alice peers: {alice.Client.Peers.ConnectedCount}");
                    Console.WriteLine($"Bob peers: {bob.Client.Peers.ConnectedCount}");
                    Console.WriteLine($"Charlie peers: {charlie.Client.Peers.ConnectedCount}");
                    break;

                case "send":
                    Console.WriteLine("Sending test message from Alice to Bob...");
                    var testSent = await alice.Client.SendMessageAsync(
                        bob.Client.Identity.ClientId,
                        Encoding.UTF8.GetBytes($"Test message at {DateTime.Now:HH:mm:ss}"),
                        cancellationToken);
                    Console.WriteLine(testSent ? "Sent!" : "Failed!");
                    await Task.Delay(500, cancellationToken);
                    if (bob.Messages.Count > 0)
                    {
                        var last = bob.Messages[^1];
                        Console.WriteLine($"Bob received: \"{last.Text}\"");
                    }
                    break;

                default:
                    Console.WriteLine("Unknown command. Use: status, peers, send, quit");
                    break;
            }
        }

        // Cleanup
        Console.WriteLine("\nShutting down...");
        await alice.Client.StopAsync();
        await bob.Client.StopAsync();
        await charlie.Client.StopAsync();
        await server.StopAsync();
        Console.WriteLine("Done.");
    }

    private static async Task<DemoClient> CreateClientAsync(
        string name,
        IPEndPoint serverEndpoint,
        int listenPort,
        bool canRelay,
        CancellationToken cancellationToken)
    {
        var config = new NetworkConfig
        {
            ServerEndPoint = serverEndpoint,
            ListenPort = listenPort,
            CanRelay = canRelay,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            HeartbeatInterval = TimeSpan.FromSeconds(30)
        };

        var client = new MeshClient(config);
        var messages = new List<ReceivedMessage>();

        client.MessageReceived += async (sourceId, data) =>
        {
            var text = Encoding.UTF8.GetString(data);
            messages.Add(new ReceivedMessage(sourceId, text, DateTime.UtcNow));
            Console.WriteLine($"      [{name}] Received: \"{text}\"");
            await Task.CompletedTask;
        };

        return new DemoClient(name, client, messages);
    }

    private static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(Console.ReadLine, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

internal record DemoClient(string Name, MeshClient Client, List<ReceivedMessage> Messages);
internal record ReceivedMessage(string SourceId, string Text, DateTime ReceivedAt);
