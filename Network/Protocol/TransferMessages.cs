using System.Buffers.Binary;
using System.Text;

namespace MeshAI.Network.Protocol;

/// <summary>
/// File transfer initiation message.
/// </summary>
public sealed class TransferInitMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.TransferInit;

    /// <summary>
    /// Unique transfer identifier.
    /// </summary>
    public required string TransferId { get; init; }

    /// <summary>
    /// Source client initiating the transfer.
    /// </summary>
    public required string SourceClientId { get; init; }

    /// <summary>
    /// Target client receiving the file.
    /// </summary>
    public required string TargetClientId { get; init; }

    /// <summary>
    /// Original filename.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Total file size in bytes.
    /// </summary>
    public required long FileSize { get; init; }

    /// <summary>
    /// Size of each chunk in bytes.
    /// </summary>
    public required int ChunkSize { get; init; }

    /// <summary>
    /// Total number of chunks.
    /// </summary>
    public required int TotalChunks { get; init; }

    /// <summary>
    /// Hash of the complete file for verification.
    /// </summary>
    public required byte[] FileHash { get; init; }

    /// <summary>
    /// Hardware hash for binding (if hardware-bound transfer).
    /// </summary>
    public string? TargetHardwareHash { get; init; }

    /// <summary>
    /// Optional expiration timestamp (Unix ms).
    /// </summary>
    public long? ExpiresAt { get; init; }

    public byte[] ToBytes()
    {
        var transferIdBytes = Encoding.ASCII.GetBytes(TransferId);
        var sourceBytes = Encoding.ASCII.GetBytes(SourceClientId);
        var targetBytes = Encoding.ASCII.GetBytes(TargetClientId);
        var fileNameBytes = Encoding.UTF8.GetBytes(FileName);
        var hwHashBytes = TargetHardwareHash != null ? Encoding.ASCII.GetBytes(TargetHardwareHash) : [];

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)transferIdBytes.Length);
        writer.Write(transferIdBytes);

        writer.Write((byte)sourceBytes.Length);
        writer.Write(sourceBytes);

        writer.Write((byte)targetBytes.Length);
        writer.Write(targetBytes);

        writer.Write((ushort)fileNameBytes.Length);
        writer.Write(fileNameBytes);

        writer.Write(FileSize);
        writer.Write(ChunkSize);
        writer.Write(TotalChunks);

        writer.Write((byte)FileHash.Length);
        writer.Write(FileHash);

        writer.Write((byte)hwHashBytes.Length);
        if (hwHashBytes.Length > 0)
            writer.Write(hwHashBytes);

        writer.Write(ExpiresAt ?? 0L);

        return ms.ToArray();
    }

    public static TransferInitMessage FromBytes(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var reader = new BinaryReader(ms);

        var transferIdLength = reader.ReadByte();
        var transferId = Encoding.ASCII.GetString(reader.ReadBytes(transferIdLength));

        var sourceLength = reader.ReadByte();
        var source = Encoding.ASCII.GetString(reader.ReadBytes(sourceLength));

        var targetLength = reader.ReadByte();
        var target = Encoding.ASCII.GetString(reader.ReadBytes(targetLength));

        var fileNameLength = reader.ReadUInt16();
        var fileName = Encoding.UTF8.GetString(reader.ReadBytes(fileNameLength));

        var fileSize = reader.ReadInt64();
        var chunkSize = reader.ReadInt32();
        var totalChunks = reader.ReadInt32();

        var hashLength = reader.ReadByte();
        var fileHash = reader.ReadBytes(hashLength);

        var hwHashLength = reader.ReadByte();
        var hwHash = hwHashLength > 0 ? Encoding.ASCII.GetString(reader.ReadBytes(hwHashLength)) : null;

        var expiresAt = reader.ReadInt64();

        return new TransferInitMessage
        {
            TransferId = transferId,
            SourceClientId = source,
            TargetClientId = target,
            FileName = fileName,
            FileSize = fileSize,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks,
            FileHash = fileHash,
            TargetHardwareHash = hwHash,
            ExpiresAt = expiresAt == 0 ? null : expiresAt
        };
    }
}

/// <summary>
/// File transfer acceptance.
/// </summary>
public sealed class TransferAcceptMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.TransferAccept;

    public required string TransferId { get; init; }

    /// <summary>
    /// Starting chunk index (for resume support).
    /// </summary>
    public int StartChunk { get; init; }

    public byte[] ToBytes()
    {
        var transferIdBytes = Encoding.ASCII.GetBytes(TransferId);
        var buffer = new byte[1 + transferIdBytes.Length + 4];

        buffer[0] = (byte)transferIdBytes.Length;
        transferIdBytes.CopyTo(buffer.AsSpan(1));
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1 + transferIdBytes.Length), StartChunk);

        return buffer;
    }

    public static TransferAcceptMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var transferIdLength = data[0];
        var transferId = Encoding.ASCII.GetString(data.Slice(1, transferIdLength));
        var startChunk = BinaryPrimitives.ReadInt32BigEndian(data.Slice(1 + transferIdLength, 4));

        return new TransferAcceptMessage
        {
            TransferId = transferId,
            StartChunk = startChunk
        };
    }
}

