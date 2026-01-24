using System.Collections.Concurrent;
using System.Net;
using MeshAI.Core.Logging;
using MeshAI.Network.Protocol;

namespace MeshAI.Server;

/// <summary>
/// Registered client information maintained by the server.
/// </summary>
public sealed class RegisteredClient
{
    public required string ClientId { get; init; }
    public required IPAddress LocalAddress { get; init; }
    public IPAddress? PublicAddress { get; set; }
    public required ushort ListenPort { get; init; }
    public required bool CanRelay { get; init; }
    public byte SubnetMask { get; init; } = 24;
    public byte[]? PublicKey { get; init; }

    public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Clients that this client can directly reach (reported by client).
    /// </summary>
    public HashSet<string> ReachablePeers { get; } = [];

    /// <summary>
    /// Current connection state as seen by server.
    /// </summary>
    public bool IsConnected { get; set; } = true;

    /// <summary>
    /// Connection quality metrics.
    /// </summary>
    public double AverageLatencyMs { get; set; }
    public int SuccessfulPings { get; set; }
    public int FailedPings { get; set; }

    /// <summary>
    /// Computed connection quality score (0-100).
    /// </summary>
    public int QualityScore => ComputeQualityScore();

    private int ComputeQualityScore()
    {
        if (!IsConnected) return 0;

        var latencyScore = Math.Max(0, 100 - (int)(AverageLatencyMs / 10));
        var reliabilityScore = SuccessfulPings + FailedPings > 0
            ? (SuccessfulPings * 100) / (SuccessfulPings + FailedPings)
            : 50;

        return (latencyScore + reliabilityScore) / 2;
    }

    /// <summary>
    /// Creates PeerInfo for distribution to other clients.
    /// </summary>
    public PeerInfo ToPeerInfo()
    {
        return new PeerInfo
        {
            ClientId = ClientId,
            LocalAddress = LocalAddress,
            PublicAddress = PublicAddress,
            ListenPort = ListenPort,
            CanRelay = CanRelay,
            LastSeen = new DateTimeOffset(LastSeen).ToUnixTimeMilliseconds(),
            SubnetMask = SubnetMask
        };
    }
}

