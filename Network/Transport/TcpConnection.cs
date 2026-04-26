using System.Net;
using System.Net.Sockets;
using MeshAI.Core.Crypto;
using MeshAI.Core.Identity;
using MeshAI.Core.Logging;
using MeshAI.Network.Protocol;

namespace MeshAI.Network.Transport;

/// <summary>
/// Represents a TCP connection with encryption and message framing.
/// </summary>
public sealed class TcpConnection : IAsyncDisposable
{
    private static readonly Logger _log = Logger.For<TcpConnection>();
    private readonly TcpClient _client;
    private NetworkStream? _stream;
    private MessageReader? _reader;
    private MessageWriter? _writer;

    private readonly ConnectionStateMachine _stateMachine = new();
    private SessionKeys? _sessionKeys;
    private byte[]? _localEcdhPublicKey;
    private byte[]? _peerEcdhPublicKey;

    // Replay protection: timestamp window + sliding-window cache of (timestamp, messageId) tuples.
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(60);
    private readonly Dictionary<ulong, long> _seenMessages = new();
    private readonly object _seenMessagesLock = new();
    private long _lastSeenPruneMs;

    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime _lastHeartbeatSent = DateTime.MinValue;
    private DateTime _lastHeartbeatReceived = DateTime.UtcNow;
    private uint _pingSequence;

    /// <summary>
    /// Remote endpoint of this connection.
    /// </summary>
    public IPEndPoint? RemoteEndPoint { get; private set; }

    /// <summary>
    /// Local endpoint of this connection.
    /// </summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>
    /// Remote client ID (if known).
    /// </summary>
    public string? RemoteClientId { get; private set; }

    /// <summary>
    /// Current connection state.
    /// </summary>
    public ConnectionState State => _stateMachine.CurrentState;

    /// <summary>
    /// Whether the connection is fully established.
    /// </summary>
    public bool IsConnected => _stateMachine.IsConnected;

    /// <summary>
    /// Whether the connection can send/receive data (connected or in key exchange).
    /// </summary>
    public bool CanCommunicate => State is ConnectionState.Connected
                                        or ConnectionState.KeyExchange;

    /// <summary>
    /// Time of last activity on this connection.
    /// </summary>
    public DateTime LastActivity => _lastActivity;

    /// <summary>
    /// Measured round-trip time in milliseconds.
    /// </summary>
    public double RoundTripTimeMs { get; private set; }

    /// <summary>
    /// Event raised when a message is received.
    /// </summary>
    public event Func<TcpConnection, MessageFrame, Task>? MessageReceived;

    /// <summary>
    /// Event raised when the connection state changes.
    /// </summary>
    public event Action<TcpConnection, ConnectionState, ConnectionState>? StateChanged;

    /// <summary>
    /// Event raised when the connection is closed.
    /// </summary>
    public event Action<TcpConnection, Exception?>? Disconnected;

    /// <summary>
    /// Creates a new connection from an accepted client.
    /// </summary>
    public TcpConnection(TcpClient client)
    {
        _client = client;
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        LocalEndPoint = client.Client.LocalEndPoint as IPEndPoint;

        _stateMachine.StateChanged += (old, @new) => StateChanged?.Invoke(this, old, @new);
    }

    /// <summary>
    /// Creates a new outbound connection.
    /// </summary>
    public TcpConnection()
    {
        _client = new TcpClient();
        _stateMachine.StateChanged += (old, @new) => StateChanged?.Invoke(this, old, @new);
    }

