using System.Net;
using MeshAI.Core.Configuration;
using MeshAI.Core.Crypto;
using MeshAI.Core.Identity;
using MeshAI.Core.Logging;
using MeshAI.Network.Protocol;
using MeshAI.Network.Transport;

namespace MeshAI.Client;

/// <summary>
/// Client states for the mesh network.
/// </summary>
public enum ClientState
{
    Disconnected,
    ConnectingToServer,
    Registering,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>
/// Mesh network client node.
/// Can connect to server, maintain peer connections, and act as relay.
/// </summary>
public sealed class MeshClient : IAsyncDisposable
{
    private readonly NetworkConfig _config;
    private readonly Logger _log = Logger.For<MeshClient>();

    private readonly ClientIdentity _identity;
    private readonly KeyExchange _keyExchange = new();
    private readonly PeerManager _peerManager = new();
    private readonly ConnectionListener _listener;

    private TcpConnection? _serverConnection;
    private readonly CancellationTokenSource _cts = new();

    private ClientState _state = ClientState.Disconnected;
    private int _reconnectAttempts;

    private Task? _heartbeatTask;
    private Task? _peerRefreshTask;
    private Task? _reachabilityTask;

    private List<PeerInfo> _knownPeers = [];
    private List<PeerInfo> _relayNodes = [];

    /// <summary>
    /// Client identity.
    /// </summary>
    public ClientIdentity Identity => _identity;

    /// <summary>
    /// Current client state.
    /// </summary>
    public ClientState State => _state;

    /// <summary>
    /// Whether connected to the server.
    /// </summary>
    public bool IsConnectedToServer => _serverConnection?.IsConnected == true;

    /// <summary>
    /// Peer manager for direct peer connections.
    /// </summary>
    public PeerManager Peers => _peerManager;

    /// <summary>
    /// Whether this client can act as a relay.
    /// </summary>
    public bool CanRelay => _config.CanRelay;

    /// <summary>
    /// Public IP as seen by the server.
    /// </summary>
    public IPAddress? PublicAddress { get; private set; }

    /// <summary>
    /// Event raised when state changes.
    /// </summary>
    public event Action<ClientState, ClientState>? StateChanged;

    /// <summary>
    /// Event raised when a direct message is received.
    /// </summary>
    public event Func<string, byte[], Task>? MessageReceived;

    /// <summary>
    /// Creates a new mesh client.
    /// </summary>
    public MeshClient(NetworkConfig config, string? identitySalt = null)
    {
        _config = config;
        _identity = ClientIdentity.Generate(identitySalt ?? config.IdentitySalt);
        _listener = new ConnectionListener(config.ListenPort);

        _listener.ConnectionAccepted += OnIncomingConnection;
        _peerManager.MessageReceived += OnPeerMessageAsync;
    }

    /// <summary>
    /// Starts the client and connects to the server.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _log.Info("Starting mesh client {0}", _identity.ShortId);

        // Start local listener
        _listener.Start();
        _log.Info("Listening on port {0}", _config.ListenPort);

        // Connect to server
        if (_config.ServerEndPoint != null)
        {
            await ConnectToServerAsync(cancellationToken);
        }
        else
        {
            _log.Info("No server configured, running in mesh-only mode");
            SetState(ClientState.Connected);
        }

        // Start background tasks
        _heartbeatTask = HeartbeatLoopAsync(_cts.Token);
        _peerRefreshTask = PeerRefreshLoopAsync(_cts.Token);
        _reachabilityTask = ReachabilityLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Stops the client.
    /// </summary>
    public async Task StopAsync()
    {
        _log.Info("Stopping mesh client...");

        await _cts.CancelAsync();

        // Deregister from server
        if (_serverConnection?.IsConnected == true)
        {
            try
            {
                var frame = MessageFrame.Create(MessageType.Deregister, []);
                await _serverConnection.SendFrameAsync(frame);
            }
            catch
            {
                // Ignore errors during shutdown
            }
        }

        await _listener.StopAsync();
        await _peerManager.DisposeAsync();

        if (_serverConnection != null)
        {
            await _serverConnection.DisposeAsync();
        }

        // Wait for background tasks
        try
        {
            if (_heartbeatTask != null)
                await _heartbeatTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (_peerRefreshTask != null)
                await _peerRefreshTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (_reachabilityTask != null)
                await _reachabilityTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Ignore
        }

        SetState(ClientState.Disconnected);
        _log.Info("Mesh client stopped");
    }

