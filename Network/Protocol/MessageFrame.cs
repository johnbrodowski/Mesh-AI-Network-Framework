using MeshAI.Core.Logging;

namespace MeshAI.Network.Protocol;

/// <summary>
/// Represents a complete message frame (header + payload) for wire transmission.
/// </summary>
public sealed class MessageFrame
{
    /// <summary>
    /// Message header.
    /// </summary>
    public MessageHeader Header { get; }

    /// <summary>
    /// Message payload bytes.
    /// </summary>
    public byte[] Payload { get; }

    private MessageFrame(MessageHeader header, byte[] payload)
    {
        Header = header;
        Payload = payload;
    }

    /// <summary>
    /// Creates a new message frame from a payload.
    /// </summary>
    public static MessageFrame Create(IMessagePayload payload, MessageFlags flags = MessageFlags.None)
    {
        var payloadBytes = payload.ToBytes();
        var header = MessageHeader.Create(payload.MessageType, (uint)payloadBytes.Length, flags);
        return new MessageFrame(header, payloadBytes);
    }

    /// <summary>
    /// Creates a response frame for a given request.
    /// </summary>
    public static MessageFrame CreateResponse(MessageHeader requestHeader, IMessagePayload payload, MessageFlags flags = MessageFlags.None)
    {
        var payloadBytes = payload.ToBytes();
        var header = MessageHeader.CreateResponse(requestHeader, payload.MessageType, (uint)payloadBytes.Length, flags);
        return new MessageFrame(header, payloadBytes);
    }

    /// <summary>
    /// Creates a frame with raw payload bytes.
    /// </summary>
    public static MessageFrame Create(MessageType type, byte[] payload, MessageFlags flags = MessageFlags.None)
    {
        var header = MessageHeader.Create(type, (uint)payload.Length, flags);
        return new MessageFrame(header, payload);
    }

    /// <summary>
    /// Parses a frame from wire bytes (header must already be parsed).
    /// </summary>
    public static MessageFrame FromParts(MessageHeader header, byte[] payload)
    {
        return new MessageFrame(header, payload);
    }

    /// <summary>
    /// Serializes the complete frame to bytes.
    /// </summary>
    public byte[] ToBytes()
    {
        var headerBytes = Header.ToBytes();
        var result = new byte[headerBytes.Length + Payload.Length];
        headerBytes.CopyTo(result, 0);
        Payload.CopyTo(result, headerBytes.Length);
        return result;
    }

    /// <summary>
    /// Gets the total size of this frame.
    /// </summary>
    public int TotalSize => MessageHeader.Size + Payload.Length;
}

/// <summary>
/// Helper for reading message frames from a stream.
/// </summary>
public sealed class MessageReader
{
    private static readonly Logger _log = Logger.For<MessageReader>();
    private readonly Stream _stream;
    private readonly byte[] _headerBuffer = new byte[MessageHeader.Size];

    public MessageReader(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Reads the next message frame from the stream.
    /// </summary>
    public async Task<MessageFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        // Read header
        var bytesRead = await ReadExactlyAsync(_headerBuffer, MessageHeader.Size, cancellationToken);
        if (bytesRead < MessageHeader.Size)
            return null;

        var header = MessageHeader.FromBytes(_headerBuffer);

        // Validate protocol version
        if (!ProtocolVersion.IsCompatible(header.Version))
            throw new InvalidOperationException($"Incompatible protocol version: {header.Version}");

        // Read payload
        var payload = new byte[header.PayloadLength];
        if (header.PayloadLength > 0)
        {
            bytesRead = await ReadExactlyAsync(payload, (int)header.PayloadLength, cancellationToken);
            if (bytesRead < header.PayloadLength)
            {
                _log.Warn("Partial frame read: expected {0} bytes, got {1} (type={2})",
                    header.PayloadLength, bytesRead, header.Type);
                return null;
            }
        }

        return MessageFrame.FromParts(header, payload);
    }

    private async Task<int> ReadExactlyAsync(byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), cancellationToken);
            if (read == 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }
}

/// <summary>
/// Helper for writing message frames to a stream.
/// </summary>
public sealed class MessageWriter
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public MessageWriter(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Writes a message frame to the stream.
    /// </summary>
    public async Task WriteFrameAsync(MessageFrame frame, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var bytes = frame.ToBytes();
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a payload as a frame to the stream.
    /// </summary>
    public async Task WriteAsync(IMessagePayload payload, MessageFlags flags = MessageFlags.None, CancellationToken cancellationToken = default)
    {
        var frame = MessageFrame.Create(payload, flags);
        await WriteFrameAsync(frame, cancellationToken);
    }
}