/// <summary>
/// File transfer rejection.
/// </summary>
public sealed class TransferRejectMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.TransferReject;

    public required string TransferId { get; init; }
    public required string Reason { get; init; }

    public byte[] ToBytes()
    {
        var transferIdBytes = Encoding.ASCII.GetBytes(TransferId);
        var reasonBytes = Encoding.UTF8.GetBytes(Reason);

        var buffer = new byte[1 + transferIdBytes.Length + 2 + reasonBytes.Length];
        var offset = 0;

        buffer[offset++] = (byte)transferIdBytes.Length;
        transferIdBytes.CopyTo(buffer.AsSpan(offset));
        offset += transferIdBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)reasonBytes.Length);
        offset += 2;
        reasonBytes.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static TransferRejectMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var transferIdLength = data[offset++];
        var transferId = Encoding.ASCII.GetString(data.Slice(offset, transferIdLength));
        offset += transferIdLength;

        var reasonLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var reason = Encoding.UTF8.GetString(data.Slice(offset, reasonLength));

        return new TransferRejectMessage
        {
            TransferId = transferId,
            Reason = reason
        };
    }
}

/// <summary>
/// File chunk data.
/// </summary>
public sealed class TransferChunkMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.TransferChunk;

    public required string TransferId { get; init; }
    public required int ChunkIndex { get; init; }
    public required byte[] ChunkHash { get; init; }
    public required byte[] Data { get; init; }

    public byte[] ToBytes()
    {
        var transferIdBytes = Encoding.ASCII.GetBytes(TransferId);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)transferIdBytes.Length);
        writer.Write(transferIdBytes);
        writer.Write(ChunkIndex);
        writer.Write((byte)ChunkHash.Length);
        writer.Write(ChunkHash);
        writer.Write(Data.Length);
        writer.Write(Data);

        return ms.ToArray();
    }

    public static TransferChunkMessage FromBytes(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var reader = new BinaryReader(ms);

        var transferIdLength = reader.ReadByte();
        var transferId = Encoding.ASCII.GetString(reader.ReadBytes(transferIdLength));
        var chunkIndex = reader.ReadInt32();
        var hashLength = reader.ReadByte();
        var chunkHash = reader.ReadBytes(hashLength);
        var dataLength = reader.ReadInt32();
        var chunkData = reader.ReadBytes(dataLength);

        return new TransferChunkMessage
        {
            TransferId = transferId,
            ChunkIndex = chunkIndex,
            ChunkHash = chunkHash,
            Data = chunkData
        };
    }
}

/// <summary>
/// Chunk acknowledgment.
/// </summary>
public sealed class TransferChunkAckMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.TransferChunkAck;

    public required string TransferId { get; init; }
    public required int ChunkIndex { get; init; }
    public required bool Success { get; init; }

    public byte[] ToBytes()
    {
        var transferIdBytes = Encoding.ASCII.GetBytes(TransferId);
        var buffer = new byte[1 + transferIdBytes.Length + 4 + 1];

        buffer[0] = (byte)transferIdBytes.Length;
        transferIdBytes.CopyTo(buffer.AsSpan(1));
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1 + transferIdBytes.Length), ChunkIndex);
        buffer[^1] = Success ? (byte)1 : (byte)0;

        return buffer;
    }

    public static TransferChunkAckMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var transferIdLength = data[0];
        var transferId = Encoding.ASCII.GetString(data.Slice(1, transferIdLength));
        var chunkIndex = BinaryPrimitives.ReadInt32BigEndian(data.Slice(1 + transferIdLength, 4));
        var success = data[^1] != 0;

        return new TransferChunkAckMessage
        {
            TransferId = transferId,
            ChunkIndex = chunkIndex,
            Success = success
        };
    }
}

/// <summary>
/// Transfer completion notification.
/// </summary>
public sealed class TransferCompleteMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.TransferComplete;

    public required string TransferId { get; init; }
    public required bool Success { get; init; }
    public required byte[] FinalHash { get; init; }

    public byte[] ToBytes()
    {
        var transferIdBytes = Encoding.ASCII.GetBytes(TransferId);
        var buffer = new byte[1 + transferIdBytes.Length + 1 + 1 + FinalHash.Length];
        var offset = 0;

        buffer[offset++] = (byte)transferIdBytes.Length;
        transferIdBytes.CopyTo(buffer.AsSpan(offset));
        offset += transferIdBytes.Length;

        buffer[offset++] = Success ? (byte)1 : (byte)0;
        buffer[offset++] = (byte)FinalHash.Length;
        FinalHash.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static TransferCompleteMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var transferIdLength = data[offset++];
        var transferId = Encoding.ASCII.GetString(data.Slice(offset, transferIdLength));
        offset += transferIdLength;

        var success = data[offset++] != 0;
        var hashLength = data[offset++];
        var finalHash = data.Slice(offset, hashLength).ToArray();

        return new TransferCompleteMessage
        {
            TransferId = transferId,
            Success = success,
            FinalHash = finalHash
        };
    }
}

/// <summary>
/// Transfer cancellation.
/// </summary>
public sealed class TransferCancelMessage : IMessagePayload
{
    public MessageType MessageType => MessageType.TransferCancel;

    public required string TransferId { get; init; }
    public required string Reason { get; init; }

    public byte[] ToBytes()
    {
        var transferIdBytes = Encoding.ASCII.GetBytes(TransferId);
        var reasonBytes = Encoding.UTF8.GetBytes(Reason);

        var buffer = new byte[1 + transferIdBytes.Length + 2 + reasonBytes.Length];
        var offset = 0;

        buffer[offset++] = (byte)transferIdBytes.Length;
        transferIdBytes.CopyTo(buffer.AsSpan(offset));
        offset += transferIdBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)reasonBytes.Length);
        offset += 2;
        reasonBytes.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static TransferCancelMessage FromBytes(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var transferIdLength = data[offset++];
        var transferId = Encoding.ASCII.GetString(data.Slice(offset, transferIdLength));
        offset += transferIdLength;

        var reasonLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var reason = Encoding.UTF8.GetString(data.Slice(offset, reasonLength));

        return new TransferCancelMessage
        {
            TransferId = transferId,
            Reason = reason
        };
    }
}
