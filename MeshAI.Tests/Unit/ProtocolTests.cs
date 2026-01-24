using System.Net;
using MeshAI.Network.Protocol;
using Xunit;

namespace MeshAI.Tests.Unit;

/// <summary>
/// Unit tests for protocol components.
/// </summary>
public class ProtocolTests
{
    [Fact]
    public void MessageHeader_Create_SetsCorrectValues()
    {
        // Act
        var header = MessageHeader.Create(MessageType.Register, 100);

        // Assert
        Assert.Equal(ProtocolVersion.Current, header.Version);
        Assert.Equal(MessageType.Register, header.Type);
        Assert.Equal(100u, header.PayloadLength);
        Assert.True(header.Timestamp > 0);
    }

    [Fact]
    public void MessageHeader_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = MessageHeader.Create(MessageType.DirectMessage, 500, MessageFlags.Encrypted | MessageFlags.RequiresAck);

        // Act
        var bytes = original.ToBytes();
        var parsed = MessageHeader.FromBytes(bytes);

        // Assert
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.Type, parsed.Type);
        Assert.Equal(original.Flags, parsed.Flags);
        Assert.Equal(original.MessageId, parsed.MessageId);
        Assert.Equal(original.PayloadLength, parsed.PayloadLength);
        Assert.Equal(original.Timestamp, parsed.Timestamp);
    }

    [Fact]
    public void MessageHeader_Size_Is32Bytes()
    {
        // Arrange
        var header = MessageHeader.Create(MessageType.Ping, 0);

        // Act
        var bytes = header.ToBytes();

        // Assert
        Assert.Equal(32, bytes.Length);
        Assert.Equal(32, MessageHeader.Size);
    }

    [Fact]
    public void MessageHeader_Flags_AreSetCorrectly()
    {
        // Arrange
        var header = MessageHeader.Create(MessageType.TransferChunk,
            1000,
            MessageFlags.Encrypted | MessageFlags.HardwareBound);

        // Assert
        Assert.True(header.IsEncrypted);
        Assert.True(header.IsHardwareBound);
        Assert.False(header.IsRelayed);
        Assert.False(header.IsCompressed);
    }

    [Fact]
    public void MessageHeader_CreateResponse_SetsResponseFlag()
    {
        // Arrange
        var request = MessageHeader.Create(MessageType.Ping, 0);

        // Act
        var response = MessageHeader.CreateResponse(request, MessageType.Pong, 0);

        // Assert
        Assert.True(response.IsResponse);
        Assert.Equal(request.MessageId, response.MessageId);
    }

    [Fact]
    public void MessageHeader_CreateFragment_SetsFragmentInfo()
    {
        // Act
        var fragment = MessageHeader.CreateFragment(MessageType.TransferChunk, 123, 1000, 2, 5);

        // Assert
        Assert.True(fragment.IsFragmented);
        Assert.False(fragment.IsFinalFragment);
        Assert.Equal(123u, fragment.MessageId);
        Assert.Equal(2u, fragment.FragmentIndex);
        Assert.Equal(5u, fragment.TotalFragments);
    }

    [Fact]
    public void MessageHeader_CreateFragment_SetsFinalFlag()
    {
        // Act
        var fragment = MessageHeader.CreateFragment(MessageType.TransferChunk, 123, 500, 4, 5);

        // Assert
        Assert.True(fragment.IsFinalFragment);
    }

    [Fact]
    public void ProtocolVersion_IsCompatible_SameMajor()
    {
        // Arrange
        var version = ProtocolVersion.Current;
        var sameMajor = (ushort)((ProtocolVersion.Major << 8) | 99);

        // Assert
        Assert.True(ProtocolVersion.IsCompatible(version));
        Assert.True(ProtocolVersion.IsCompatible(sameMajor));
    }

    [Fact]
    public void ProtocolVersion_IsIncompatible_DifferentMajor()
    {
        // Arrange
        var differentMajor = (ushort)(((ProtocolVersion.Major + 1) << 8) | 0);

        // Assert
        Assert.False(ProtocolVersion.IsCompatible(differentMajor));
    }

    [Fact]
    public void RegisterMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new RegisterMessage
        {
            ClientId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            LocalAddress = IPAddress.Parse("192.168.1.100"),
            ListenPort = 9500,
            CanRelay = true,
            PublicKey = new byte[] { 1, 2, 3, 4, 5 },
            SubnetMask = 24
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = RegisterMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.ClientId, parsed.ClientId);
        Assert.Equal(original.LocalAddress, parsed.LocalAddress);
        Assert.Equal(original.ListenPort, parsed.ListenPort);
        Assert.Equal(original.CanRelay, parsed.CanRelay);
        Assert.Equal(original.PublicKey, parsed.PublicKey);
        Assert.Equal(original.SubnetMask, parsed.SubnetMask);
    }

    [Fact]
    public void RegisterAckMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new RegisterAckMessage
        {
            Success = true,
            PublicAddress = IPAddress.Parse("203.0.113.45"),
            ServerPublicKey = new byte[] { 10, 20, 30 }
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = RegisterAckMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.Success, parsed.Success);
        Assert.Equal(original.PublicAddress, parsed.PublicAddress);
        Assert.Equal(original.ServerPublicKey, parsed.ServerPublicKey);
    }

    [Fact]
    public void PeerInfo_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new PeerInfo
        {
            ClientId = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
            LocalAddress = IPAddress.Parse("10.0.0.5"),
            PublicAddress = IPAddress.Parse("1.2.3.4"),
            ListenPort = 8080,
            CanRelay = true,
            LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SubnetMask = 16
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = PeerInfo.FromBytes(bytes, out var bytesRead);

        // Assert
        Assert.Equal(original.ClientId, parsed.ClientId);
        Assert.Equal(original.LocalAddress, parsed.LocalAddress);
        Assert.Equal(original.PublicAddress, parsed.PublicAddress);
        Assert.Equal(original.ListenPort, parsed.ListenPort);
        Assert.Equal(original.CanRelay, parsed.CanRelay);
        Assert.Equal(original.LastSeen, parsed.LastSeen);
        Assert.Equal(original.SubnetMask, parsed.SubnetMask);
        Assert.True(bytesRead > 0);
    }

    [Fact]
    public void PeerListMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new PeerListMessage
        {
            Peers =
            [
                new PeerInfo
                {
                    ClientId = "aaaa" + new string('0', 60),
                    LocalAddress = IPAddress.Loopback,
                    ListenPort = 1000,
                    CanRelay = false,
                    LastSeen = 12345678
                },
                new PeerInfo
                {
                    ClientId = "bbbb" + new string('1', 60),
                    LocalAddress = IPAddress.Parse("192.168.0.1"),
                    ListenPort = 2000,
                    CanRelay = true,
                    LastSeen = 87654321
                }
            ]
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = PeerListMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(2, parsed.Peers.Count);
        Assert.Equal(original.Peers[0].ClientId, parsed.Peers[0].ClientId);
        Assert.Equal(original.Peers[1].ClientId, parsed.Peers[1].ClientId);
    }

    [Fact]
    public void PingMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new PingMessage
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence = 42
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = PingMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.Timestamp, parsed.Timestamp);
        Assert.Equal(original.Sequence, parsed.Sequence);
    }

    [Fact]
    public void PongMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new PongMessage
        {
            OriginalTimestamp = 1000000,
            Sequence = 42,
            ResponseTimestamp = 1000050
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = PongMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.OriginalTimestamp, parsed.OriginalTimestamp);
        Assert.Equal(original.Sequence, parsed.Sequence);
        Assert.Equal(original.ResponseTimestamp, parsed.ResponseTimestamp);
    }

    [Fact]
    public void DirectMessagePayload_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new DirectMessagePayload
        {
            SourceClientId = "source" + new string('a', 58),
            TargetClientId = "target" + new string('b', 58),
            Data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = DirectMessagePayload.FromBytes(bytes);

        // Assert
        Assert.Equal(original.SourceClientId, parsed.SourceClientId);
        Assert.Equal(original.TargetClientId, parsed.TargetClientId);
        Assert.Equal(original.Data, parsed.Data);
    }

    [Fact]
    public void RelayDataMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new RelayDataMessage
        {
            SourceClientId = "src" + new string('1', 61),
            TargetClientId = "tgt" + new string('2', 61),
            HopCount = 3,
            EncryptedPayload = new byte[100]
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = RelayDataMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.SourceClientId, parsed.SourceClientId);
        Assert.Equal(original.TargetClientId, parsed.TargetClientId);
        Assert.Equal(original.HopCount, parsed.HopCount);
        Assert.Equal(original.EncryptedPayload, parsed.EncryptedPayload);
    }

    [Fact]
    public void ErrorMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new ErrorMessage
        {
            OriginalMessageType = MessageType.RouteRequest,
            OriginalMessageId = 12345,
            ErrorCode = "NOT_FOUND",
            ErrorDescription = "Target client not found"
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = ErrorMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.OriginalMessageType, parsed.OriginalMessageType);
        Assert.Equal(original.OriginalMessageId, parsed.OriginalMessageId);
        Assert.Equal(original.ErrorCode, parsed.ErrorCode);
        Assert.Equal(original.ErrorDescription, parsed.ErrorDescription);
    }

    [Fact]
    public void MessageFrame_Create_FromPayload()
    {
        // Arrange
        var payload = new PingMessage
        {
            Timestamp = 123456789,
            Sequence = 1
        };

        // Act
        var frame = MessageFrame.Create(payload);

        // Assert
        Assert.Equal(MessageType.Ping, frame.Header.Type);
        Assert.Equal((uint)frame.Payload.Length, frame.Header.PayloadLength);
    }

    [Fact]
    public void MessageFrame_ToBytes_IncludesHeaderAndPayload()
    {
        // Arrange
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = MessageFrame.Create(MessageType.DirectMessage, payload);

        // Act
        var bytes = frame.ToBytes();

        // Assert
        Assert.Equal(MessageHeader.Size + payload.Length, bytes.Length);
        Assert.Equal(frame.TotalSize, bytes.Length);
    }
}
