using System.Collections.Concurrent;
using System.Security.Cryptography;
using MeshAI.Core.Crypto;
using MeshAI.Core.Identity;
using MeshAI.Core.Logging;
using MeshAI.Network.Protocol;

namespace MeshAI.Transfer;

/// <summary>
/// State of a file transfer.
/// </summary>
public enum TransferState
{
    Pending,
    Accepted,
    InProgress,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Direction of transfer relative to this node.
/// </summary>
public enum TransferDirection
{
    Outbound,
    Inbound
}

/// <summary>
/// Represents an active file transfer.
/// </summary>
public sealed class FileTransfer : IDisposable
{
    private readonly Logger _log = Logger.For<FileTransfer>();

    public string TransferId { get; }
    public TransferDirection Direction { get; }
    public string LocalClientId { get; }
    public string RemoteClientId { get; }
    public string FileName { get; }
    public long FileSize { get; }
    public int ChunkSize { get; }
    public int TotalChunks { get; }
    public byte[] FileHash { get; }
    public string? TargetHardwareHash { get; }
    public DateTime? ExpiresAt { get; }

    public TransferState State { get; private set; } = TransferState.Pending;
    public int TransferredChunks { get; private set; }
    public long TransferredBytes { get; private set; }
    public double Progress => TotalChunks > 0 ? (TransferredChunks * 100.0) / TotalChunks : 0;

    public DateTime StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? ErrorMessage { get; private set; }

    private FileChunker? _chunker;
    private FileAssembler? _assembler;
    private byte[]? _encryptionKey;
    private string? _tempFilePath;
    private string? _finalFilePath;

    private bool _disposed;

    /// <summary>
    /// Event raised when transfer state changes.
    /// </summary>
    public event Action<FileTransfer, TransferState>? StateChanged;

    /// <summary>
    /// Event raised when a chunk is transferred.
    /// </summary>
    public event Action<FileTransfer, int>? ChunkTransferred;

    private FileTransfer(
        string transferId,
        TransferDirection direction,
        string localClientId,
        string remoteClientId,
        string fileName,
        long fileSize,
        int chunkSize,
        int totalChunks,
        byte[] fileHash,
        string? targetHardwareHash,
        DateTime? expiresAt)
    {
        TransferId = transferId;
        Direction = direction;
        LocalClientId = localClientId;
        RemoteClientId = remoteClientId;
        FileName = fileName;
        FileSize = fileSize;
        ChunkSize = chunkSize;
        TotalChunks = totalChunks;
        FileHash = fileHash;
        TargetHardwareHash = targetHardwareHash;
        ExpiresAt = expiresAt;
    }

    /// <summary>
    /// Creates an outbound transfer.
    /// </summary>
    public static FileTransfer CreateOutbound(
        string localClientId,
        string targetClientId,
        string filePath,
        int chunkSize = 64 * 1024,
        string? targetHardwareHash = null,
        TimeSpan? expiration = null)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("File not found", filePath);

        var totalChunks = (int)Math.Ceiling((double)fileInfo.Length / chunkSize);
        var transferId = GenerateTransferId();

        // Generate encryption key
        var encryptionKey = PayloadEncryption.GenerateKey();

        // Compute file hash
        using var stream = File.OpenRead(filePath);
        var fileHash = SHA256.HashData(stream);
        stream.Position = 0;

        var transfer = new FileTransfer(
            transferId,
            TransferDirection.Outbound,
            localClientId,
            targetClientId,
            fileInfo.Name,
            fileInfo.Length,
            chunkSize,
            totalChunks,
            fileHash,
            targetHardwareHash,
            expiration.HasValue ? DateTime.UtcNow + expiration.Value : null);

        transfer._encryptionKey = encryptionKey;
        transfer._chunker = new FileChunker(stream, chunkSize, encryptionKey);

        return transfer;
    }

    /// <summary>
    /// Creates an inbound transfer from an init message.
    /// </summary>
    public static FileTransfer CreateInbound(
        TransferInitMessage initMessage,
        string downloadDirectory)
    {
        var finalPath = Path.Combine(downloadDirectory, initMessage.FileName);
        var tempPath = finalPath + ".partial";

        var transfer = new FileTransfer(
            initMessage.TransferId,
            TransferDirection.Inbound,
            initMessage.TargetClientId,
            initMessage.SourceClientId,
            initMessage.FileName,
            initMessage.FileSize,
            initMessage.ChunkSize,
            initMessage.TotalChunks,
            initMessage.FileHash,
            initMessage.TargetHardwareHash,
            initMessage.ExpiresAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(initMessage.ExpiresAt.Value).UtcDateTime : null);

        transfer._tempFilePath = tempPath;
        transfer._finalFilePath = finalPath;

        return transfer;
    }

