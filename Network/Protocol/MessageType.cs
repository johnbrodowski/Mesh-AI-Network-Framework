namespace MeshAI.Network.Protocol;

/// <summary>
/// Defines all message types used in the mesh network protocol.
/// </summary>
public enum MessageType : byte
{
    // ============================================
    // Connection & Registration (0x00 - 0x0F)
    // ============================================

    /// <summary>
    /// Client registration request to server.
    /// </summary>
    Register = 0x01,

    /// <summary>
    /// Server acknowledgment of registration.
    /// </summary>
    RegisterAck = 0x02,

    /// <summary>
    /// Client deregistration (graceful disconnect).
    /// </summary>
    Deregister = 0x03,

    /// <summary>
    /// Key exchange initiation.
    /// </summary>
    KeyExchange = 0x04,

    /// <summary>
    /// Key exchange response.
    /// </summary>
    KeyExchangeResponse = 0x05,

    // ============================================
    // Discovery (0x10 - 0x1F)
    // ============================================

    /// <summary>
    /// Request peer list from server.
    /// </summary>
    PeerListRequest = 0x10,

    /// <summary>
    /// Peer list response from server.
    /// </summary>
    PeerListResponse = 0x11,

    /// <summary>
    /// Request relay candidates from server.
    /// </summary>
    RelayRequest = 0x12,

    /// <summary>
    /// Relay candidates response from server.
    /// </summary>
    RelayResponse = 0x13,

    /// <summary>
    /// Route suggestion from server.
    /// </summary>
    RouteSuggestion = 0x14,

    // ============================================
    // Heartbeat & Health (0x20 - 0x2F)
    // ============================================

    /// <summary>
    /// Heartbeat ping.
    /// </summary>
    Ping = 0x20,

    /// <summary>
    /// Heartbeat pong.
    /// </summary>
    Pong = 0x21,

    /// <summary>
    /// Client status update to server.
    /// </summary>
    StatusUpdate = 0x22,

    /// <summary>
    /// Reachability report (which peers this client can reach).
    /// </summary>
    ReachabilityReport = 0x23,

    // ============================================
    // Routing (0x30 - 0x3F)
    // ============================================

    /// <summary>
    /// Request route to a specific client.
    /// </summary>
    RouteRequest = 0x30,

    /// <summary>
    /// Route response with path information.
    /// </summary>
    RouteResponse = 0x31,

    /// <summary>
    /// Notification that a route is no longer valid.
    /// </summary>
    RouteInvalidation = 0x32,

    /// <summary>
    /// Request to establish relay connection.
    /// </summary>
    RelayConnect = 0x33,

    /// <summary>
    /// Acknowledgment of relay connection.
    /// </summary>
    RelayConnectAck = 0x34,

    /// <summary>
    /// Relay disconnect notification.
    /// </summary>
    RelayDisconnect = 0x35,

    // ============================================
    // Peer-to-Peer (0x40 - 0x4F)
    // ============================================

    /// <summary>
    /// Direct connection request to peer.
    /// </summary>
    PeerConnect = 0x40,

    /// <summary>
    /// Direct connection acknowledgment.
    /// </summary>
    PeerConnectAck = 0x41,

    /// <summary>
    /// Peer disconnect notification.
    /// </summary>
    PeerDisconnect = 0x42,

    /// <summary>
    /// Direct message to peer.
    /// </summary>
    DirectMessage = 0x43,

    /// <summary>
    /// Direct message acknowledgment.
    /// </summary>
    DirectMessageAck = 0x44,

    // ============================================
    // Relay Data (0x50 - 0x5F)
    // ============================================

    /// <summary>
    /// Data packet being relayed.
    /// </summary>
    RelayData = 0x50,

    /// <summary>
    /// Relay acknowledgment.
    /// </summary>
    RelayDataAck = 0x51,

    // ============================================
    // File Transfer (0x60 - 0x6F)
    // ============================================

    /// <summary>
    /// File transfer initiation.
    /// </summary>
    TransferInit = 0x60,

    /// <summary>
    /// File transfer acceptance.
    /// </summary>
    TransferAccept = 0x61,

    /// <summary>
    /// File transfer rejection.
    /// </summary>
    TransferReject = 0x62,

    /// <summary>
    /// File chunk data.
    /// </summary>
    TransferChunk = 0x63,

    /// <summary>
    /// Chunk acknowledgment.
    /// </summary>
    TransferChunkAck = 0x64,

    /// <summary>
    /// Transfer completion notification.
    /// </summary>
    TransferComplete = 0x65,

    /// <summary>
    /// Transfer cancellation.
    /// </summary>
    TransferCancel = 0x66,

    /// <summary>
    /// Request to resume interrupted transfer.
    /// </summary>
    TransferResume = 0x67,

    /// <summary>
    /// Resume response with position info.
    /// </summary>
    TransferResumeAck = 0x68,

    // ============================================
    // Errors (0xF0 - 0xFF)
    // ============================================

    /// <summary>
    /// Generic error response.
    /// </summary>
    Error = 0xF0,

    /// <summary>
    /// Authentication failure.
    /// </summary>
    AuthError = 0xF1,

    /// <summary>
    /// Route not found.
    /// </summary>
    RouteError = 0xF2,

    /// <summary>
    /// Transfer error.
    /// </summary>
    TransferError = 0xF3,

    /// <summary>
    /// Protocol version mismatch.
    /// </summary>
    VersionError = 0xF4
}
