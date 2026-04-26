using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace MeshAI.Network.Protocol;

/// <summary>
/// Base interface for all message payloads.
/// </summary>
public interface IMessagePayload
{
    /// <summary>
    /// Serializes the payload to bytes.
    /// </summary>
    byte[] ToBytes();

    /// <summary>
    /// Gets the message type for this payload.
    /// </summary>
    MessageType MessageType { get; }
}

/// <summary>
/// Client registration request.
/// </summary>
public sealed class RegisterMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.Register;

    /// <summary>
    /// Client's unique identifier (64 hex chars = 32 bytes).
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Client's local IP address.
    /// </summary>
    public required IPAddress LocalAddress { get; init; }

    /// <summary>
    /// Client's listening port.
    /// </summary>
    public required ushort ListenPort { get; init; }

    /// <summary>
    /// Whether this client can act as a relay.
    /// </summary>
    public required bool CanRelay { get; init; }

    /// <summary>
    /// Client's public key for key exchange.
    /// </summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>
    /// Optional subnet mask for topology hints.
    /// </summary>
    public byte SubnetMask { get; init; } = 24;

    public byte[] ToBytes()
    {
        var clientIdBytes = Encoding.ASCII.GetBytes(ClientId);
        var addressBytes = LocalAddress.GetAddressBytes();

        // ClientId length (1) + ClientId + Address length (1) + Address + Port (2) + CanRelay (1) + SubnetMask (1) + PublicKey length (2) + PublicKey
        var size = 1 + clientIdBytes.Length + 1 + addressBytes.Length + 2 + 1 + 1 + 2 + PublicKey.Length;
        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = (byte)clientIdBytes.Length;
        clientIdBytes.CopyTo(buffer.AsSpan(offset));
        offset += clientIdBytes.Length;

        buffer[offset++] = (byte)addressBytes.Length;
        addressBytes.CopyTo(buffer.AsSpan(offset));
        offset += addressBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), ListenPort);
        offset += 2;

        buffer[offset++] = CanRelay ? (byte)1 : (byte)0;
        buffer[offset++] = SubnetMask;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)PublicKey.Length);
        offset += 2;
        PublicKey.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static RegisterMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var clientIdLength = data[offset++];
        var clientId = Encoding.ASCII.GetString(data.Slice(offset, clientIdLength));
        offset += clientIdLength;

        var addressLength = data[offset++];
        var address = new IPAddress(data.Slice(offset, addressLength));
        offset += addressLength;

        var port = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;

        var canRelay = data[offset++] != 0;
        var subnetMask = data[offset++];

        var publicKeyLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var publicKey = data.Slice(offset, publicKeyLength).ToArray();

        return new RegisterMessage
        {
            ClientId = clientId,
            LocalAddress = address,
            ListenPort = port,
            CanRelay = canRelay,
            SubnetMask = subnetMask,
            PublicKey = publicKey
        };
    }
}

/// <summary>
/// Server acknowledgment of registration.
/// </summary>
public sealed class RegisterAckMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.RegisterAck;

    /// <summary>
    /// Whether registration was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Client's public IP as seen by server.
    /// </summary>
    public IPAddress? PublicAddress { get; init; }

    /// <summary>
    /// Server's public key for key exchange.
    /// </summary>
    public byte[]? ServerPublicKey { get; init; }

    /// <summary>
    /// Error message if registration failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public byte[] ToBytes()
    {
        var errorBytes = ErrorMessage != null ? Encoding.UTF8.GetBytes(ErrorMessage) : [];
        var addressBytes = PublicAddress?.GetAddressBytes() ?? [];
        var keyBytes = ServerPublicKey ?? [];

        var size = 1 + 1 + addressBytes.Length + 2 + keyBytes.Length + 2 + errorBytes.Length;
        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = Success ? (byte)1 : (byte)0;
        buffer[offset++] = (byte)addressBytes.Length;
        addressBytes.CopyTo(buffer.AsSpan(offset));
        offset += addressBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)keyBytes.Length);
        offset += 2;
        keyBytes.CopyTo(buffer.AsSpan(offset));
        offset += keyBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)errorBytes.Length);
        offset += 2;
        errorBytes.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static RegisterAckMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var success = data[offset++] != 0;
        var addressLength = data[offset++];
        var address = addressLength > 0 ? new IPAddress(data.Slice(offset, addressLength)) : null;
        offset += addressLength;

        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var key = keyLength > 0 ? data.Slice(offset, keyLength).ToArray() : null;
        offset += keyLength;

        var errorLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var error = errorLength > 0 ? Encoding.UTF8.GetString(data.Slice(offset, errorLength)) : null;

        return new RegisterAckMessage
        {
            Success = success,
            PublicAddress = address,
            ServerPublicKey = key,
            ErrorMessage = error
        };
    }
}

