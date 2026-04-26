using MeshAI.Client;
using MeshAI.Core.Crypto;
using MeshAI.Core.Logging;
using MeshAI.Network.Protocol;
using MeshAI.Network.Transport;

namespace MeshAI.Network.Routing;

/// <summary>
/// Result of a routing operation.
/// </summary>
public sealed class RoutingResult
{
    public bool Success { get; init; }
    public RouteType? RouteUsed { get; init; }
    public double LatencyMs { get; init; }
    public string? ErrorMessage { get; init; }

    public static RoutingResult Succeeded(RouteType route, double latencyMs) =>
        new() { Success = true, RouteUsed = route, LatencyMs = latencyMs };

    public static RoutingResult Failed(string error) =>
        new() { Success = false, ErrorMessage = error };
}

/// <summary>
/// Routes messages to destinations using the best available path.
/// Implements the three-tier routing: Direct -> Relay -> Server.
/// </summary>
public sealed class MessageRouter
{
    private readonly RouteTable _routeTable = new();
    private readonly Logger _log = Logger.For<MessageRouter>();

    private readonly string _localClientId;
    private readonly PeerManager _peerManager;
    private readonly TcpConnection? _serverConnection;
    private readonly Func<TcpConnection?> _serverConnectionProvider;

    /// <summary>
    /// Route table for inspection.
    /// </summary>
    public RouteTable RouteTable => _routeTable;

    public MessageRouter(
        string localClientId,
        PeerManager peerManager,
        Func<TcpConnection?> serverConnectionProvider)
    {
        _localClientId = localClientId;
        _peerManager = peerManager;
        _serverConnectionProvider = serverConnectionProvider;
    }

    /// <summary>
    /// Routes a message to a destination, trying paths in order of preference.
    /// </summary>
    public async Task<RoutingResult> RouteMessageAsync(
        string destinationId,
        byte[] data,
        bool requireEncryption = true,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;

        // 1. Try direct route first (same subnet or established connection)
        var directResult = await TryDirectRouteAsync(destinationId, data, requireEncryption, cancellationToken);
        if (directResult.Success)
        {
            _routeTable.RecordSuccess(destinationId, RouteType.Direct, directResult.LatencyMs);
            return directResult;
        }

        // 2. Try relay route
        var relayResult = await TryRelayRouteAsync(destinationId, data, cancellationToken);
        if (relayResult.Success)
        {
            _routeTable.RecordSuccess(destinationId, RouteType.Relay, relayResult.LatencyMs);
            return relayResult;
        }

        // 3. Fall back to server-mediated route
        var serverResult = await TryServerRouteAsync(destinationId, data, cancellationToken);
        if (serverResult.Success)
        {
            _routeTable.RecordSuccess(destinationId, RouteType.ServerMediated, serverResult.LatencyMs);
            return serverResult;
        }

        // All routes failed
        _log.Warn("All routes to {0} failed", destinationId[..16]);
        return RoutingResult.Failed("No available route to destination");
    }

    private async Task<RoutingResult> TryDirectRouteAsync(
        string destinationId,
        byte[] data,
        bool encrypt,
        CancellationToken cancellationToken)
    {
        // Check if we have a direct peer connection
        if (!_peerManager.IsConnected(destinationId))
        {
            return RoutingResult.Failed("No direct connection");
        }

        var startTime = DateTime.UtcNow;

        try
        {
            var message = new DirectMessagePayload
            {
                SourceClientId = _localClientId,
                TargetClientId = destinationId,
                Data = data
            };

            var success = await _peerManager.SendAsync(destinationId, message, encrypt);
            if (!success)
            {
                _routeTable.RecordFailure(destinationId, RouteType.Direct);
                return RoutingResult.Failed("Failed to send via direct connection");
            }

            var latency = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _log.Debug("Sent to {0} via direct route ({1:F1}ms)", destinationId[..16], latency);
            return RoutingResult.Succeeded(RouteType.Direct, latency);
        }
        catch (Exception ex)
        {
            _routeTable.RecordFailure(destinationId, RouteType.Direct);
            return RoutingResult.Failed($"Direct route error: {ex.Message}");
        }
    }