    /// <summary>
    /// Connects to a remote endpoint.
    /// </summary>
    public async Task ConnectAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_stateMachine.TryTransition(ConnectionEvent.Connect, out _))
            throw new InvalidOperationException($"Cannot connect in state {State}");

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await _client.ConnectAsync(endpoint.Address, endpoint.Port, linkedCts.Token);

            RemoteEndPoint = _client.Client.RemoteEndPoint as IPEndPoint;
            LocalEndPoint = _client.Client.LocalEndPoint as IPEndPoint;

            InitializeStream();

            _stateMachine.TryTransition(ConnectionEvent.TcpConnected, out _);
        }
        catch (OperationCanceledException)
        {
            _stateMachine.TryTransition(ConnectionEvent.Timeout, out _);
            throw new TimeoutException($"Connection to {endpoint} timed out");
        }
        catch (Exception)
        {
            _stateMachine.TryTransition(ConnectionEvent.Error, out _);
            throw;
        }
    }

    /// <summary>
    /// Initializes the connection after accepting an incoming connection.
    /// </summary>
    public void Initialize()
    {
        _stateMachine.TryTransition(ConnectionEvent.Connect, out _);
        InitializeStream();
        _stateMachine.TryTransition(ConnectionEvent.TcpConnected, out _);
    }

    private void InitializeStream()
    {
        _stream = _client.GetStream();
        _reader = new MessageReader(_stream);
        _writer = new MessageWriter(_stream);
    }

    /// <summary>
    /// Performs key exchange with the remote peer.
    /// </summary>
    public async Task PerformKeyExchangeAsync(byte[] localPublicKey, CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.KeyExchange)
            throw new InvalidOperationException($"Cannot perform key exchange in state {State}");

        _localEcdhPublicKey = localPublicKey;

        // Send our public key
        var keyExchangeMessage = MessageFrame.Create(MessageType.KeyExchange, localPublicKey);
        await SendFrameAsync(keyExchangeMessage, cancellationToken);
    }

    /// <summary>
    /// Completes key exchange with the peer's public key.
    /// </summary>
    public void CompleteKeyExchange(KeyExchange localExchange, byte[] peerPublicKey)
    {
        _localEcdhPublicKey = localExchange.PublicKey;
        _peerEcdhPublicKey = peerPublicKey;
        _sessionKeys = SessionKeys.Establish(localExchange, peerPublicKey);
        _stateMachine.TryTransition(ConnectionEvent.KeyExchangeComplete, out _);
    }

    /// <summary>
    /// Session identifier derived from the key exchange (null until key exchange completes).
    /// </summary>
    public byte[]? SessionId => _sessionKeys?.SessionId;

    /// <summary>
    /// The peer's ECDH public key from the key exchange (null until key exchange completes).
    /// </summary>
    public byte[]? PeerEcdhPublicKey => _peerEcdhPublicKey;

    /// <summary>
    /// Builds and sends an IdentityProof signed by the local identity, binding our ClientId
    /// to the current session and the peer's ECDH public key.
    /// </summary>
    public async Task SendIdentityProofAsync(ClientIdentity localIdentity, CancellationToken cancellationToken = default)
    {
        if (_sessionKeys == null || _peerEcdhPublicKey == null)
            throw new InvalidOperationException("Cannot send identity proof before key exchange");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signedData = IdentityProofMessage.BuildSignedData(_sessionKeys.SessionId, _peerEcdhPublicKey, timestamp);
        var signature = localIdentity.Sign(signedData);

        var proof = new IdentityProofMessage
        {
            ClientId = localIdentity.ClientId,
            PublicSigningKey = localIdentity.PublicSigningKey,
            Signature = signature,
            Timestamp = timestamp
        };

        var frame = MessageFrame.Create(MessageType.IdentityProof, proof.ToBytes());
        await SendFrameAsync(frame, cancellationToken);
    }

    /// <summary>
    /// Verifies a received IdentityProof and, on success, sets RemoteClientId.
    /// Throws on any verification failure.
    /// </summary>
    public void VerifyAndAcceptIdentityProof(IdentityProofMessage proof, TimeSpan maxClockSkew)
    {
        if (_sessionKeys == null || _localEcdhPublicKey == null)
            throw new InvalidOperationException("Cannot verify identity proof before key exchange");

        // 1. ClientId must be cryptographically bound to the public signing key
        if (!ClientIdentity.VerifyClientIdBinding(proof.ClientId, proof.PublicSigningKey))
            throw new InvalidOperationException("IdentityProof: ClientId does not match public signing key");

        // 2. Timestamp must be within acceptable skew
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - proof.Timestamp) > maxClockSkew.TotalMilliseconds)
            throw new InvalidOperationException("IdentityProof: timestamp outside acceptable skew window");

        // 3. Signature must be valid for (sessionId || ourEcdhPublicKey || timestamp)
        var expectedSignedData = IdentityProofMessage.BuildSignedData(_sessionKeys.SessionId, _localEcdhPublicKey, proof.Timestamp);
        if (!IdentityKey.Verify(proof.PublicSigningKey, expectedSignedData, proof.Signature))
            throw new InvalidOperationException("IdentityProof: signature verification failed");

        RemoteClientId = proof.ClientId;
    }

    /// <summary>
    /// Sets the remote client ID.
    /// </summary>
    public void SetRemoteClientId(string clientId)
    {
        RemoteClientId = clientId;
    }

    /// <summary>
    /// Starts the receive loop.
    /// </summary>
    public void StartReceiving()
    {
        _receiveLoop = ReceiveLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Returns true if the frame is within the freshness window and has not been seen before.
    /// Returns false (and logs a Warning) for stale or duplicate frames.
    /// </summary>
    private bool ValidateFrameFreshness(MessageHeader header)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var skewMs = (long)MaxClockSkew.TotalMilliseconds;

        if (Math.Abs(now - header.Timestamp) > skewMs)
        {
            _log.Warn("Rejecting stale frame (type={0}, age={1}ms)", header.Type, now - header.Timestamp);
            return false;
        }

        // Pack (timestamp & 0xFFFFFFFF) << 32 | messageId for a compact key.
        // Two timestamps further than 2^32 ms (~49 days) apart could collide; well outside the skew window.
        var key = ((ulong)(uint)header.Timestamp << 32) | header.MessageId;

        lock (_seenMessagesLock)
        {
            // Periodic prune: anything older than 2*skew is no longer reachable.
            if (now - _lastSeenPruneMs > skewMs)
            {
                var cutoff = now - 2 * skewMs;
                var stale = _seenMessages.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
                foreach (var k in stale) _seenMessages.Remove(k);
                _lastSeenPruneMs = now;
            }

            if (!_seenMessages.TryAdd(key, header.Timestamp))
            {
                _log.Warn("Rejecting duplicate frame (type={0}, id={1}, ts={2})",
                    header.Type, header.MessageId, header.Timestamp);
                return false;
            }
        }

        return true;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && CanCommunicate)
            {
                var frame = await _reader!.ReadFrameAsync(cancellationToken);
                if (frame == null)
                {
                    // Connection closed
                    break;
                }

                _lastActivity = DateTime.UtcNow;

                // Replay / freshness check: reject stale or duplicate frames.
                if (!ValidateFrameFreshness(frame.Header))
                {
                    // Drop the frame silently (already logged inside the validator).
                    continue;
                }

                // Handle pong internally
                if (frame.Header.Type == MessageType.Pong)
                {
                    HandlePong(frame);
                    continue;
                }

                // Handle ping internally
                if (frame.Header.Type == MessageType.Ping)
                {
                    await HandlePingAsync(frame, cancellationToken);
                    continue;
                }

                // Decrypt if needed
                if (frame.Header.IsEncrypted && _sessionKeys != null)
                {
                    var decrypted = PayloadEncryption.Decrypt(frame.Payload, _sessionKeys.ReceiveKey);
                    frame = MessageFrame.FromParts(frame.Header, decrypted);
                }

                // Raise event for application handling
                if (MessageReceived != null)
                {
                    await MessageReceived.Invoke(this, frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _stateMachine.TryTransition(ConnectionEvent.Error, out _);
            Disconnected?.Invoke(this, ex);
            return;
        }

        _stateMachine.TryTransition(ConnectionEvent.Closed, out _);
        Disconnected?.Invoke(this, null);
    }

    private void HandlePong(MessageFrame frame)
    {
        _lastHeartbeatReceived = DateTime.UtcNow;
        var pong = PongMessage.FromBytes(frame.Payload);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RoundTripTimeMs = now - pong.OriginalTimestamp;
    }

    private async Task HandlePingAsync(MessageFrame frame, CancellationToken cancellationToken)
    {
        var ping = PingMessage.FromBytes(frame.Payload);
        var pong = new PongMessage
        {
            OriginalTimestamp = ping.Timestamp,
            Sequence = ping.Sequence,
            ResponseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await SendAsync(pong, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Sends a heartbeat ping.
    /// </summary>
    public async Task SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var ping = new PingMessage
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence = Interlocked.Increment(ref _pingSequence)
        };
        _lastHeartbeatSent = DateTime.UtcNow;
        await SendAsync(ping, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Checks if heartbeat has timed out.
    /// </summary>
    public bool CheckHeartbeatTimeout(TimeSpan timeout)
    {
        if (_lastHeartbeatSent == DateTime.MinValue)
            return false;

        var timeSinceLastReceived = DateTime.UtcNow - _lastHeartbeatReceived;
        if (timeSinceLastReceived > timeout)
        {
            _stateMachine.TryTransition(ConnectionEvent.HeartbeatTimeout, out _);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sends a message payload.
    /// </summary>
    public async Task SendAsync(IMessagePayload payload, bool encrypt = false, CancellationToken cancellationToken = default)
    {
        var flags = MessageFlags.None;
        var payloadBytes = payload.ToBytes();

        if (encrypt && _sessionKeys != null)
        {
            payloadBytes = PayloadEncryption.Encrypt(payloadBytes, _sessionKeys.SendKey);
            flags |= MessageFlags.Encrypted;
        }

        var frame = MessageFrame.Create(payload.MessageType, payloadBytes, flags);
        await SendFrameAsync(frame, cancellationToken);
    }

    /// <summary>
    /// Sends a raw message frame.
    /// </summary>
    public async Task SendFrameAsync(MessageFrame frame, CancellationToken cancellationToken = default)
    {
        if (_writer == null)
            throw new InvalidOperationException("Connection not initialized");

        await _writer.WriteFrameAsync(frame, cancellationToken);
        _lastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Reads the next message frame.
    /// </summary>
    public async Task<MessageFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_reader == null)
            throw new InvalidOperationException("Connection not initialized");

        var frame = await _reader.ReadFrameAsync(cancellationToken);
        if (frame != null)
        {
            _lastActivity = DateTime.UtcNow;

            // Decrypt if needed
            if (frame.Header.IsEncrypted && _sessionKeys != null)
            {
                var decrypted = PayloadEncryption.Decrypt(frame.Payload, _sessionKeys.ReceiveKey);
                frame = MessageFrame.FromParts(frame.Header, decrypted);
            }
        }

        return frame;
    }

    /// <summary>
    /// Gracefully closes the connection.
    /// </summary>
    public async Task CloseAsync()
    {
        _stateMachine.TryTransition(ConnectionEvent.Disconnect, out _);

        try
        {
            await _cts.CancelAsync();

            if (_receiveLoop != null)
            {
                try
                {
                    await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    // Ignore timeout
                }
            }

            _client.Close();
        }
        finally
        {
            _stateMachine.TryTransition(ConnectionEvent.Closed, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _sessionKeys?.Dispose();
        _cts.Dispose();
    }
}