/// <summary>
/// Information about a peer in the network.
/// </summary>
public sealed class PeerInfo
{
    public required string ClientId { get; init; }
    public required IPAddress LocalAddress { get; init; }
    public IPAddress? PublicAddress { get; init; }
    public required ushort ListenPort { get; init; }
    public required bool CanRelay { get; init; }
    public required long LastSeen { get; init; }
    public byte SubnetMask { get; init; } = 24;

    public byte[] ToBytes()
    {
        var clientIdBytes = Encoding.ASCII.GetBytes(ClientId);
        var localAddressBytes = LocalAddress.GetAddressBytes();
        var publicAddressBytes = PublicAddress?.GetAddressBytes() ?? [];

        var size = 1 + clientIdBytes.Length + 1 + localAddressBytes.Length +
                   1 + publicAddressBytes.Length + 2 + 1 + 8 + 1;
        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = (byte)clientIdBytes.Length;
        clientIdBytes.CopyTo(buffer.AsSpan(offset));
        offset += clientIdBytes.Length;

        buffer[offset++] = (byte)localAddressBytes.Length;
        localAddressBytes.CopyTo(buffer.AsSpan(offset));
        offset += localAddressBytes.Length;

        buffer[offset++] = (byte)publicAddressBytes.Length;
        publicAddressBytes.CopyTo(buffer.AsSpan(offset));
        offset += publicAddressBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), ListenPort);
        offset += 2;

        buffer[offset++] = CanRelay ? (byte)1 : (byte)0;

        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset), LastSeen);
        offset += 8;

        buffer[offset] = SubnetMask;

        return buffer;
    }

    public static PeerInfo FromBytes(ReadOnlySpan<byte> data, out int bytesRead)
    {
        var offset = 0;

        var clientIdLength = data[offset++];
        var clientId = Encoding.ASCII.GetString(data.Slice(offset, clientIdLength));
        offset += clientIdLength;

        var localAddressLength = data[offset++];
        var localAddress = new IPAddress(data.Slice(offset, localAddressLength));
        offset += localAddressLength;

        var publicAddressLength = data[offset++];
        var publicAddress = publicAddressLength > 0 ? new IPAddress(data.Slice(offset, publicAddressLength)) : null;
        offset += publicAddressLength;

        var port = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;

        var canRelay = data[offset++] != 0;

        var lastSeen = BinaryPrimitives.ReadInt64BigEndian(data.Slice(offset, 8));
        offset += 8;

        var subnetMask = data[offset++];

        bytesRead = offset;
        return new PeerInfo
        {
            ClientId = clientId,
            LocalAddress = localAddress,
            PublicAddress = publicAddress,
            ListenPort = port,
            CanRelay = canRelay,
            LastSeen = lastSeen,
            SubnetMask = subnetMask
        };
    }
}

/// <summary>
/// Peer list response from server.
/// </summary>
public sealed class PeerListMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.PeerListResponse;

    public required List<PeerInfo> Peers { get; init; }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();

        // Write count as big-endian
        var countBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(countBytes, (ushort)Peers.Count);
        ms.Write(countBytes);

        foreach (var peer in Peers)
        {
            var peerBytes = peer.ToBytes();
            var lengthBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(lengthBytes, (ushort)peerBytes.Length);
            ms.Write(lengthBytes);
            ms.Write(peerBytes);
        }

        return ms.ToArray();
    }

    public static PeerListMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var count = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;

        var peers = new List<PeerInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var peerLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            offset += 2;
            var peer = PeerInfo.FromBytes(data.Slice(offset, peerLength), out _);
            offset += peerLength;
            peers.Add(peer);
        }

        return new PeerListMessage { Peers = peers };
    }
}

/// <summary>
/// Heartbeat ping message.
/// </summary>
public sealed class PingMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.Ping;

    public required long Timestamp { get; init; }
    public required uint Sequence { get; init; }

    public byte[] ToBytes()
    {
        var buffer = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(0, 8), Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), Sequence);
        return buffer;
    }

    public static PingMessage FromBytes(ReadOnlySpan<byte> data)
    {
        return new PingMessage
        {
            Timestamp = BinaryPrimitives.ReadInt64BigEndian(data[..8]),
            Sequence = BinaryPrimitives.ReadUInt32BigEndian(data[8..12])
        };
    }
}