    private async Task ConnectToServerAsync(CancellationToken cancellationToken)
    {
        if (_config.ServerEndPoint == null)
            return;

        SetState(ClientState.ConnectingToServer);

        try
        {
            _serverConnection = new TcpConnection();
            await _serverConnection.ConnectAsync(_config.ServerEndPoint, _config.ConnectionTimeout, cancellationToken);

            _log.Info("Connected to server at {0}", _config.ServerEndPoint);

            // Perform key exchange
            await _serverConnection.PerformKeyExchangeAsync(_keyExchange.PublicKey, cancellationToken);

            var keyResponse = await _serverConnection.ReceiveAsync(cancellationToken);
            if (keyResponse?.Header.Type != MessageType.KeyExchangeResponse)
            {
                throw new InvalidOperationException("Invalid key exchange response from server");
            }

            _serverConnection.CompleteKeyExchange(_keyExchange, keyResponse.Payload);
            _log.Debug("Key exchange completed with server");

            // Register with server
            await RegisterWithServerAsync(cancellationToken);

            // Set up message handling
            _serverConnection.MessageReceived += OnServerMessageAsync;
            _serverConnection.Disconnected += OnServerDisconnected;
            _serverConnection.StartReceiving();

            SetState(ClientState.Connected);
            _reconnectAttempts = 0;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to connect to server");
            SetState(ClientState.Failed);
            throw;
        }
    }

    private async Task RegisterWithServerAsync(CancellationToken cancellationToken)
    {
        SetState(ClientState.Registering);

        var localAddress = GetLocalIPAddress();

        var registerMessage = new RegisterMessage
        {
            ClientId = _identity.ClientId,
            LocalAddress = localAddress,
            ListenPort = (ushort)_config.ListenPort,
            CanRelay = _config.CanRelay,
            PublicKey = _keyExchange.PublicKey,
            SubnetMask = _config.SubnetMask
        };

        var frame = MessageFrame.Create(registerMessage);
        await _serverConnection!.SendFrameAsync(frame, cancellationToken);

        // Wait for acknowledgment
        var response = await _serverConnection.ReceiveAsync(cancellationToken);
        if (response == null)
        {
            throw new InvalidOperationException("No response to registration");
        }

        var ack = RegisterAckMessage.FromBytes(response.Payload);
        if (!ack.Success)
        {
            throw new InvalidOperationException($"Registration failed: {ack.ErrorMessage}");
        }

        PublicAddress = ack.PublicAddress;
        _log.Info("Registered with server. Public IP: {0}", PublicAddress);
    }

