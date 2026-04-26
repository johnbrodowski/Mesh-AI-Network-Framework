using System.Collections.Concurrent;
using System.Net;
using MeshAI.Core.Configuration;
using MeshAI.Core.Crypto;
using MeshAI.Core.Identity;
using MeshAI.Core.Logging;
using MeshAI.Network.Protocol;
using MeshAI.Network.Transport;

namespace MeshAI.Server;

/// <summary>
/// Server node for the hybrid mesh network.
/// Acts as discovery authority, topology coordinator, and routing directory.
/// </summary>
public sealed class MeshServer : IAsyncDisposable
{
    private readonly NetworkConfig _config;
    private readonly Logger _log = Logger.For<MeshServer>();

    private readonly ClientRegistry _registry = new();
    private readonly ConnectionListener _listener;
    private readonly ConcurrentDictionary<string, TcpConnection> _connections = new();

    private readonly KeyExchange _keyExchange = new();
    private readonly ClientIdentity _identity = ClientIdentity.Generate("mesh-server");
    private static readonly TimeSpan IdentityProofMaxSkew = TimeSpan.FromSeconds(60);
    private readonly CancellationTokenSource _cts = new();

    private Task? _heartbeatTask;
    private Task? _cleanupTask;

    private bool _isRunning;

    /// <summary>
    /// Client registry for access by external components.
    /// </summary>
    public ClientRegistry Registry => _registry;

    /// <summary>
    /// Whether the server is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Event raised when the server starts.
    /// </summary>
    public event Action? Started;

    /// <summary>
    /// Event raised when the server stops.
    /// </summary>
    public event Action? Stopped;

    /// <summary>
    /// Creates a new mesh server.
    /// </summary>
    public MeshServer(NetworkConfig config)
    {
        _config = config;
        _listener = new ConnectionListener(config.ListenPort);

        _listener.ConnectionAccepted += OnConnectionAccepted;
        _listener.AcceptError += OnAcceptError;
    }

    /// <summary>
    /// Starts the server.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
            throw new InvalidOperationException("Server already running");

        _log.Info("Starting mesh server on port {0}", _config.ListenPort);

        _listener.Start();
        _isRunning = true;

        // Start background tasks
        _heartbeatTask = HeartbeatLoopAsync(_cts.Token);
        _cleanupTask = CleanupLoopAsync(_cts.Token);