    /// <summary>
    /// Checks if this transfer can be used on the current hardware.
    /// </summary>
    public bool CheckHardwareBinding(ClientIdentity identity)
    {
        if (string.IsNullOrEmpty(TargetHardwareHash))
            return true;

        return identity.HardwareHash == TargetHardwareHash;
    }

    /// <summary>
    /// Accepts an inbound transfer and prepares to receive chunks.
    /// </summary>
    public void Accept(byte[]? decryptionKey = null)
    {
        if (Direction != TransferDirection.Inbound)
            throw new InvalidOperationException("Can only accept inbound transfers");

        if (State != TransferState.Pending)
            throw new InvalidOperationException($"Cannot accept transfer in state {State}");

        _encryptionKey = decryptionKey;
        _assembler = new FileAssembler(_tempFilePath!, TotalChunks, ChunkSize, decryptionKey);

        SetState(TransferState.Accepted);
        StartedAt = DateTime.UtcNow;
        _log.Info("Accepted transfer {0}: {1}", TransferId, FileName);
    }

    /// <summary>
    /// Rejects an inbound transfer.
    /// </summary>
    public void Reject(string reason)
    {
        ErrorMessage = reason;
        SetState(TransferState.Cancelled);
        _log.Info("Rejected transfer {0}: {1}", TransferId, reason);
    }

    /// <summary>
    /// Gets the next chunk to send (outbound only).
    /// </summary>
    public FileChunk? GetNextChunk()
    {
        if (Direction != TransferDirection.Outbound)
            throw new InvalidOperationException("Can only get chunks from outbound transfers");

        return _chunker?.ReadNextChunk();
    }

    /// <summary>
    /// Gets a specific chunk (outbound only).
    /// </summary>
    public FileChunk? GetChunk(int index)
    {
        if (Direction != TransferDirection.Outbound)
            throw new InvalidOperationException("Can only get chunks from outbound transfers");

        return _chunker?.ReadChunk(index);
    }

    /// <summary>
    /// Writes a received chunk (inbound only).
    /// </summary>
    public bool WriteChunk(TransferChunkMessage chunkMessage)
    {
        if (Direction != TransferDirection.Inbound)
            throw new InvalidOperationException("Can only write chunks to inbound transfers");

        if (_assembler == null)
            throw new InvalidOperationException("Transfer not accepted");

        var chunk = new FileChunk
        {
            Index = chunkMessage.ChunkIndex,
            Data = chunkMessage.Data,
            Hash = chunkMessage.ChunkHash,
            IsLast = chunkMessage.ChunkIndex == TotalChunks - 1
        };

        if (!_assembler.WriteChunk(chunk))
        {
            _log.Warn("Failed to write chunk {0}", chunkMessage.ChunkIndex);
            return false;
        }

        TransferredChunks++;
        TransferredBytes += chunk.Data.Length;
        ChunkTransferred?.Invoke(this, chunk.Index);

        if (State == TransferState.Accepted)
        {
            SetState(TransferState.InProgress);
        }

        return true;
    }

    /// <summary>
    /// Marks a chunk as acknowledged (outbound only).
    /// </summary>
    public void AcknowledgeChunk(int chunkIndex)
    {
        if (Direction != TransferDirection.Outbound)
            return;

        TransferredChunks++;
        TransferredBytes += ChunkSize; // Approximate

        if (State == TransferState.Accepted)
        {
            SetState(TransferState.InProgress);
        }

        ChunkTransferred?.Invoke(this, chunkIndex);
    }