/// <summary>
/// Heartbeat pong response.
/// </summary>
public sealed class PongMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.Pong;

    public required long OriginalTimestamp { get; init; }
    public required uint Sequence { get; init; }
    public required long ResponseTimestamp { get; init; }

    public byte[] ToBytes()
    {
        var buffer = new byte[20];
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(0, 8), OriginalTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), Sequence);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(12, 8), ResponseTimestamp);
        return buffer;
    }

    public static PongMessage FromBytes(ReadOnlySpan<byte> data)
    {
        return new PongMessage
        {
            OriginalTimestamp = BinaryPrimitives.ReadInt64BigEndian(data[..8]),
            Sequence = BinaryPrimitives.ReadUInt32BigEndian(data[8..12]),
            ResponseTimestamp = BinaryPrimitives.ReadInt64BigEndian(data[12..20])
        };
    }
}

/// <summary>
/// Direct message between peers.
/// </summary>
public sealed class DirectMessagePayload : IMessagePayload
{
    public MessageType MessageType => MessageType.DirectMessage;

    public required string SourceClientId { get; init; }
    public required string TargetClientId { get; init; }
    public required byte[] Data { get; init; }

    public byte[] ToBytes()
    {
        var sourceBytes = Encoding.ASCII.GetBytes(SourceClientId);
        var targetBytes = Encoding.ASCII.GetBytes(TargetClientId);

        var size = 1 + sourceBytes.Length + 1 + targetBytes.Length + 4 + Data.Length;
        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = (byte)sourceBytes.Length;
        sourceBytes.CopyTo(buffer.AsSpan(offset));
        offset += sourceBytes.Length;

        buffer[offset++] = (byte)targetBytes.Length;
        targetBytes.CopyTo(buffer.AsSpan(offset));
        offset += targetBytes.Length;

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), (uint)Data.Length);
        offset += 4;
        Data.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static DirectMessagePayload FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var sourceLength = data[offset++];
        var source = Encoding.ASCII.GetString(data.Slice(offset, sourceLength));
        offset += sourceLength;

        var targetLength = data[offset++];
        var target = Encoding.ASCII.GetString(data.Slice(offset, targetLength));
        offset += targetLength;

        var dataLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;
        var payload = data.Slice(offset, (int)dataLength).ToArray();

        return new DirectMessagePayload
        {
            SourceClientId = source,
            TargetClientId = target,
            Data = payload
        };
    }
}

/// <summary>
/// Relayed data packet.
/// </summary>
public sealed class RelayDataMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.RelayData;

    public required string SourceClientId { get; init; }
    public required string TargetClientId { get; init; }
    public required byte[] EncryptedPayload { get; init; }
    public required uint HopCount { get; init; }

    public byte[] ToBytes()
    {
        var sourceBytes = Encoding.ASCII.GetBytes(SourceClientId);
        var targetBytes = Encoding.ASCII.GetBytes(TargetClientId);

        var size = 1 + sourceBytes.Length + 1 + targetBytes.Length + 4 + 4 + EncryptedPayload.Length;
        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = (byte)sourceBytes.Length;
        sourceBytes.CopyTo(buffer.AsSpan(offset));
        offset += sourceBytes.Length;

        buffer[offset++] = (byte)targetBytes.Length;
        targetBytes.CopyTo(buffer.AsSpan(offset));
        offset += targetBytes.Length;

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), HopCount);
        offset += 4;

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), (uint)EncryptedPayload.Length);
        offset += 4;
        EncryptedPayload.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static RelayDataMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var sourceLength = data[offset++];
        var source = Encoding.ASCII.GetString(data.Slice(offset, sourceLength));
        offset += sourceLength;

        var targetLength = data[offset++];
        var target = Encoding.ASCII.GetString(data.Slice(offset, targetLength));
        offset += targetLength;

        var hopCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;
        var payload = data.Slice(offset, (int)payloadLength).ToArray();

        return new RelayDataMessage
        {
            SourceClientId = source,
            TargetClientId = target,
            HopCount = hopCount,
            EncryptedPayload = payload
        };
    }
}