    private async Task<RoutingResult> TryRelayRouteAsync(
        string destinationId,
        byte[] data,
        CancellationToken cancellationToken)
    {
        // Find a relay that can reach the destination
        var route = _routeTable.GetRoutes(destinationId)
            .FirstOrDefault(r => r.Type == RouteType.Relay);

        if (route == null || route.NextHopId == null)
        {
            return RoutingResult.Failed("No relay route available");
        }

        // Check if we're connected to the relay
        if (!_peerManager.IsConnected(route.NextHopId))
        {
            return RoutingResult.Failed("Not connected to relay");
        }

        var startTime = DateTime.UtcNow;

        try
        {
            var relayMessage = new RelayDataMessage
            {
                SourceClientId = _localClientId,
                TargetClientId = destinationId,
                HopCount = 0,
                EncryptedPayload = data
            };

            var success = await _peerManager.SendAsync(route.NextHopId, relayMessage);
            if (!success)
            {
                _routeTable.RecordFailure(destinationId, RouteType.Relay);
                return RoutingResult.Failed("Failed to send to relay");
            }

            var latency = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _log.Debug("Sent to {0} via relay {1} ({2:F1}ms)",
                destinationId[..16], route.NextHopId[..16], latency);
            return RoutingResult.Succeeded(RouteType.Relay, latency);
        }
        catch (Exception ex)
        {
            _routeTable.RecordFailure(destinationId, RouteType.Relay);
            return RoutingResult.Failed($"Relay route error: {ex.Message}");
        }
    }

    private async Task<RoutingResult> TryServerRouteAsync(
        string destinationId,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var serverConnection = _serverConnectionProvider();
        if (serverConnection?.IsConnected != true)
        {
            return RoutingResult.Failed("Not connected to server");
        }

        var startTime = DateTime.UtcNow;

        try
        {
            // Encrypt data for end-to-end security
            var encryptedData = PayloadEncryption.Encrypt(data, PayloadEncryption.GenerateKey());

            var relayMessage = new RelayDataMessage
            {
                SourceClientId = _localClientId,
                TargetClientId = destinationId,
                HopCount = 0,
                EncryptedPayload = encryptedData
            };

            await serverConnection.SendAsync(relayMessage, cancellationToken: cancellationToken);

            var latency = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _log.Debug("Sent to {0} via server ({1:F1}ms)", destinationId[..16], latency);
            return RoutingResult.Succeeded(RouteType.ServerMediated, latency);
        }
        catch (Exception ex)
        {
            _routeTable.RecordFailure(destinationId, RouteType.ServerMediated);
            return RoutingResult.Failed($"Server route error: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the route table with peer information.
    /// </summary>
    public void UpdateRoutes(IEnumerable<PeerInfo> peers)
    {
        foreach (var peer in peers)
        {
            // Add direct routes for all known peers
            var route = RouteTable.CreateDirectRoute(peer);
            _routeTable.AddRoute(route);
        }
    }

    /// <summary>
    /// Adds a relay route suggestion.
    /// </summary>
    public void AddRelayRoute(string destinationId, PeerInfo relay)
    {
        var route = RouteTable.CreateRelayRoute(destinationId, relay);
        _routeTable.AddRoute(route);
    }

    /// <summary>
    /// Ensures a server-mediated route exists for a destination.
    /// </summary>
    public void EnsureServerRoute(string destinationId)
    {
        if (!_routeTable.HasRoute(destinationId))
        {
            var route = RouteTable.CreateServerRoute(destinationId);
            _routeTable.AddRoute(route);
        }
    }

    /// <summary>
    /// Handles a peer disconnection by invalidating affected routes.
    /// </summary>
    public void OnPeerDisconnected(string peerId)
    {
        _routeTable.InvalidateRoutes(peerId);
        _routeTable.InvalidateRoutesVia(peerId);
    }

    /// <summary>
    /// Performs periodic route maintenance.
    /// </summary>
    public void Maintain()
    {
        _routeTable.CleanupExpired();
    }
}