    private async Task OnServerMessageAsync(TcpConnection connection, MessageFrame frame)
    {
        try
        {
            switch (frame.Header.Type)
            {
                case MessageType.PeerListResponse:
                    HandlePeerListResponse(frame);
                    break;

                case MessageType.RelayResponse:
                    HandleRelayResponse(frame);
                    break;

                case MessageType.RouteSuggestion:
                    await HandleRouteSuggestionAsync(frame);
                    break;

                case MessageType.RelayData:
                    await HandleRelayDataAsync(frame);
                    break;

                default:
                    _log.Debug("Received server message: {0}", frame.Header.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error handling server message {0}", frame.Header.Type);
        }
    }

    private void HandlePeerListResponse(MessageFrame frame)
    {
        var peerList = PeerListMessage.FromBytes(frame.Payload);
        _knownPeers = peerList.Peers;
        _log.Debug("Received {0} peers from server", _knownPeers.Count);
    }

    private void HandleRelayResponse(MessageFrame frame)
    {
        var relayList = PeerListMessage.FromBytes(frame.Payload);
        _relayNodes = relayList.Peers;
        _log.Debug("Received {0} relay candidates from server", _relayNodes.Count);
    }

    private async Task HandleRouteSuggestionAsync(MessageFrame frame)
    {
        var route = PeerListMessage.FromBytes(frame.Payload);
        _log.Debug("Received route suggestion with {0} hops", route.Peers.Count);

        // Attempt to connect to peers in the route
        foreach (var peer in route.Peers)
        {
            if (!_peerManager.IsConnected(peer.ClientId))
            {
                await _peerManager.ConnectAsync(peer, _keyExchange, _identity.ClientId,
                    _config.ConnectionTimeout);
            }
        }
    }

    private async Task HandleRelayDataAsync(MessageFrame frame)
    {
        var relayMessage = RelayDataMessage.FromBytes(frame.Payload);

        // If we're the target, decrypt and deliver
        if (relayMessage.TargetClientId == _identity.ClientId)
        {
            if (MessageReceived != null)
            {
                await MessageReceived.Invoke(relayMessage.SourceClientId, relayMessage.EncryptedPayload);
            }
        }
        // If we're a relay, forward the message
        else if (_config.CanRelay)
        {
            await RelayMessageAsync(relayMessage);
        }
    }

    private async Task RelayMessageAsync(RelayDataMessage message)
    {
        // Try to forward to target via direct peer connection
        if (_peerManager.IsConnected(message.TargetClientId))
        {
            var forwardMessage = new RelayDataMessage
            {
                SourceClientId = message.SourceClientId,
                TargetClientId = message.TargetClientId,
                HopCount = message.HopCount + 1,
                EncryptedPayload = message.EncryptedPayload
            };

            await _peerManager.SendAsync(message.TargetClientId, forwardMessage);
            _log.Debug("Relayed message to {0}", message.TargetClientId[..16]);
        }
        // Forward via server
        else if (_serverConnection?.IsConnected == true)
        {
            await _serverConnection.SendAsync(message);
        }
    }

    private void OnServerDisconnected(TcpConnection connection, Exception? error)
    {
        _log.Warn("Disconnected from server: {0}", error?.Message ?? "Connection closed");
        SetState(ClientState.Reconnecting);

        // Start reconnection in background
        _ = ReconnectLoopAsync(_cts.Token);
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _reconnectAttempts < _config.MaxReconnectAttempts)
        {
            _reconnectAttempts++;
            _log.Info("Reconnection attempt {0}/{1}", _reconnectAttempts, _config.MaxReconnectAttempts);

            try
            {
                await Task.Delay(_config.ReconnectInterval, cancellationToken);
                await ConnectToServerAsync(cancellationToken);
                return; // Success
            }
            catch (Exception ex)
            {
                _log.Warn("Reconnection failed: {0}", ex.Message);
            }
        }

        _log.Error("Max reconnection attempts reached, operating in mesh-only mode");
        SetState(ClientState.Connected); // Continue without server
    }

    private void OnIncomingConnection(TcpConnection connection)
    {
        _log.Debug("Incoming connection from {0}", connection.RemoteEndPoint);

        connection.MessageReceived += async (conn, frame) =>
        {
            await HandleIncomingPeerMessageAsync(conn, frame);
        };

        connection.StartReceiving();
    }

    private async Task HandleIncomingPeerMessageAsync(TcpConnection connection, MessageFrame frame)
    {
        // Handle key exchange
        if (frame.Header.Type == MessageType.KeyExchange)
        {
            connection.CompleteKeyExchange(_keyExchange, frame.Payload);
            var response = MessageFrame.Create(MessageType.KeyExchangeResponse, _keyExchange.PublicKey);
            await connection.SendFrameAsync(response);
            return;
        }

        // Handle peer connect
        if (frame.Header.Type == MessageType.DirectMessage || frame.Header.Type == MessageType.PeerConnect)
        {
            var message = DirectMessagePayload.FromBytes(frame.Payload);
            var peerInfo = new PeerInfo
            {
                ClientId = message.SourceClientId,
                LocalAddress = connection.RemoteEndPoint!.Address,
                ListenPort = (ushort)connection.RemoteEndPoint.Port,
                CanRelay = false,
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            connection.SetRemoteClientId(message.SourceClientId);
            _peerManager.AcceptPeer(connection, peerInfo);

            var ack = MessageFrame.Create(MessageType.PeerConnectAck, []);
            await connection.SendFrameAsync(ack);
        }
    }

    private async Task OnPeerMessageAsync(PeerConnection peer, MessageFrame frame)
    {
        try
        {
            switch (frame.Header.Type)
            {
                case MessageType.DirectMessage:
                    var message = DirectMessagePayload.FromBytes(frame.Payload);
                    if (MessageReceived != null)
                    {
                        await MessageReceived.Invoke(message.SourceClientId, message.Data);
                    }
                    break;

                case MessageType.RelayData:
                    await HandleRelayDataAsync(frame);
                    break;

                default:
                    _log.Debug("Received peer message: {0} from {1}", frame.Header.Type, peer.ClientId[..16]);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error handling peer message");
        }
    }

    /// <summary>
    /// Sends a direct message to another client.
    /// Automatically routes through relay or server if needed.
    /// </summary>
    public async Task<bool> SendMessageAsync(string targetClientId, byte[] data, CancellationToken cancellationToken = default)
    {
        // Try direct peer connection first
        if (_peerManager.IsConnected(targetClientId))
        {
            var message = new DirectMessagePayload
            {
                SourceClientId = _identity.ClientId,
                TargetClientId = targetClientId,
                Data = data
            };
            return await _peerManager.SendAsync(targetClientId, message);
        }

        // Try to find and connect to peer
        var peerInfo = _knownPeers.FirstOrDefault(p => p.ClientId == targetClientId);
        if (peerInfo != null)
        {
            var peer = await _peerManager.ConnectAsync(peerInfo, _keyExchange, _identity.ClientId,
                _config.ConnectionTimeout, cancellationToken);

            if (peer != null)
            {
                var message = new DirectMessagePayload
                {
                    SourceClientId = _identity.ClientId,
                    TargetClientId = targetClientId,
                    Data = data
                };
                return await _peerManager.SendAsync(targetClientId, message);
            }
        }

        // Fall back to relay or server
        return await SendViaRelayAsync(targetClientId, data, cancellationToken);
    }

    private async Task<bool> SendViaRelayAsync(string targetClientId, byte[] data, CancellationToken cancellationToken)
    {
        // Note: End-to-end encryption requires key exchange with target.
        // For server-mediated relay, transport-level encryption provides security.
        // Data is sent as-is; encrypted at transport layer.
        var relayMessage = new RelayDataMessage
        {
            SourceClientId = _identity.ClientId,
            TargetClientId = targetClientId,
            HopCount = 0,
            EncryptedPayload = data
        };

        // Try relay nodes
        foreach (var relay in _relayNodes.Where(r => _peerManager.IsConnected(r.ClientId)))
        {
            try
            {
                await _peerManager.SendAsync(relay.ClientId, relayMessage);
                _log.Debug("Sent message via relay {0}", relay.ClientId[..16]);
                return true;
            }
            catch
            {
                continue;
            }
        }

        // Fall back to server
        if (_serverConnection?.IsConnected == true)
        {
            await _serverConnection.SendAsync(relayMessage, cancellationToken: cancellationToken);
            _log.Debug("Sent message via server");
            return true;
        }

        _log.Warn("No route to {0}", targetClientId[..16]);
        return false;
    }

    /// <summary>
    /// Requests the peer list from the server.
    /// </summary>
    public async Task RefreshPeerListAsync(CancellationToken cancellationToken = default)
    {
        if (_serverConnection?.IsConnected != true)
            return;

        var frame = MessageFrame.Create(MessageType.PeerListRequest, []);
        await _serverConnection.SendFrameAsync(frame, cancellationToken);
    }

    /// <summary>
    /// Requests route to a specific client.
    /// </summary>
    public async Task RequestRouteAsync(string targetClientId, CancellationToken cancellationToken = default)
    {
        if (_serverConnection?.IsConnected != true)
            return;

        var payload = System.Text.Encoding.ASCII.GetBytes(targetClientId);
        var frame = MessageFrame.Create(MessageType.RouteRequest, payload);
        await _serverConnection.SendFrameAsync(frame, cancellationToken);
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.HeartbeatInterval, cancellationToken);

                // Heartbeat to server
                if (_serverConnection?.IsConnected == true)
                {
                    await _serverConnection.SendHeartbeatAsync(cancellationToken);
                }

                // Heartbeat to peers
                foreach (var peer in _peerManager.GetConnectedPeers())
                {
                    try
                    {
                        await peer.SendHeartbeatAsync();
                    }
                    catch
                    {
                        // Ignore individual peer failures
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PeerRefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.PeerRefreshInterval, cancellationToken);
                await RefreshPeerListAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReachabilityLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);

                if (_serverConnection?.IsConnected != true)
                    continue;

                // Report reachable peers to server
                var reachablePeers = _peerManager.GetReachablePeerIds().ToList();

                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);
                writer.Write((ushort)reachablePeers.Count);
                foreach (var peerId in reachablePeers)
                {
                    var bytes = System.Text.Encoding.ASCII.GetBytes(peerId);
                    writer.Write((byte)bytes.Length);
                    writer.Write(bytes);
                }

                var frame = MessageFrame.Create(MessageType.ReachabilityReport, ms.ToArray());
                await _serverConnection.SendFrameAsync(frame, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SetState(ClientState newState)
    {
        var oldState = _state;
        _state = newState;
        if (oldState != newState)
        {
            _log.Info("State: {0} -> {1}", oldState, newState);
            StateChanged?.Invoke(oldState, newState);
        }
    }

    private static IPAddress GetLocalIPAddress()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address ?? IPAddress.Loopback;
        }
        catch
        {
            return IPAddress.Loopback;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _keyExchange.Dispose();
        _cts.Dispose();
    }
}
