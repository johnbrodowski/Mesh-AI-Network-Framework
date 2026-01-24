using MeshAI.Core.Crypto;

namespace MeshAI.Transfer;

/// <summary>
/// Represents a file chunk ready for transfer.
/// </summary>
public sealed class FileChunk
{
    public required int Index { get; init; }
    public required byte[] Data { get; init; }
    public required byte[] Hash { get; init; }
    public required bool IsLast { get; init; }

    /// <summary>
    /// Verifies the chunk data against its hash.
    /// </summary>
    public bool Verify()
    {
        return ChunkHasher.VerifyHash(Data, Hash);
    }
}

/// <summary>
/// Handles file chunking for transfer.
/// </summary>
public sealed class FileChunker : IDisposable
{
    private readonly Stream _stream;
    private readonly int _chunkSize;
    private readonly long _totalSize;
    private readonly int _totalChunks;
    private readonly byte[]? _encryptionKey;

    private int _currentChunk;
    private bool _disposed;

    /// <summary>
    /// Total file size in bytes.
    /// </summary>
    public long TotalSize => _totalSize;

    /// <summary>
    /// Chunk size in bytes.
    /// </summary>
    public int ChunkSize => _chunkSize;

    /// <summary>
    /// Total number of chunks.
    /// </summary>
    public int TotalChunks => _totalChunks;

    /// <summary>
    /// Current chunk index.
    /// </summary>
    public int CurrentChunk => _currentChunk;

    /// <summary>
    /// Progress as percentage (0-100).
    /// </summary>
    public double Progress => _totalChunks > 0 ? (_currentChunk * 100.0) / _totalChunks : 0;

    /// <summary>
    /// Creates a file chunker from a file path.
    /// </summary>
    public FileChunker(string filePath, int chunkSize = 64 * 1024, byte[]? encryptionKey = null)
        : this(File.OpenRead(filePath), chunkSize, encryptionKey)
    {
    }

    /// <summary>
    /// Creates a file chunker from a stream.
    /// </summary>
    public FileChunker(Stream stream, int chunkSize = 64 * 1024, byte[]? encryptionKey = null)
    {
        _stream = stream;
        _chunkSize = chunkSize;
        _encryptionKey = encryptionKey;
        _totalSize = stream.Length;
        _totalChunks = (int)Math.Ceiling((double)_totalSize / _chunkSize);
    }

    /// <summary>
    /// Reads the next chunk.
    /// </summary>
    public FileChunk? ReadNextChunk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_currentChunk >= _totalChunks)
            return null;

        var buffer = new byte[_chunkSize];
        var bytesRead = _stream.Read(buffer, 0, _chunkSize);

        if (bytesRead == 0)
            return null;

        var data = bytesRead < _chunkSize
            ? buffer[..bytesRead]
            : buffer;

        // Encrypt if key provided
        if (_encryptionKey != null)
        {
            data = PayloadEncryption.Encrypt(data, _encryptionKey);
        }

        var hash = ChunkHasher.ComputeHash(data);
        var isLast = _currentChunk == _totalChunks - 1;

        var chunk = new FileChunk
        {
            Index = _currentChunk,
            Data = data,
            Hash = hash,
            IsLast = isLast
        };

        _currentChunk++;
        return chunk;
    }

    /// <summary>
    /// Reads a specific chunk by index.
    /// </summary>
    public FileChunk? ReadChunk(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (index < 0 || index >= _totalChunks)
            return null;

        _stream.Seek((long)index * _chunkSize, SeekOrigin.Begin);
        _currentChunk = index;

        return ReadNextChunk();
    }

    /// <summary>
    /// Computes the hash of the entire file.
    /// </summary>
    public byte[] ComputeFileHash()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var position = _stream.Position;
        _stream.Seek(0, SeekOrigin.Begin);

        using var incrementalHash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);

        var buffer = new byte[_chunkSize];
        int bytesRead;

        while ((bytesRead = _stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            incrementalHash.AppendData(buffer, 0, bytesRead);
        }

        _stream.Position = position;
        return incrementalHash.GetCurrentHash();
    }

    /// <summary>
    /// Resets to the beginning of the file.
    /// </summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Seek(0, SeekOrigin.Begin);
        _currentChunk = 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Assembles file chunks back into a complete file.
