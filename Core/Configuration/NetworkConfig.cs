using System.Net;

namespace MeshAI.Core.Configuration;

/// <summary>
/// Network configuration settings.
/// </summary>
public sealed class NetworkConfig
{
    /// <summary>
    /// Local port to listen on.
    /// </summary>
    public int ListenPort { get; init; } = 9500;

    /// <summary>
    /// Server address (for client mode).
    /// </summary>
    public IPEndPoint? ServerEndPoint { get; init; }

    /// <summary>
    /// Whether this node can act as a relay.
    /// </summary>
    public bool CanRelay { get; init; } = false;

    /// <summary>
    /// Maximum number of peer connections.
    /// </summary>
    public int MaxPeerConnections { get; init; } = 50;

    /// <summary>
    /// Connection timeout.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Heartbeat interval.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Heartbeat timeout before considering peer dead.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Interval for reconnection attempts.
    /// </summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum reconnection attempts.
    /// </summary>
    public int MaxReconnectAttempts { get; init; } = 5;

    /// <summary>
    /// Interval for peer list refresh.
    /// </summary>
    public TimeSpan PeerRefreshInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// File transfer chunk size.
    /// </summary>
    public int TransferChunkSize { get; init; } = 64 * 1024; // 64 KB

    /// <summary>
    /// Maximum concurrent transfers.
    /// </summary>
    public int MaxConcurrentTransfers { get; init; } = 5;

    /// <summary>
    /// Subnet mask for local network detection.
    /// </summary>
    public byte SubnetMask { get; init; } = 24;

    /// <summary>
    /// Optional client ID salt for additional uniqueness.
    /// </summary>
    public string? IdentitySalt { get; init; }

    /// <summary>
    /// Creates default client configuration.
    /// </summary>
    public static NetworkConfig CreateClientConfig(IPEndPoint serverEndPoint, bool canRelay = false)
    {
        return new NetworkConfig
        {
            ServerEndPoint = serverEndPoint,
            CanRelay = canRelay
        };
    }

    /// <summary>
    /// Creates default server configuration.
    /// </summary>
    public static NetworkConfig CreateServerConfig(int port = 9500)
    {
        return new NetworkConfig
        {
            ListenPort = port,
            CanRelay = true
        };
    }
}

/// <summary>
/// Logging configuration.
/// </summary>
public sealed class LogConfig
{
    /// <summary>
    /// Minimum log level.
    /// </summary>
    public LogLevel MinLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// Log file path (null for console only).
    /// </summary>
    public string? LogFilePath { get; init; }

    /// <summary>
    /// Whether to include timestamps.
    /// </summary>
    public bool IncludeTimestamps { get; init; } = true;

    /// <summary>
    /// Whether to include source context.
    /// </summary>
    public bool IncludeSource { get; init; } = true;
}

/// <summary>
/// Log levels.
/// </summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5
}