/// <summary>
/// Manages the registry of connected clients.
/// </summary>
public sealed class ClientRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredClient> _clients = new();
    private readonly Logger _log = Logger.For<ClientRegistry>();

    /// <summary>
    /// Number of registered clients.
    /// </summary>
    public int Count => _clients.Count;

    /// <summary>
    /// Number of connected (online) clients.
    /// </summary>
    public int ConnectedCount => _clients.Values.Count(c => c.IsConnected);

    /// <summary>
    /// Number of relay-capable clients.
    /// </summary>
    public int RelayCount => _clients.Values.Count(c => c.IsConnected && c.CanRelay);

    /// <summary>
    /// Event raised when a client is registered.
    /// </summary>
    public event Action<RegisteredClient>? ClientRegistered;

    /// <summary>
    /// Event raised when a client is deregistered.
    /// </summary>
    public event Action<RegisteredClient>? ClientDeregistered;

    /// <summary>
    /// Event raised when a client goes offline.
    /// </summary>
    public event Action<RegisteredClient>? ClientDisconnected;

    /// <summary>
    /// Registers or updates a client.
    /// </summary>
    public RegisteredClient Register(RegisterMessage message, IPAddress? publicAddress)
    {
        var client = new RegisteredClient
        {
            ClientId = message.ClientId,
            LocalAddress = message.LocalAddress,
            PublicAddress = publicAddress,
            ListenPort = message.ListenPort,
            CanRelay = message.CanRelay,
            SubnetMask = message.SubnetMask,
            PublicKey = message.PublicKey
        };

        var isNew = !_clients.ContainsKey(message.ClientId);
        _clients[message.ClientId] = client;

        if (isNew)
        {
            _log.Info("Client registered: {0} from {1}:{2}", client.ClientId[..16], client.LocalAddress, client.ListenPort);
            ClientRegistered?.Invoke(client);
        }
        else
        {
            _log.Debug("Client re-registered: {0}", client.ClientId[..16]);
        }

        return client;
    }

    /// <summary>
    /// Deregisters a client.
    /// </summary>
    public bool Deregister(string clientId)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            _log.Info("Client deregistered: {0}", clientId[..16]);
            ClientDeregistered?.Invoke(client);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Marks a client as disconnected.
    /// </summary>
    public void MarkDisconnected(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            client.IsConnected = false;
            _log.Warn("Client disconnected: {0}", clientId[..16]);
            ClientDisconnected?.Invoke(client);
        }
    }

    /// <summary>
    /// Marks a client as connected.
    /// </summary>
    public void MarkConnected(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            client.IsConnected = true;
            client.LastSeen = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Updates the last seen timestamp for a client.
    /// </summary>
    public void UpdateLastSeen(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            client.LastSeen = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Gets a client by ID.
    /// </summary>
    public RegisteredClient? Get(string clientId)
    {
        _clients.TryGetValue(clientId, out var client);
        return client;
    }

    /// <summary>
    /// Gets all registered clients.
    /// </summary>
    public IEnumerable<RegisteredClient> GetAll() => _clients.Values;

    /// <summary>
    /// Gets all connected clients.
    /// </summary>
    public IEnumerable<RegisteredClient> GetConnected() => _clients.Values.Where(c => c.IsConnected);

    /// <summary>
    /// Gets relay-capable clients.
    /// </summary>
    public IEnumerable<RegisteredClient> GetRelays() => _clients.Values.Where(c => c.IsConnected && c.CanRelay);

    /// <summary>
    /// Updates reachability information from a client.
    /// </summary>
    public void UpdateReachability(string clientId, IEnumerable<string> reachablePeers)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            client.ReachablePeers.Clear();
            foreach (var peer in reachablePeers)
            {
                client.ReachablePeers.Add(peer);
            }
        }
    }

    /// <summary>
    /// Finds clients that are on the same subnet.
    /// </summary>
    public IEnumerable<RegisteredClient> FindSameSubnet(RegisteredClient client)
    {
        var subnetMask = CreateSubnetMask(client.SubnetMask);
        var clientSubnet = GetSubnet(client.LocalAddress, subnetMask);

        return _clients.Values
            .Where(c => c.ClientId != client.ClientId && c.IsConnected)
            .Where(c =>
            {
                var peerSubnet = GetSubnet(c.LocalAddress, subnetMask);
                return clientSubnet.SequenceEqual(peerSubnet);
            });
    }

    /// <summary>
    /// Finds the best relay candidates for a client to reach a target.
    /// </summary>
    public IEnumerable<RegisteredClient> FindRelaysForRoute(string sourceId, string targetId)
    {
        return _clients.Values
            .Where(c => c.ClientId != sourceId && c.ClientId != targetId)
            .Where(c => c.IsConnected && c.CanRelay)
            .Where(c => c.ReachablePeers.Contains(targetId))
            .OrderByDescending(c => c.QualityScore);
    }

    /// <summary>
    /// Removes stale clients that haven't been seen recently.
    /// </summary>
    public int RemoveStale(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;
        var stale = _clients.Values
            .Where(c => c.LastSeen < cutoff)
            .Select(c => c.ClientId)
            .ToList();

        foreach (var id in stale)
        {
            if (_clients.TryRemove(id, out var client))
            {
                _log.Warn("Removed stale client: {0} (last seen {1})", id[..16], client.LastSeen);
                ClientDeregistered?.Invoke(client);
            }
        }

        return stale.Count;
    }

    private static byte[] CreateSubnetMask(int prefixLength)
    {
        var mask = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (prefixLength >= 8)
            {
                mask[i] = 0xFF;
                prefixLength -= 8;
            }
            else if (prefixLength > 0)
            {
                mask[i] = (byte)(0xFF << (8 - prefixLength));
                prefixLength = 0;
            }
        }
        return mask;
    }

    private static byte[] GetSubnet(IPAddress address, byte[] mask)
    {
        var addressBytes = address.GetAddressBytes();
        if (addressBytes.Length != 4)
            return addressBytes; // IPv6 not handled

        var subnet = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            subnet[i] = (byte)(addressBytes[i] & mask[i]);
        }
        return subnet;
    }
}