/// </summary>
public sealed class FileAssembler : IDisposable
{
    private readonly Stream _stream;
    private readonly int _totalChunks;
    private readonly byte[]? _decryptionKey;
    private readonly HashSet<int> _receivedChunks = [];
    private readonly List<byte[]> _chunkHashes = [];

    private bool _disposed;

    /// <summary>
    /// Total number of expected chunks.
    /// </summary>
    public int TotalChunks => _totalChunks;

    /// <summary>
    /// Number of chunks received.
    /// </summary>
    public int ReceivedChunks => _receivedChunks.Count;

    /// <summary>
    /// Whether all chunks have been received.
    /// </summary>
    public bool IsComplete => _receivedChunks.Count == _totalChunks;

    /// <summary>
    /// Progress as percentage (0-100).
    /// </summary>
    public double Progress => _totalChunks > 0 ? (_receivedChunks.Count * 100.0) / _totalChunks : 0;

    /// <summary>
    /// Missing chunk indices.
    /// </summary>
    public IEnumerable<int> MissingChunks =>
        Enumerable.Range(0, _totalChunks).Where(i => !_receivedChunks.Contains(i));

    /// <summary>
    /// Creates a file assembler for a new file.
    /// </summary>
    public FileAssembler(string filePath, int totalChunks, int chunkSize, byte[]? decryptionKey = null)
        : this(File.Create(filePath), totalChunks, chunkSize, decryptionKey)
    {
    }

    /// <summary>
    /// Creates a file assembler for a stream.
    /// </summary>
    public FileAssembler(Stream stream, int totalChunks, int chunkSize, byte[]? decryptionKey = null)
    {
        _stream = stream;
        _totalChunks = totalChunks;
        _decryptionKey = decryptionKey;

        // Pre-allocate space for chunk hashes
        for (var i = 0; i < totalChunks; i++)
        {
            _chunkHashes.Add([]);
        }
    }

    /// <summary>
    /// Writes a chunk to the file.
    /// </summary>
    public bool WriteChunk(FileChunk chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (chunk.Index < 0 || chunk.Index >= _totalChunks)
            return false;

        // Verify chunk integrity
        if (!chunk.Verify())
            return false;

        var data = chunk.Data;

        // Decrypt if key provided
        if (_decryptionKey != null)
        {
            try
            {
                data = PayloadEncryption.Decrypt(data, _decryptionKey);
            }
            catch
            {
                return false; // Decryption failed
            }
        }

        // Seek to correct position and write
        // For encrypted chunks, we need to track where each chunk ends
        // For simplicity, we'll write sequentially and require chunks in order
        // or buffer out-of-order chunks

        lock (_stream)
        {
            // Simple approach: require chunks in order
            if (chunk.Index == _receivedChunks.Count)
            {
                _stream.Write(data);
                _receivedChunks.Add(chunk.Index);
                _chunkHashes[chunk.Index] = chunk.Hash;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies the assembled file against the expected hash.
    /// </summary>
    public bool VerifyFile(byte[] expectedHash)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stream.Seek(0, SeekOrigin.Begin);

        using var incrementalHash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);

        var buffer = new byte[64 * 1024];
        int bytesRead;

        while ((bytesRead = _stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            incrementalHash.AppendData(buffer, 0, bytesRead);
        }

        var actualHash = incrementalHash.GetCurrentHash();
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    /// <summary>
    /// Gets the Merkle root of received chunks.
    /// </summary>
    public byte[] GetMerkleRoot()
    {
        return ChunkHasher.ComputeMerkleRoot(_chunkHashes);
    }

    /// <summary>
    /// Flushes the stream.
    /// </summary>
    public void Flush()
    {
        _stream.Flush();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream.Dispose();
            _disposed = true;
        }
    }
}