/// <summary>
/// Error message.
/// </summary>
public sealed class ErrorMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.Error;

    public required MessageType OriginalMessageType { get; init; }
    public required uint OriginalMessageId { get; init; }
    public required string ErrorCode { get; init; }
    public required string ErrorDescription { get; init; }

    public byte[] ToBytes()
    {
        var codeBytes = Encoding.UTF8.GetBytes(ErrorCode);
        var descBytes = Encoding.UTF8.GetBytes(ErrorDescription);

        var size = 1 + 4 + 2 + codeBytes.Length + 2 + descBytes.Length;
        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = (byte)OriginalMessageType;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), OriginalMessageId);
        offset += 4;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)codeBytes.Length);
        offset += 2;
        codeBytes.CopyTo(buffer.AsSpan(offset));
        offset += codeBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)descBytes.Length);
        offset += 2;
        descBytes.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static ErrorMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var originalType = (MessageType)data[offset++];
        var originalId = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;

        var codeLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var code = Encoding.UTF8.GetString(data.Slice(offset, codeLength));
        offset += codeLength;

        var descLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var desc = Encoding.UTF8.GetString(data.Slice(offset, descLength));

        return new ErrorMessage
        {
            OriginalMessageType = originalType,
            OriginalMessageId = originalId,
            ErrorCode = code,
            ErrorDescription = desc
        };
    }
}

/// <summary>
/// Identity proof exchanged after key exchange. Each side sends one of these to prove
/// it owns the private key matching the claimed ClientId.
///
/// The signature is computed over the SHA-256 hash of:
///   sessionId || peerEcdhPublicKey || timestamp(8 BE bytes)
///
/// Verifier must check:
///   1. SHA256(PublicSigningKey) == ClientId  (ClientId binding)
///   2. ECDSA.VerifyData(signed_data, Signature, PublicSigningKey)  (ownership proof)
///   3. timestamp is within an acceptable skew window
/// </summary>
public sealed class IdentityProofMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.IdentityProof;

    /// <summary>The sender's claimed ClientId.</summary>
    public required string ClientId { get; init; }

    /// <summary>The sender's public signing key (SPKI format).</summary>
    public required byte[] PublicSigningKey { get; init; }

    /// <summary>Signature over (sessionId || peerEcdhPublicKey || timestamp).</summary>
    public required byte[] Signature { get; init; }

    /// <summary>Unix-ms timestamp included in the signature payload.</summary>
    public required long Timestamp { get; init; }

    public byte[] ToBytes()
    {
        var clientIdBytes = Encoding.ASCII.GetBytes(ClientId);

        // ClientId len (1) + ClientId + PubKey len (2) + PubKey + Sig len (2) + Sig + Timestamp (8)
        var size = 1 + clientIdBytes.Length + 2 + PublicSigningKey.Length + 2 + Signature.Length + 8;
        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = (byte)clientIdBytes.Length;
        clientIdBytes.CopyTo(buffer.AsSpan(offset));
        offset += clientIdBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)PublicSigningKey.Length);
        offset += 2;
        PublicSigningKey.CopyTo(buffer.AsSpan(offset));
        offset += PublicSigningKey.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)Signature.Length);
        offset += 2;
        Signature.CopyTo(buffer.AsSpan(offset));
        offset += Signature.Length;

        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset), Timestamp);

        return buffer;
    }

    public static IdentityProofMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var clientIdLength = data[offset++];
        var clientId = Encoding.ASCII.GetString(data.Slice(offset, clientIdLength));
        offset += clientIdLength;

        var pubKeyLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var pubKey = data.Slice(offset, pubKeyLength).ToArray();
        offset += pubKeyLength;

        var sigLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var signature = data.Slice(offset, sigLength).ToArray();
        offset += sigLength;

        var timestamp = BinaryPrimitives.ReadInt64BigEndian(data.Slice(offset, 8));

        return new IdentityProofMessage
        {
            ClientId = clientId,
            PublicSigningKey = pubKey,
            Signature = signature,
            Timestamp = timestamp
        };
    }

    /// <summary>
    /// Computes the canonical bytes that must be signed (and verified) for an identity proof.
    /// </summary>
    public static byte[] BuildSignedData(byte[] sessionId, byte[] peerEcdhPublicKey, long timestamp)
    {
        var buffer = new byte[sessionId.Length + peerEcdhPublicKey.Length + 8];
        var offset = 0;
        sessionId.CopyTo(buffer.AsSpan(offset));
        offset += sessionId.Length;
        peerEcdhPublicKey.CopyTo(buffer.AsSpan(offset));
        offset += peerEcdhPublicKey.Length;
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset), timestamp);
        return buffer;
    }
}
