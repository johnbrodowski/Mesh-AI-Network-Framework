using MeshAI.Core.Crypto;
using MeshAI.Network.Protocol;
using MeshAI.Transfer;
using Xunit;

namespace MeshAI.Tests.Unit;

/// <summary>
/// Unit tests for file transfer components.
/// </summary>
public class TransferTests : IDisposable
{
    private readonly string _testDir;

    public TransferTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"MeshAI_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    private string CreateTestFile(string name, int sizeBytes)
    {
        var path = Path.Combine(_testDir, name);
        var data = new byte[sizeBytes];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void FileChunker_CalculatesChunksCorrectly()
    {
        // Arrange
        var filePath = CreateTestFile("test_chunks.bin", 1000);

        // Act
        using var chunker = new FileChunker(filePath, chunkSize: 300);

        // Assert
        Assert.Equal(1000, chunker.TotalSize);
        Assert.Equal(300, chunker.ChunkSize);
        Assert.Equal(4, chunker.TotalChunks); // ceil(1000/300) = 4
    }

    [Fact]
    public void FileChunker_ReadsAllChunks()
    {
        // Arrange
        var filePath = CreateTestFile("test_read.bin", 500);
        using var chunker = new FileChunker(filePath, chunkSize: 200);

        // Act
        var chunks = new List<FileChunk>();
        FileChunk? chunk;
        while ((chunk = chunker.ReadNextChunk()) != null)
        {
            chunks.Add(chunk);
        }

        // Assert
        Assert.Equal(3, chunks.Count);
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(1, chunks[1].Index);
        Assert.Equal(2, chunks[2].Index);
        Assert.True(chunks[2].IsLast);
        Assert.False(chunks[0].IsLast);
    }

    [Fact]
    public void FileChunker_ChunksHaveCorrectHashes()
    {
        // Arrange
        var filePath = CreateTestFile("test_hash.bin", 400);
        using var chunker = new FileChunker(filePath, chunkSize: 200);

        // Act
        var chunk = chunker.ReadNextChunk();

        // Assert
        Assert.NotNull(chunk);
        Assert.True(chunk.Verify());
    }

    [Fact]
    public void FileChunker_ReadSpecificChunk()
    {
        // Arrange
        var filePath = CreateTestFile("test_specific.bin", 600);
        using var chunker = new FileChunker(filePath, chunkSize: 200);

        // Act
        var chunk2 = chunker.ReadChunk(2);

        // Assert
        Assert.NotNull(chunk2);
        Assert.Equal(2, chunk2.Index);
    }

    [Fact]
    public void FileChunker_ComputesFileHash()
    {
        // Arrange
        var filePath = CreateTestFile("test_filehash.bin", 300);
        using var chunker = new FileChunker(filePath, chunkSize: 100);

        // Act
        var hash = chunker.ComputeFileHash();

        // Assert
        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length); // SHA-256
    }

    [Fact]
    public void FileChunker_ReportsProgress()
    {
        // Arrange
        var filePath = CreateTestFile("test_progress.bin", 400);
        using var chunker = new FileChunker(filePath, chunkSize: 100);

        // Act & Assert
        Assert.Equal(0, chunker.Progress);

        chunker.ReadNextChunk();
        Assert.Equal(25, chunker.Progress);

        chunker.ReadNextChunk();
        Assert.Equal(50, chunker.Progress);

        chunker.ReadNextChunk();
        Assert.Equal(75, chunker.Progress);

        chunker.ReadNextChunk();
        Assert.Equal(100, chunker.Progress);
    }

    [Fact]
    public void FileChunker_WithEncryption_EncryptsData()
    {
        // Arrange
        var filePath = CreateTestFile("test_encrypt.bin", 200);
        var key = PayloadEncryption.GenerateKey();

        // Read without encryption
        using var plainChunker = new FileChunker(filePath, chunkSize: 100);
        var plainChunk = plainChunker.ReadNextChunk()!;

        // Read with encryption
        using var encryptedChunker = new FileChunker(filePath, chunkSize: 100, encryptionKey: key);
        var encryptedChunk = encryptedChunker.ReadNextChunk()!;

        // Assert - Encrypted chunk should be different (and larger due to nonce/tag)
        Assert.NotEqual(plainChunk.Data, encryptedChunk.Data);
        Assert.True(encryptedChunk.Data.Length > plainChunk.Data.Length);
    }

    [Fact]
    public void FileAssembler_WritesChunksSequentially()
    {
        // Arrange
        var outputPath = Path.Combine(_testDir, "output.bin");
        using var assembler = new FileAssembler(outputPath, totalChunks: 3, chunkSize: 100);

        var chunk0 = new FileChunk { Index = 0, Data = new byte[100], Hash = ChunkHasher.ComputeHash(new byte[100]), IsLast = false };
        var chunk1 = new FileChunk { Index = 1, Data = new byte[100], Hash = ChunkHasher.ComputeHash(new byte[100]), IsLast = false };
        var chunk2 = new FileChunk { Index = 2, Data = new byte[50], Hash = ChunkHasher.ComputeHash(new byte[50]), IsLast = true };

        // Act
        Assert.True(assembler.WriteChunk(chunk0));
        Assert.True(assembler.WriteChunk(chunk1));
        Assert.True(assembler.WriteChunk(chunk2));

        // Assert
        Assert.True(assembler.IsComplete);
        Assert.Equal(3, assembler.ReceivedChunks);
        Assert.Equal(100, assembler.Progress);
    }

