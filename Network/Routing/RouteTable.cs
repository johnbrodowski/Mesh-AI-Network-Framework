using System.Collections.Concurrent;
using MeshAI.Core.Logging;
using MeshAI.Network.Protocol;

namespace MeshAI.Network.Routing;

/// <summary>
/// Type of route to a destination.
/// </summary>
public enum RouteType
{
    /// <summary>
    /// Direct peer-to-peer connection.
    /// </summary>
    Direct,

    /// <summary>
    /// Route through a relay node.
    /// </summary>
    Relay,

    /// <summary>
    /// Route through the server.
    /// </summary>
    ServerMediated
}

/// <summary>
/// Represents a route to a destination client.
/// </summary>
public sealed class Route
{
    public required string DestinationId { get; init; }
    public required RouteType Type { get; init; }

    /// <summary>
    /// Next hop client ID (null for direct or server-mediated routes).
    /// </summary>
    public string? NextHopId { get; init; }

    /// <summary>
    /// Full path of client IDs (for multi-hop routes).
    /// </summary>
    public List<string> Path { get; init; } = [];

    /// <summary>
    /// Estimated latency in milliseconds.
    /// </summary>
    public double EstimatedLatencyMs { get; set; }

    /// <summary>
    /// Route quality score (0-100).
    /// </summary>
    public int QualityScore { get; set; } = 50;

    /// <summary>
    /// When this route was last verified.
    /// </summary>
    public DateTime LastVerified { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this route expires.
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(5);

    /// <summary>
    /// Number of successful uses.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed uses.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Whether this route is still valid.
    /// </summary>
    public bool IsValid => DateTime.UtcNow < ExpiresAt && QualityScore > 0;

    /// <summary>
    /// Number of hops to destination.
    /// </summary>
    public int HopCount => Type switch
    {
        RouteType.Direct => 1,
        RouteType.Relay => Path.Count,
        RouteType.ServerMediated => 2,
        _ => 999
    };

    /// <summary>
    /// Records a successful use of this route.
    /// </summary>
    public void RecordSuccess(double latencyMs)
    {
        SuccessCount++;
        EstimatedLatencyMs = (EstimatedLatencyMs * 0.8) + (latencyMs * 0.2);
        QualityScore = Math.Min(100, QualityScore + 5);
        ExpiresAt = DateTime.UtcNow.AddMinutes(5);
    }

    /// <summary>
    /// Records a failed use of this route.
    /// </summary>
    public void RecordFailure()
    {
        FailureCount++;
        QualityScore = Math.Max(0, QualityScore - 20);
    }
}

/// <summary>
/// Manages routes to destinations with automatic expiration and quality tracking.
/// </summary>
public sealed class RouteTable
{
    private readonly ConcurrentDictionary<string, List<Route>> _routes = new();
    private readonly Logger _log = Logger.For<RouteTable>();

    /// <summary>
    /// Maximum routes to keep per destination.
    /// </summary>
    public int MaxRoutesPerDestination { get; init; } = 5;

    /// <summary>
    /// Route expiration time.
    /// </summary>
    public TimeSpan RouteExpiration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Adds or updates a route.
    /// </summary>
    public void AddRoute(Route route)
    {
        var routes = _routes.GetOrAdd(route.DestinationId, _ => []);

        lock (routes)
        {
            // Remove existing route of same type and path
            routes.RemoveAll(r =>
                r.Type == route.Type &&
                r.NextHopId == route.NextHopId);

            routes.Add(route);

            // Keep only best routes
            if (routes.Count > MaxRoutesPerDestination)
            {
                routes.Sort((a, b) => b.QualityScore.CompareTo(a.QualityScore));
                routes.RemoveRange(MaxRoutesPerDestination, routes.Count - MaxRoutesPerDestination);
            }
        }

        _log.Debug("Added {0} route to {1}", route.Type, route.DestinationId[..16]);
    }

