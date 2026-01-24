using System.Buffers.Binary;

namespace MeshAI.Network.Protocol;

/// <summary>
/// Protocol version constant.
/// </summary>
public static class ProtocolVersion
{
    public const byte Major = 1;
    public const byte Minor = 0;

    public static ushort Current => (ushort)((Major << 8) | Minor);

    public static bool IsCompatible(ushort version)
    {
        var major = (byte)(version >> 8);
        return major == Major; // Compatible if same major version
    }
}

/// <summary>
/// Message flags for additional message properties.
/// </summary>
[Flags]
public enum MessageFlags : byte
{
    None = 0x00,

    /// <summary>
    /// Message payload is encrypted.
    /// </summary>
    Encrypted = 0x01,

    /// <summary>
    /// Message requires acknowledgment.
    /// </summary>
    RequiresAck = 0x02,

    /// <summary>
    /// Message is being relayed (not direct).
    /// </summary>
    Relayed = 0x04,

    /// <summary>
    /// Message is a response to another message.
    /// </summary>
    IsResponse = 0x08,

    /// <summary>
    /// Message is fragmented (more fragments follow).
    /// </summary>
    Fragmented = 0x10,

    /// <summary>
    /// Message is the final fragment.
    /// </summary>
    FinalFragment = 0x20,

    /// <summary>
    /// Message has hardware binding.
    /// </summary>
    HardwareBound = 0x40,

    /// <summary>
    /// Message is compressed.
    /// </summary>
    Compressed = 0x80
}

/// <summary>
/// Fixed-size message header (32 bytes).
///
/// Wire format:
/// [0-1]   Protocol version (2 bytes, big-endian)
/// [2]     Message type (1 byte)
/// [3]     Flags (1 byte)
/// [4-7]   Message ID (4 bytes, big-endian)
/// [8-11]  Payload length (4 bytes, big-endian)
/// [12-19] Timestamp (8 bytes, Unix ms, big-endian)
/// [20-23] Fragment index (4 bytes, big-endian) - 0 if not fragmented
/// [24-27] Total fragments (4 bytes, big-endian) - 0 if not fragmented
/// [28-31] Reserved (4 bytes)
/// </summary>
public readonly struct MessageHeader
{
    public const int Size = 32;

    public ushort Version { get; init; }
    public MessageType Type { get; init; }
    public MessageFlags Flags { get; init; }
    public uint MessageId { get; init; }
    public uint PayloadLength { get; init; }
    public long Timestamp { get; init; }
    public uint FragmentIndex { get; init; }
    public uint TotalFragments { get; init; }

    public bool IsEncrypted => Flags.HasFlag(MessageFlags.Encrypted);
    public bool RequiresAck => Flags.HasFlag(MessageFlags.RequiresAck);
    public bool IsRelayed => Flags.HasFlag(MessageFlags.Relayed);
    public bool IsResponse => Flags.HasFlag(MessageFlags.IsResponse);
    public bool IsFragmented => Flags.HasFlag(MessageFlags.Fragmented);
    public bool IsFinalFragment => Flags.HasFlag(MessageFlags.FinalFragment);
    public bool IsHardwareBound => Flags.HasFlag(MessageFlags.HardwareBound);
    public bool IsCompressed => Flags.HasFlag(MessageFlags.Compressed);

    /// <summary>
    /// Creates a new message header with current timestamp.
    /// </summary>
    public static MessageHeader Create(MessageType type, uint payloadLength, MessageFlags flags = MessageFlags.None)
    {
        return new MessageHeader
        {
            Version = ProtocolVersion.Current,
            Type = type,
            Flags = flags,
            MessageId = GenerateMessageId(),
            PayloadLength = payloadLength,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FragmentIndex = 0,
            TotalFragments = 0
        };
    }

    /// <summary>
    /// Creates a response header for a given request.
    /// </summary>
    public static MessageHeader CreateResponse(MessageHeader request, MessageType responseType, uint payloadLength, MessageFlags flags = MessageFlags.None)
    {
        return new MessageHeader
        {
            Version = ProtocolVersion.Current,
            Type = responseType,
            Flags = flags | MessageFlags.IsResponse,
            MessageId = request.MessageId,
            PayloadLength = payloadLength,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FragmentIndex = 0,
            TotalFragments = 0
        };
    }

    /// <summary>
    /// Creates a fragment header.
    /// </summary>
    public static MessageHeader CreateFragment(MessageType type, uint messageId, uint payloadLength,
        uint fragmentIndex, uint totalFragments, MessageFlags additionalFlags = MessageFlags.None)
    {
        var flags = MessageFlags.Fragmented | additionalFlags;
        if (fragmentIndex == totalFragments - 1)
            flags |= MessageFlags.FinalFragment;

        return new MessageHeader
        {
            Version = ProtocolVersion.Current,
            Type = type,
            Flags = flags,
            MessageId = messageId,
            PayloadLength = payloadLength,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FragmentIndex = fragmentIndex,
            TotalFragments = totalFragments
        };
    }

    /// <summary>
    /// Serializes the header to a byte array.
    /// </summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        WriteToSpan(buffer);
        return buffer;
    }

    /// <summary>
    /// Writes the header to a span.
    /// </summary>
    public void WriteToSpan(Span<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes");

        BinaryPrimitives.WriteUInt16BigEndian(buffer[0..2], Version);
        buffer[2] = (byte)Type;
        buffer[3] = (byte)Flags;
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..8], MessageId);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..12], PayloadLength);
        BinaryPrimitives.WriteInt64BigEndian(buffer[12..20], Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[20..24], FragmentIndex);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[24..28], TotalFragments);
        buffer[28..32].Clear(); // Reserved
    }

    /// <summary>
    /// Parses a header from a byte array.
    /// </summary>
    public static MessageHeader FromBytes(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes");

        return new MessageHeader
        {
            Version = BinaryPrimitives.ReadUInt16BigEndian(buffer[0..2]),
            Type = (MessageType)buffer[2],
            Flags = (MessageFlags)buffer[3],
            MessageId = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..8]),
            PayloadLength = BinaryPrimitives.ReadUInt32BigEndian(buffer[8..12]),
            Timestamp = BinaryPrimitives.ReadInt64BigEndian(buffer[12..20]),
            FragmentIndex = BinaryPrimitives.ReadUInt32BigEndian(buffer[20..24]),
            TotalFragments = BinaryPrimitives.ReadUInt32BigEndian(buffer[24..28])
        };
    }

    private static uint _messageIdCounter;

    private static uint GenerateMessageId()
    {
        return Interlocked.Increment(ref _messageIdCounter);
    }
}