    [Fact]
    public void FileAssembler_ReportsProgress()
    {
        // Arrange
        var outputPath = Path.Combine(_testDir, "progress.bin");
        using var assembler = new FileAssembler(outputPath, totalChunks: 4, chunkSize: 100);

        // Act & Assert
        Assert.Equal(0, assembler.Progress);

        var data = new byte[100];
        var hash = ChunkHasher.ComputeHash(data);
        assembler.WriteChunk(new FileChunk { Index = 0, Data = data, Hash = hash, IsLast = false });
        Assert.Equal(25, assembler.Progress);

        assembler.WriteChunk(new FileChunk { Index = 1, Data = data, Hash = hash, IsLast = false });
        Assert.Equal(50, assembler.Progress);
    }

    [Fact]
    public void FileAssembler_RejectsBadChunkHash()
    {
        // Arrange
        var outputPath = Path.Combine(_testDir, "badhash.bin");
        using var assembler = new FileAssembler(outputPath, totalChunks: 2, chunkSize: 100);

        var data = new byte[100];
        var wrongHash = new byte[32]; // Wrong hash

        // Act
        var result = assembler.WriteChunk(new FileChunk { Index = 0, Data = data, Hash = wrongHash, IsLast = false });

        // Assert
        Assert.False(result);
        Assert.Equal(0, assembler.ReceivedChunks);
    }

    [Fact]
    public void FileAssembler_TracksMissingChunks()
    {
        // Arrange
        var outputPath = Path.Combine(_testDir, "missing.bin");
        using var assembler = new FileAssembler(outputPath, totalChunks: 5, chunkSize: 100);

        var data = new byte[100];
        var hash = ChunkHasher.ComputeHash(data);

        // Only write chunks 0, 1, 3 (skip 2 and 4)
        assembler.WriteChunk(new FileChunk { Index = 0, Data = data, Hash = hash, IsLast = false });
        assembler.WriteChunk(new FileChunk { Index = 1, Data = data, Hash = hash, IsLast = false });

        // Act
        var missing = assembler.MissingChunks.ToList();

        // Assert
        Assert.Contains(2, missing);
        Assert.Contains(3, missing);
        Assert.Contains(4, missing);
    }

    [Fact]
    public void TransferInitMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new TransferInitMessage
        {
            TransferId = "transfer123",
            SourceClientId = "src" + new string('0', 61),
            TargetClientId = "tgt" + new string('1', 61),
            FileName = "test_file.pdf",
            FileSize = 1024000,
            ChunkSize = 65536,
            TotalChunks = 16,
            FileHash = new byte[32],
            TargetHardwareHash = "hw" + new string('a', 62),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds()
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = TransferInitMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.TransferId, parsed.TransferId);
        Assert.Equal(original.SourceClientId, parsed.SourceClientId);
        Assert.Equal(original.TargetClientId, parsed.TargetClientId);
        Assert.Equal(original.FileName, parsed.FileName);
        Assert.Equal(original.FileSize, parsed.FileSize);
        Assert.Equal(original.ChunkSize, parsed.ChunkSize);
        Assert.Equal(original.TotalChunks, parsed.TotalChunks);
        Assert.Equal(original.FileHash, parsed.FileHash);
        Assert.Equal(original.TargetHardwareHash, parsed.TargetHardwareHash);
        Assert.Equal(original.ExpiresAt, parsed.ExpiresAt);
    }

    [Fact]
    public void TransferChunkMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var chunkData = new byte[1000];
        Random.Shared.NextBytes(chunkData);

        var original = new TransferChunkMessage
        {
            TransferId = "xfer456",
            ChunkIndex = 7,
            ChunkHash = ChunkHasher.ComputeHash(chunkData),
            Data = chunkData
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = TransferChunkMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.TransferId, parsed.TransferId);
        Assert.Equal(original.ChunkIndex, parsed.ChunkIndex);
        Assert.Equal(original.ChunkHash, parsed.ChunkHash);
        Assert.Equal(original.Data, parsed.Data);
    }

    [Fact]
    public void TransferAcceptMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new TransferAcceptMessage
        {
            TransferId = "accept789",
            StartChunk = 5
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = TransferAcceptMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.TransferId, parsed.TransferId);
        Assert.Equal(original.StartChunk, parsed.StartChunk);
    }

    [Fact]
    public void TransferCompleteMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new TransferCompleteMessage
        {
            TransferId = "complete123",
            Success = true,
            FinalHash = new byte[32]
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = TransferCompleteMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.TransferId, parsed.TransferId);
        Assert.Equal(original.Success, parsed.Success);
        Assert.Equal(original.FinalHash, parsed.FinalHash);
    }

    [Fact]
    public void TransferCancelMessage_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var original = new TransferCancelMessage
        {
            TransferId = "cancel456",
            Reason = "User cancelled the transfer"
        };

        // Act
        var bytes = original.ToBytes();
        var parsed = TransferCancelMessage.FromBytes(bytes);

        // Assert
        Assert.Equal(original.TransferId, parsed.TransferId);
        Assert.Equal(original.Reason, parsed.Reason);
    }

    [Fact]
    public void FileChunker_Reset_AllowsReReading()
    {
        // Arrange
        var filePath = CreateTestFile("test_reset.bin", 300);
        using var chunker = new FileChunker(filePath, chunkSize: 100);

        // Read all chunks first
        while (chunker.ReadNextChunk() != null) { }
        Assert.Equal(100, chunker.Progress);

        // Act - Reset
        chunker.Reset();

        // Assert
        Assert.Equal(0, chunker.CurrentChunk);
        var chunk = chunker.ReadNextChunk();
        Assert.NotNull(chunk);
        Assert.Equal(0, chunk.Index);
    }
}