    /// <summary>
    /// Gets the best route to a destination.
    /// </summary>
    public Route? GetBestRoute(string destinationId)
    {
        if (!_routes.TryGetValue(destinationId, out var routes))
            return null;

        lock (routes)
        {
            // Remove expired routes
            routes.RemoveAll(r => !r.IsValid);

            return routes
                .OrderByDescending(r => r.QualityScore)
                .ThenBy(r => r.HopCount)
                .ThenBy(r => r.EstimatedLatencyMs)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Gets all valid routes to a destination.
    /// </summary>
    public IEnumerable<Route> GetRoutes(string destinationId)
    {
        if (!_routes.TryGetValue(destinationId, out var routes))
            return [];

        lock (routes)
        {
            routes.RemoveAll(r => !r.IsValid);
            return routes.OrderByDescending(r => r.QualityScore).ToList();
        }
    }

    /// <summary>
    /// Checks if any route exists to a destination.
    /// </summary>
    public bool HasRoute(string destinationId)
    {
        return GetBestRoute(destinationId) != null;
    }

    /// <summary>
    /// Invalidates all routes to a destination.
    /// </summary>
    public void InvalidateRoutes(string destinationId)
    {
        if (_routes.TryGetValue(destinationId, out var routes))
        {
            lock (routes)
            {
                foreach (var route in routes)
                {
                    route.QualityScore = 0;
                }
            }
        }

        _log.Debug("Invalidated routes to {0}", destinationId[..16]);
    }

    /// <summary>
    /// Invalidates routes that use a specific next hop.
    /// </summary>
    public void InvalidateRoutesVia(string nextHopId)
    {
        foreach (var routes in _routes.Values)
        {
            lock (routes)
            {
                foreach (var route in routes.Where(r => r.NextHopId == nextHopId || r.Path.Contains(nextHopId)))
                {
                    route.QualityScore = 0;
                }
            }
        }

        _log.Debug("Invalidated routes via {0}", nextHopId[..16]);
    }

    /// <summary>
    /// Records a successful message delivery to update route quality.
    /// </summary>
    public void RecordSuccess(string destinationId, RouteType type, double latencyMs)
    {
        if (!_routes.TryGetValue(destinationId, out var routes))
            return;

        lock (routes)
        {
            var route = routes.FirstOrDefault(r => r.Type == type);
            route?.RecordSuccess(latencyMs);
        }
    }

    /// <summary>
    /// Records a failed message delivery to update route quality.
    /// </summary>
    public void RecordFailure(string destinationId, RouteType type)
    {
        if (!_routes.TryGetValue(destinationId, out var routes))
            return;

        lock (routes)
        {
            var route = routes.FirstOrDefault(r => r.Type == type);
            route?.RecordFailure();
        }
    }

    /// <summary>
    /// Removes expired routes from all destinations.
    /// </summary>
    public int CleanupExpired()
    {
        var removed = 0;

        foreach (var routes in _routes.Values)
        {
            lock (routes)
            {
                removed += routes.RemoveAll(r => !r.IsValid);
            }
        }

        if (removed > 0)
        {
            _log.Debug("Removed {0} expired routes", removed);
        }

        return removed;
    }

    /// <summary>
    /// Creates a direct route from peer info.
    /// </summary>
    public static Route CreateDirectRoute(PeerInfo peer)
    {
        return new Route
        {
            DestinationId = peer.ClientId,
            Type = RouteType.Direct,
            QualityScore = 80,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
    }

    /// <summary>
    /// Creates a relay route.
    /// </summary>
    public static Route CreateRelayRoute(string destinationId, PeerInfo relay)
    {
        return new Route
        {
            DestinationId = destinationId,
            Type = RouteType.Relay,
            NextHopId = relay.ClientId,
            Path = [relay.ClientId, destinationId],
            QualityScore = 60,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
    }

    /// <summary>
    /// Creates a server-mediated route.
    /// </summary>
    public static Route CreateServerRoute(string destinationId)
    {
        return new Route
        {
            DestinationId = destinationId,
            Type = RouteType.ServerMediated,
            QualityScore = 40,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };
    }
}