        _log.Info("Mesh server started");
        Started?.Invoke();
    }

    /// <summary>
    /// Stops the server.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _log.Info("Stopping mesh server...");
        _isRunning = false;

        await _cts.CancelAsync();
        await _listener.StopAsync();

        // Close all connections
        foreach (var conn in _connections.Values)
        {
            await conn.CloseAsync();
        }
        _connections.Clear();

        // Wait for background tasks
        try
        {
            if (_heartbeatTask != null)
                await _heartbeatTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (_cleanupTask != null)
                await _cleanupTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Ignore
        }

        _log.Info("Mesh server stopped");
        Stopped?.Invoke();
    }

    private void OnConnectionAccepted(TcpConnection connection)
    {
        _log.Debug("New connection from {0}", connection.RemoteEndPoint);

        connection.MessageReceived += OnMessageReceivedAsync;
        connection.Disconnected += OnConnectionDisconnected;
        connection.StartReceiving();
    }

    private void OnAcceptError(Exception ex)
    {
        _log.Error(ex, "Error accepting connection");
    }

    private async Task OnMessageReceivedAsync(TcpConnection connection, MessageFrame frame)
    {
        try
        {
            await HandleMessageAsync(connection, frame);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error handling message {0}", frame.Header.Type);
        }
    }

    private async Task HandleMessageAsync(TcpConnection connection, MessageFrame frame)
    {
        switch (frame.Header.Type)
        {
            case MessageType.Register:
                await HandleRegisterAsync(connection, frame);
                break;

            case MessageType.Deregister:
                await HandleDeregisterAsync(connection, frame);
                break;

            case MessageType.KeyExchange:
                await HandleKeyExchangeAsync(connection, frame);
                break;

            case MessageType.IdentityProof:
                HandleIdentityProof(connection, frame);
                break;

            case MessageType.PeerListRequest:
                await HandlePeerListRequestAsync(connection, frame);
                break;

            case MessageType.RelayRequest:
                await HandleRelayRequestAsync(connection, frame);
                break;

            case MessageType.StatusUpdate:
                HandleStatusUpdate(connection, frame);
                break;

            case MessageType.ReachabilityReport:
                HandleReachabilityReport(connection, frame);
                break;

            case MessageType.RouteRequest:
                await HandleRouteRequestAsync(connection, frame);
                break;

            case MessageType.Ping:
                await HandlePingAsync(connection, frame);
                break;

            case MessageType.RelayData:
                await HandleRelayDataAsync(connection, frame);
                break;

            default:
                _log.Warn("Unhandled message type: {0}", frame.Header.Type);
                break;
        }
    }

    private async Task HandleRegisterAsync(TcpConnection connection, MessageFrame frame)
    {
        var message = RegisterMessage.FromBytes(frame.Payload);

        // The connection must have already proven ownership of this ClientId via IdentityProof.
        if (connection.RemoteClientId == null ||
            !string.Equals(connection.RemoteClientId, message.ClientId, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn("Register from unverified or mismatched client (claimed={0}, verified={1})",
                message.ClientId[..16], connection.RemoteClientId?[..16] ?? "<none>");
            await connection.CloseAsync();
            return;
        }

        var publicAddress = connection.RemoteEndPoint?.Address;
        var client = _registry.Register(message, publicAddress);

        // Store connection mapping
        _connections[message.ClientId] = connection;

        // Send acknowledgment
        var ack = new RegisterAckMessage
        {
            Success = true,
            PublicAddress = publicAddress,
            ServerPublicKey = _keyExchange.PublicKey
        };

        var response = MessageFrame.CreateResponse(frame.Header, ack);
        await connection.SendFrameAsync(response);

        _log.Info("Client {0} registered successfully", message.ClientId[..16]);
    }

    private async Task HandleDeregisterAsync(TcpConnection connection, MessageFrame frame)
    {
        if (connection.RemoteClientId != null)
        {
            _registry.Deregister(connection.RemoteClientId);
            _connections.TryRemove(connection.RemoteClientId, out _);
        }
        await connection.CloseAsync();
    }

    private async Task HandleKeyExchangeAsync(TcpConnection connection, MessageFrame frame)
    {
        // Client's public key is in the payload
        var clientPublicKey = frame.Payload;

        // Complete key exchange
        connection.CompleteKeyExchange(_keyExchange, clientPublicKey);

        // Send our public key back
        var response = MessageFrame.Create(MessageType.KeyExchangeResponse, _keyExchange.PublicKey);
        await connection.SendFrameAsync(response);

        // Immediately follow with our own identity proof, so the client can verify it
        // before trusting this connection.
        await connection.SendIdentityProofAsync(_identity);
    }

    private void HandleIdentityProof(TcpConnection connection, MessageFrame frame)
    {
        var proof = IdentityProofMessage.FromBytes(frame.Payload);
        connection.VerifyAndAcceptIdentityProof(proof, IdentityProofMaxSkew);
        _log.Debug("Verified identity proof from {0}", proof.ClientId[..16]);
    }

    private async Task HandlePeerListRequestAsync(TcpConnection connection, MessageFrame frame)
    {
        var requestingClient = connection.RemoteClientId;
        if (requestingClient == null)
        {
            _log.Warn("Peer list request from unregistered client");
            return;
        }

        var client = _registry.Get(requestingClient);
        if (client == null)
            return;

        // Get peers, prioritizing same subnet
        var sameSubnetPeers = _registry.FindSameSubnet(client).Take(10);
        var otherPeers = _registry.GetConnected()
            .Where(c => c.ClientId != requestingClient)
            .Except(sameSubnetPeers)
            .Take(20);

        var allPeers = sameSubnetPeers.Concat(otherPeers)
            .Select(c => c.ToPeerInfo())
            .ToList();

        var response = new PeerListMessage { Peers = allPeers };
        var responseFrame = MessageFrame.CreateResponse(frame.Header, response);
        await connection.SendFrameAsync(responseFrame);

        _log.Debug("Sent {0} peers to {1}", allPeers.Count, requestingClient[..16]);
    }

    private async Task HandleRelayRequestAsync(TcpConnection connection, MessageFrame frame)
    {
        var requestingClient = connection.RemoteClientId;
        if (requestingClient == null)
            return;

        // Parse target client ID from payload
        var targetClientId = System.Text.Encoding.ASCII.GetString(frame.Payload);

        // Find relay candidates
        var relays = _registry.FindRelaysForRoute(requestingClient, targetClientId)
            .Take(5)
            .Select(c => c.ToPeerInfo())
            .ToList();

        var response = new PeerListMessage { Peers = relays };
        var responseFrame = MessageFrame.Create(MessageType.RelayResponse, response.ToBytes());
        await connection.SendFrameAsync(responseFrame);

        _log.Debug("Sent {0} relay candidates for route {1} -> {2}",
            relays.Count, requestingClient[..16], targetClientId[..16]);
    }

    private void HandleStatusUpdate(TcpConnection connection, MessageFrame frame)
    {
        if (connection.RemoteClientId != null)
        {
            _registry.UpdateLastSeen(connection.RemoteClientId);
        }
    }

    private void HandleReachabilityReport(TcpConnection connection, MessageFrame frame)
    {
        if (connection.RemoteClientId == null)
            return;

        // Parse reachable peer IDs from payload
        var data = frame.Payload;
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data);
        var offset = 2;
        var reachablePeers = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var idLength = data[offset++];
            var id = System.Text.Encoding.ASCII.GetString(data.AsSpan(offset, idLength));
            offset += idLength;
            reachablePeers.Add(id);
        }

        _registry.UpdateReachability(connection.RemoteClientId, reachablePeers);
        _log.Debug("Updated reachability for {0}: {1} peers",
            connection.RemoteClientId[..16], reachablePeers.Count);
    }

    private async Task HandleRouteRequestAsync(TcpConnection connection, MessageFrame frame)
    {
        var requestingClient = connection.RemoteClientId;
        if (requestingClient == null)
            return;

        var targetClientId = System.Text.Encoding.ASCII.GetString(frame.Payload);
        var target = _registry.Get(targetClientId);

        if (target == null || !target.IsConnected)
        {
            var error = new ErrorMessage
            {
                OriginalMessageType = MessageType.RouteRequest,
                OriginalMessageId = frame.Header.MessageId,
                ErrorCode = "NOT_FOUND",
                ErrorDescription = "Target client not found or offline"
            };
            await connection.SendAsync(error);
            return;
        }

        // Build route suggestion
        var route = new List<PeerInfo>();

        // Check if direct route is possible
        var source = _registry.Get(requestingClient);
        if (source != null)
        {
            var sameSubnet = _registry.FindSameSubnet(source).Any(c => c.ClientId == targetClientId);
            if (sameSubnet)
            {
                // Direct route
                route.Add(target.ToPeerInfo());
            }
            else
            {
                // Find relay
                var relays = _registry.FindRelaysForRoute(requestingClient, targetClientId).Take(1);
                foreach (var relay in relays)
                {
                    route.Add(relay.ToPeerInfo());
                }
                route.Add(target.ToPeerInfo());
            }
        }

        var response = new PeerListMessage { Peers = route };
        var responseFrame = MessageFrame.Create(MessageType.RouteSuggestion, response.ToBytes());
        await connection.SendFrameAsync(responseFrame);
    }

    private async Task HandlePingAsync(TcpConnection connection, MessageFrame frame)
    {
        var ping = PingMessage.FromBytes(frame.Payload);
        var pong = new PongMessage
        {
            OriginalTimestamp = ping.Timestamp,
            Sequence = ping.Sequence,
            ResponseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var response = MessageFrame.CreateResponse(frame.Header, pong);
        await connection.SendFrameAsync(response);

        if (connection.RemoteClientId != null)
        {
            _registry.UpdateLastSeen(connection.RemoteClientId);
        }
    }

    private async Task HandleRelayDataAsync(TcpConnection connection, MessageFrame frame)
    {
        var relayMessage = RelayDataMessage.FromBytes(frame.Payload);

        // Find target connection
        if (_connections.TryGetValue(relayMessage.TargetClientId, out var targetConnection))
        {
            // Forward the message
            var forwardMessage = new RelayDataMessage
            {
                SourceClientId = relayMessage.SourceClientId,
                TargetClientId = relayMessage.TargetClientId,
                HopCount = relayMessage.HopCount + 1,
                EncryptedPayload = relayMessage.EncryptedPayload
            };

            await targetConnection.SendAsync(forwardMessage);
            _log.Debug("Relayed data from {0} to {1}",
                relayMessage.SourceClientId[..16], relayMessage.TargetClientId[..16]);
        }
        else
        {
            _log.Warn("Cannot relay to {0}: not connected", relayMessage.TargetClientId[..16]);
        }
    }

    private void OnConnectionDisconnected(TcpConnection connection, Exception? error)
    {
        if (connection.RemoteClientId != null)
        {
            _registry.MarkDisconnected(connection.RemoteClientId);
            _connections.TryRemove(connection.RemoteClientId, out _);

            if (error != null)
            {
                _log.Warn("Client {0} disconnected with error: {1}",
                    connection.RemoteClientId[..16], error.Message);
            }
            else
            {
                _log.Info("Client {0} disconnected", connection.RemoteClientId[..16]);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.HeartbeatInterval, cancellationToken);

                foreach (var connection in _connections.Values.ToList())
                {
                    try
                    {
                        if (connection.IsConnected)
                        {
                            await connection.SendHeartbeatAsync(cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Debug("Heartbeat failed for {0}: {1}",
                            connection.RemoteClientId?[..16] ?? "unknown", ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);

                // Remove stale clients
                var removed = _registry.RemoveStale(TimeSpan.FromMinutes(5));
                if (removed > 0)
                {
                    _log.Info("Removed {0} stale clients", removed);
                }

                // Check heartbeat timeouts
                foreach (var connection in _connections.Values.ToList())
                {
                    if (connection.CheckHeartbeatTimeout(_config.HeartbeatTimeout))
                    {
                        _log.Warn("Heartbeat timeout for {0}",
                            connection.RemoteClientId?[..16] ?? "unknown");
                        await connection.CloseAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _keyExchange.Dispose();
        _cts.Dispose();
    }
}