    /// <summary>
    /// Starts the transfer.
    /// </summary>
    public void Start()
    {
        if (State != TransferState.Pending && State != TransferState.Accepted)
            throw new InvalidOperationException($"Cannot start transfer in state {State}");

        SetState(TransferState.InProgress);
        StartedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Completes the transfer.
    /// </summary>
    public bool Complete()
    {
        if (Direction == TransferDirection.Inbound && _assembler != null)
        {
            _assembler.Flush();

            // Verify file
            if (!_assembler.VerifyFile(FileHash))
            {
                ErrorMessage = "File hash verification failed";
                SetState(TransferState.Failed);
                return false;
            }

            // Move temp file to final location
            _assembler.Dispose();
            _assembler = null;

            if (File.Exists(_finalFilePath))
                File.Delete(_finalFilePath);

            File.Move(_tempFilePath!, _finalFilePath!);
        }

        CompletedAt = DateTime.UtcNow;
        SetState(TransferState.Completed);
        _log.Info("Transfer {0} completed: {1}", TransferId, FileName);
        return true;
    }

    /// <summary>
    /// Fails the transfer.
    /// </summary>
    public void Fail(string error)
    {
        ErrorMessage = error;
        SetState(TransferState.Failed);
        _log.Error("Transfer {0} failed: {1}", TransferId, error);

        // Clean up temp file
        if (_tempFilePath != null && File.Exists(_tempFilePath))
        {
            try { File.Delete(_tempFilePath); } catch { }
        }
    }

    /// <summary>
    /// Cancels the transfer.
    /// </summary>
    public void Cancel()
    {
        SetState(TransferState.Cancelled);
        _log.Info("Transfer {0} cancelled", TransferId);

        // Clean up temp file
        if (_tempFilePath != null && File.Exists(_tempFilePath))
        {
            try { File.Delete(_tempFilePath); } catch { }
        }
    }

    /// <summary>
    /// Creates the init message for this transfer.
    /// </summary>
    public TransferInitMessage CreateInitMessage()
    {
        if (Direction != TransferDirection.Outbound)
            throw new InvalidOperationException("Can only create init message for outbound transfers");

        return new TransferInitMessage
        {
            TransferId = TransferId,
            SourceClientId = LocalClientId,
            TargetClientId = RemoteClientId,
            FileName = FileName,
            FileSize = FileSize,
            ChunkSize = ChunkSize,
            TotalChunks = TotalChunks,
            FileHash = FileHash,
            TargetHardwareHash = TargetHardwareHash,
            ExpiresAt = ExpiresAt.HasValue ? new DateTimeOffset(ExpiresAt.Value).ToUnixTimeMilliseconds() : null
        };
    }

    private void SetState(TransferState newState)
    {
        if (State != newState)
        {
            var oldState = State;
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }

    private static string GenerateTransferId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _chunker?.Dispose();
            _assembler?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Manages file transfers.
/// </summary>
public sealed class TransferManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, FileTransfer> _transfers = new();
    private readonly Logger _log = Logger.For<TransferManager>();
    private readonly string _downloadDirectory;
    private readonly ClientIdentity _identity;
    private readonly int _maxConcurrent;

    private int _activeTransfers;

    /// <summary>
    /// Event raised when a new inbound transfer is requested.
    /// </summary>
    public event Func<FileTransfer, Task<bool>>? TransferRequested;

    /// <summary>
    /// Event raised when a transfer completes.
    /// </summary>
    public event Action<FileTransfer>? TransferCompleted;

    /// <summary>
    /// Event raised when a transfer fails.
    /// </summary>
    public event Action<FileTransfer, string>? TransferFailed;

    public TransferManager(ClientIdentity identity, string downloadDirectory, int maxConcurrent = 5)
    {
        _identity = identity;
        _downloadDirectory = downloadDirectory;
        _maxConcurrent = maxConcurrent;

        Directory.CreateDirectory(downloadDirectory);
    }

    /// <summary>
    /// Gets an active transfer by ID.
    /// </summary>
    public FileTransfer? GetTransfer(string transferId)
    {
        _transfers.TryGetValue(transferId, out var transfer);
        return transfer;
    }

    /// <summary>
    /// Gets all active transfers.
    /// </summary>
    public IEnumerable<FileTransfer> GetActiveTransfers() =>
        _transfers.Values.Where(t => t.State == TransferState.InProgress);

    /// <summary>
    /// Creates an outbound transfer.
    /// </summary>
    public FileTransfer CreateOutboundTransfer(
        string targetClientId,
        string filePath,
        bool hardwareBound = false)
    {
        var targetHardwareHash = hardwareBound ? targetClientId : null;

        var transfer = FileTransfer.CreateOutbound(
            _identity.ClientId,
            targetClientId,
            filePath,
            targetHardwareHash: targetHardwareHash);

        _transfers[transfer.TransferId] = transfer;
        transfer.StateChanged += OnTransferStateChanged;

        _log.Info("Created outbound transfer {0}: {1} -> {2}",
            transfer.TransferId, transfer.FileName, targetClientId[..16]);

        return transfer;
    }

    /// <summary>
    /// Handles an incoming transfer init message.
    /// </summary>
    public async Task<FileTransfer?> HandleTransferInitAsync(TransferInitMessage initMessage)
    {
        // Check concurrent transfer limit
        if (_activeTransfers >= _maxConcurrent)
        {
            _log.Warn("Rejecting transfer {0}: max concurrent transfers reached", initMessage.TransferId);
            return null;
        }

        // Create inbound transfer
        var transfer = FileTransfer.CreateInbound(initMessage, _downloadDirectory);
        _transfers[transfer.TransferId] = transfer;
        transfer.StateChanged += OnTransferStateChanged;

        // Check hardware binding
        if (!transfer.CheckHardwareBinding(_identity))
        {
            transfer.Reject("Hardware binding mismatch");
            return null;
        }

        // Ask application if it wants to accept
        if (TransferRequested != null)
        {
            var accepted = await TransferRequested.Invoke(transfer);
            if (!accepted)
            {
                transfer.Reject("Transfer rejected by user");
                return null;
            }
        }

        return transfer;
    }

    /// <summary>
    /// Handles a received chunk.
    /// </summary>
    public bool HandleChunk(TransferChunkMessage chunkMessage)
    {
        if (!_transfers.TryGetValue(chunkMessage.TransferId, out var transfer))
        {
            _log.Warn("Received chunk for unknown transfer {0}", chunkMessage.TransferId);
            return false;
        }

        return transfer.WriteChunk(chunkMessage);
    }

    /// <summary>
    /// Handles a chunk acknowledgment.
    /// </summary>
    public void HandleChunkAck(TransferChunkAckMessage ackMessage)
    {
        if (!_transfers.TryGetValue(ackMessage.TransferId, out var transfer))
            return;

        if (ackMessage.Success)
        {
            transfer.AcknowledgeChunk(ackMessage.ChunkIndex);
        }
    }

    /// <summary>
    /// Handles a transfer complete message.
    /// </summary>
    public void HandleTransferComplete(TransferCompleteMessage completeMessage)
    {
        if (!_transfers.TryGetValue(completeMessage.TransferId, out var transfer))
            return;

        if (completeMessage.Success)
        {
            transfer.Complete();
        }
        else
        {
            transfer.Fail("Remote side reported failure");
        }
    }

    /// <summary>
    /// Handles a transfer cancel message.
    /// </summary>
    public void HandleTransferCancel(TransferCancelMessage cancelMessage)
    {
        if (!_transfers.TryGetValue(cancelMessage.TransferId, out var transfer))
            return;

        transfer.Cancel();
    }

    private void OnTransferStateChanged(FileTransfer transfer, TransferState newState)
    {
        switch (newState)
        {
            case TransferState.InProgress:
                Interlocked.Increment(ref _activeTransfers);
                break;

            case TransferState.Completed:
                Interlocked.Decrement(ref _activeTransfers);
                TransferCompleted?.Invoke(transfer);
                break;

            case TransferState.Failed:
                Interlocked.Decrement(ref _activeTransfers);
                TransferFailed?.Invoke(transfer, transfer.ErrorMessage ?? "Unknown error");
                break;

            case TransferState.Cancelled:
                if (transfer.State == TransferState.InProgress)
                    Interlocked.Decrement(ref _activeTransfers);
                break;
        }
    }

    /// <summary>
    /// Cleans up completed and failed transfers.
    /// </summary>
    public void Cleanup()
    {
        var toRemove = _transfers
            .Where(kv => kv.Value.State is TransferState.Completed
                                        or TransferState.Failed
                                        or TransferState.Cancelled)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            if (_transfers.TryRemove(id, out var transfer))
            {
                transfer.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var transfer in _transfers.Values)
        {
            transfer.Dispose();
        }
        _transfers.Clear();
        await Task.CompletedTask;
    }
}
