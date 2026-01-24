using System.Net;
using MeshAI.Client;
using MeshAI.Core.Configuration;
using MeshAI.Core.Identity;
using MeshAI.Core.Logging;
using MeshAI.Server;
using MeshAI.Transfer;

namespace MeshAI;

/// <summary>
/// MeshAI - Hybrid Mesh-Server File & Message Network
///
/// A decentralized-friendly networking system for file and message exchange.
/// Supports direct peer-to-peer, relay, and server-mediated routing.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Configure logging
        Logger.Configure(new LogConfig
        {
            MinLevel = LogLevel.Information,
            IncludeTimestamps = true,
            IncludeSource = true
        });

        var log = Logger.For(typeof(Program));

        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            var command = args[0].ToLowerInvariant();

            return command switch
            {
                "server" => await RunServerAsync(args.Skip(1).ToArray()),
                "client" => await RunClientAsync(args.Skip(1).ToArray()),
                "identity" => RunIdentity(),
                "help" or "--help" or "-h" => PrintUsage(),
                _ => PrintUnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            log.Critical("Fatal error: {0}", ex.Message);
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(@"
MeshAI - Hybrid Mesh-Server File & Message Network

USAGE:
    meshai <command> [options]

COMMANDS:
    server      Start a mesh network server
    client      Start a mesh network client
    identity    Display hardware identity information
    help        Show this help message

SERVER OPTIONS:
    --port <port>       Port to listen on (default: 9500)
    --verbose           Enable verbose logging

CLIENT OPTIONS:
    --server <host:port>    Server address to connect to
    --port <port>           Local port to listen on (default: 9500)
    --relay                 Enable relay capability
    --download <dir>        Download directory (default: ./downloads)
    --verbose               Enable verbose logging

EXAMPLES:
    meshai server --port 9500
    meshai client --server 192.168.1.100:9500 --relay
    meshai identity

ARCHITECTURE:
    This is a hybrid server + mesh topology network:
    - Star topology to the server (when reachable)
    - Series/mesh topology between clients (when server is unreachable)

    Routing priority:
    1. Direct peer-to-peer (same subnet or NAT-friendly)
    2. Relay through another client
    3. Server-mediated (last resort)

    All payloads are encrypted end-to-end. The server only handles
    routing metadata, never inspecting payload contents.
");
        return 0;
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.WriteLine($"Unknown command: {command}");
        Console.WriteLine("Use 'meshai help' for usage information.");
        return 1;
    }

    private static int RunIdentity()
    {
        var identity = ClientIdentity.Generate();

        Console.WriteLine();
        Console.WriteLine("Hardware Identity Information");
        Console.WriteLine("=============================");
        Console.WriteLine($"Client ID:       {identity.ClientId}");
        Console.WriteLine($"Short ID:        {identity.ShortId}");
        Console.WriteLine($"Hardware Hash:   {identity.HardwareHash}");
        Console.WriteLine($"Generated At:    {identity.GeneratedAt:u}");
        Console.WriteLine();
        Console.WriteLine("This identity is derived from your hardware and is deterministic.");
        Console.WriteLine("The same hardware will always produce the same Client ID.");
        Console.WriteLine();

        return 0;
    }

    private static async Task<int> RunServerAsync(string[] args)
    {
        var port = 9500;
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i]);
                    break;
                case "--verbose":
                    verbose = true;
                    break;
            }
        }

        if (verbose)
        {
            Logger.Configure(new LogConfig { MinLevel = LogLevel.Debug });
        }

        var log = Logger.For(typeof(Program));
        log.Info("Starting MeshAI Server");

        var config = NetworkConfig.CreateServerConfig(port);

        await using var server = new MeshServer(config);

        // Handle Ctrl+C
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        server.Start();

        Console.WriteLine();
        Console.WriteLine($"MeshAI Server running on port {port}");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        // Interactive status loop
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                if (!cts.Token.IsCancellationRequested)
                {
                    log.Info("Status: {0} clients connected, {1} relays available",
                        server.Registry.ConnectedCount, server.Registry.RelayCount);
                }
            }
        }, cts.Token);

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        log.Info("Shutting down server...");
        await server.StopAsync();
        log.Info("Server stopped.");

        return 0;
    }

    private static async Task<int> RunClientAsync(string[] args)
    {
        IPEndPoint? serverEndpoint = null;
        var port = 9500;
        var canRelay = false;
        var downloadDir = "./downloads";
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server" when i + 1 < args.Length:
                    var parts = args[++i].Split(':');
                    var host = IPAddress.Parse(parts[0]);
                    var serverPort = parts.Length > 1 ? int.Parse(parts[1]) : 9500;
                    serverEndpoint = new IPEndPoint(host, serverPort);
                    break;
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i]);
                    break;
                case "--relay":
                    canRelay = true;
                    break;
                case "--download" when i + 1 < args.Length:
                    downloadDir = args[++i];
                    break;
                case "--verbose":
                    verbose = true;
                    break;
            }
        }

        if (verbose)
        {
            Logger.Configure(new LogConfig { MinLevel = LogLevel.Debug });
        }

        var log = Logger.For(typeof(Program));
        log.Info("Starting MeshAI Client");

        var config = new NetworkConfig
        {
            ServerEndPoint = serverEndpoint,
            ListenPort = port,
            CanRelay = canRelay
        };

        await using var client = new MeshClient(config);

        // Set up message handler
        client.MessageReceived += async (sourceId, data) =>
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
            log.Info("Message from {0}: {1}", sourceId[..16], text);
            await Task.CompletedTask;
        };

        // Set up transfer manager
        var transferManager = new TransferManager(client.Identity, downloadDir);
        transferManager.TransferRequested += async transfer =>
        {
            log.Info("Incoming transfer: {0} ({1:N0} bytes) from {2}",
                transfer.FileName, transfer.FileSize, transfer.RemoteClientId[..16]);
            // Auto-accept for demo
            return await Task.FromResult(true);
        };
        transferManager.TransferCompleted += transfer =>
        {
            log.Info("Transfer completed: {0}", transfer.FileName);
        };

        // Handle Ctrl+C
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await client.StartAsync(cts.Token);

        Console.WriteLine();
        Console.WriteLine($"MeshAI Client running");
        Console.WriteLine($"  Client ID: {client.Identity.ShortId}");
        Console.WriteLine($"  Listening on port: {port}");
        Console.WriteLine($"  Relay enabled: {canRelay}");
        if (serverEndpoint != null)
            Console.WriteLine($"  Server: {serverEndpoint}");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  peers           - List connected peers");
        Console.WriteLine("  send <id> <msg> - Send message to peer");
        Console.WriteLine("  status          - Show connection status");
        Console.WriteLine("  quit            - Exit client");
        Console.WriteLine();

        // Interactive command loop
        var inputTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                Console.Write("> ");
                var line = await Task.Run(Console.ReadLine, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                if (line == null || cts.Token.IsCancellationRequested)
                    break;

                var cmdParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cmdParts.Length == 0)
                    continue;

                switch (cmdParts[0].ToLowerInvariant())
                {
                    case "peers":
                        var peers = client.Peers.GetConnectedPeers().ToList();
                        if (peers.Count == 0)
                        {
                            Console.WriteLine("No connected peers.");
                        }
                        else
                        {
                            Console.WriteLine($"Connected peers ({peers.Count}):");
                            foreach (var peer in peers)
                            {
                                Console.WriteLine($"  {peer.ClientId[..16]} - {peer.LatencyMs:F1}ms - Quality: {peer.QualityScore}");
                            }
                        }
                        break;

                    case "send" when cmdParts.Length >= 3:
                        var targetId = cmdParts[1];
                        var message = string.Join(' ', cmdParts.Skip(2));
                        var data = System.Text.Encoding.UTF8.GetBytes(message);
                        var sent = await client.SendMessageAsync(targetId, data, cts.Token);
                        Console.WriteLine(sent ? "Message sent." : "Failed to send message.");
                        break;

                    case "status":
                        Console.WriteLine($"Client ID:   {client.Identity.ClientId}");
                        Console.WriteLine($"State:       {client.State}");
                        Console.WriteLine($"Server:      {(client.IsConnectedToServer ? "Connected" : "Disconnected")}");
                        Console.WriteLine($"Public IP:   {client.PublicAddress?.ToString() ?? "Unknown"}");
                        Console.WriteLine($"Peers:       {client.Peers.ConnectedCount}");
                        break;

                    case "quit" or "exit":
                        cts.Cancel();
                        break;

                    default:
                        Console.WriteLine("Unknown command. Type 'help' for available commands.");
                        break;
                }
            }
        }, cts.Token);

        try
        {
            await inputTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        log.Info("Shutting down client...");
        await client.StopAsync();
        log.Info("Client stopped.");

        return 0;
    }
}
