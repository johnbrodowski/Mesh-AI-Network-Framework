using System.Collections.Concurrent;
using System.Net;
using MeshAI.Core.Crypto;
using MeshAI.Core.Logging;
using MeshAI.Network.Protocol;
using MeshAI.Network.Transport;

namespace MeshAI.Client;

/// <summary>
/// Manages peer connections for a mesh client.
/// </summary>
public sealed class PeerManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PeerConnection> _peers = new();
    private readonly Logger _log = Logger.For<PeerManager>();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>
    /// Maximum number of peer connections.
    /// </summary>
    public int MaxPeers { get; init; } = 50;

    /// <summary>
    /// Number of currently connected peers.
    /// </summary>
    public int ConnectedCount => _peers.Values.Count(p => p.IsConnected);

    /// <summary>
    /// Event raised when a peer connects.
    /// </summary>
    public event Action<PeerConnection>? PeerConnected;

    /// <summary>
    /// Event raised when a peer disconnects.
    /// </summary>
    public event Action<PeerConnection>? PeerDisconnected;

    /// <summary>
    /// Event raised when a message is received from a peer.
    /// </summary>
    public event Func<PeerConnection, MessageFrame, Task>? MessageReceived;

    /// <summary>
    /// Gets a peer by client ID.
    /// </summary>
    public PeerConnection? GetPeer(string clientId)
    {
        _peers.TryGetValue(clientId, out var peer);
        return peer;
    }

    /// <summary>
    /// Gets all connected peers.
    /// </summary>
    public IEnumerable<PeerConnection> GetConnectedPeers() =>
        _peers.Values.Where(p => p.IsConnected);

    /// <summary>
    /// Checks if a peer is connected.
    /// </summary>
    public bool IsConnected(string clientId) =>
        _peers.TryGetValue(clientId, out var peer) && peer.IsConnected;

    /// <summary>
    /// Connects to a peer.
    /// </summary>
    public async Task<PeerConnection?> ConnectAsync(
        PeerInfo peerInfo,
        KeyExchange localKeyExchange,
        string localClientId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // Check if already connected
        if (_peers.TryGetValue(peerInfo.ClientId, out var existing) && existing.IsConnected)
        {
            return existing;
        }

        // Check capacity
        if (_peers.Count >= MaxPeers)
        {
            _log.Warn("Max peer connections reached ({0})", MaxPeers);
            return null;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after lock
            if (_peers.TryGetValue(peerInfo.ClientId, out existing) && existing.IsConnected)
            {
                return existing;
            }

            // Try to connect
            var endpoint = new IPEndPoint(
                peerInfo.PublicAddress ?? peerInfo.LocalAddress,
                peerInfo.ListenPort);

            var connection = new TcpConnection();

            try
            {
                await connection.ConnectAsync(endpoint, timeout, cancellationToken);

                // Perform key exchange
                await connection.PerformKeyExchangeAsync(localKeyExchange.PublicKey, cancellationToken);

                // Wait for key exchange response
                var response = await connection.ReceiveAsync(cancellationToken);
                if (response == null || response.Header.Type != MessageType.KeyExchangeResponse)
                {
                    throw new InvalidOperationException("Invalid key exchange response");
                }

                connection.CompleteKeyExchange(localKeyExchange, response.Payload);
                connection.SetRemoteClientId(peerInfo.ClientId);

                // Send peer connect
                var connectMessage = new DirectMessagePayload
                {
                    SourceClientId = localClientId,
                    TargetClientId = peerInfo.ClientId,
                    Data = []
                };
                await connection.SendAsync(connectMessage);

                // Create peer connection wrapper
                var peer = new PeerConnection(peerInfo, connection);

                connection.MessageReceived += async (conn, frame) =>
                {
                    if (MessageReceived != null)
                        await MessageReceived.Invoke(peer, frame);
                };

                connection.Disconnected += (conn, error) =>
                {
                    peer.MarkDisconnected();
                    PeerDisconnected?.Invoke(peer);
                };

                connection.StartReceiving();

                _peers[peerInfo.ClientId] = peer;
                _log.Info("Connected to peer {0} at {1}", peerInfo.ClientId[..16], endpoint);
                PeerConnected?.Invoke(peer);

                return peer;
            }
            catch (Exception ex)
            {
                _log.Warn("Failed to connect to peer {0}: {1}", peerInfo.ClientId[..16], ex.Message);
                await connection.DisposeAsync();
                return null;
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Accepts an incoming peer connection.
    /// </summary>
    public PeerConnection AcceptPeer(TcpConnection connection, PeerInfo peerInfo)
    {
        var peer = new PeerConnection(peerInfo, connection);

        connection.MessageReceived += async (conn, frame) =>
        {
            if (MessageReceived != null)
                await MessageReceived.Invoke(peer, frame);
        };

        connection.Disconnected += (conn, error) =>
        {
            peer.MarkDisconnected();
            _peers.TryRemove(peerInfo.ClientId, out _);
            PeerDisconnected?.Invoke(peer);
        };

        _peers[peerInfo.ClientId] = peer;
        _log.Info("Accepted peer connection from {0}", peerInfo.ClientId[..16]);
        PeerConnected?.Invoke(peer);

        return peer;
    }

    /// <summary>
    /// Disconnects from a peer.
    /// </summary>
    public async Task DisconnectAsync(string clientId)
    {
        if (_peers.TryRemove(clientId, out var peer))
        {
            await peer.DisconnectAsync();
            _log.Info("Disconnected from peer {0}", clientId[..16]);
        }
    }

    /// <summary>
    /// Sends a message to a peer.
    /// </summary>
    public async Task<bool> SendAsync(string clientId, IMessagePayload message, bool encrypt = true)
    {
        if (!_peers.TryGetValue(clientId, out var peer) || !peer.IsConnected)
        {
            return false;
        }

        await peer.SendAsync(message, encrypt);
        return true;
    }

    /// <summary>
    /// Gets reachable peer IDs.
    /// </summary>
    public IEnumerable<string> GetReachablePeerIds() =>
        _peers.Values.Where(p => p.IsConnected).Select(p => p.ClientId);

    /// <summary>
    /// Updates peer health based on heartbeat responses.
    /// </summary>
    public void UpdatePeerHealth(string clientId, double rttMs)
    {
        if (_peers.TryGetValue(clientId, out var peer))
        {
            peer.UpdateLatency(rttMs);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var peer in _peers.Values)
        {
            await peer.DisconnectAsync();
        }
        _peers.Clear();
        _connectionLock.Dispose();
    }
}

/// <summary>
/// Represents a connection to a peer.
/// </summary>
public sealed class PeerConnection
{
    private readonly TcpConnection _connection;

    public PeerInfo Info { get; }
    public string ClientId => Info.ClientId;
    public bool IsConnected => _connection.IsConnected;
    public bool CanRelay => Info.CanRelay;

    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivity => _connection.LastActivity;
    public double LatencyMs { get; private set; }
    public int QualityScore { get; private set; } = 50;

    internal PeerConnection(PeerInfo info, TcpConnection connection)
    {
        Info = info;
        _connection = connection;
    }

    public async Task SendAsync(IMessagePayload message, bool encrypt = true)
    {
        await _connection.SendAsync(message, encrypt);
    }

    public async Task SendFrameAsync(MessageFrame frame)
    {
        await _connection.SendFrameAsync(frame);
    }

    public async Task SendHeartbeatAsync()
    {
        await _connection.SendHeartbeatAsync();
    }

    internal void UpdateLatency(double rttMs)
    {
        LatencyMs = rttMs;
        // Update quality score based on latency
        QualityScore = Math.Clamp((int)(100 - rttMs / 5), 0, 100);
    }

    internal void MarkDisconnected()
    {
        QualityScore = 0;
    }

    public async Task DisconnectAsync()
    {
        await _connection.CloseAsync();
    }
}
