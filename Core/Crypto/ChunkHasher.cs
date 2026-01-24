using System.Security.Cryptography;

namespace MeshAI.Core.Crypto;

/// <summary>
/// Provides hash-based integrity verification for file chunks.
/// Uses SHA-256 for content hashing and includes replay protection.
/// </summary>
public static class ChunkHasher
{
    /// <summary>
    /// Computes the hash of a data chunk.
    /// </summary>
    public static byte[] ComputeHash(byte[] data)
    {
        return SHA256.HashData(data);
    }

    /// <summary>
    /// Computes the hash of a data chunk as a hex string.
    /// </summary>
    public static string ComputeHashHex(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies a chunk against its expected hash.
    /// </summary>
    public static bool VerifyHash(byte[] data, byte[] expectedHash)
    {
        var actualHash = ComputeHash(data);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    /// <summary>
    /// Verifies a chunk against its expected hash (hex string).
    /// </summary>
    public static bool VerifyHashHex(byte[] data, string expectedHashHex)
    {
        var expectedHash = Convert.FromHexString(expectedHashHex);
        return VerifyHash(data, expectedHash);
    }

    /// <summary>
    /// Creates a timestamped hash that includes replay protection.
    /// Format: SHA256(timestamp || data)
    /// </summary>
    public static (byte[] Hash, long Timestamp) ComputeTimestampedHash(byte[] data)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var timestampBytes = BitConverter.GetBytes(timestamp);

        var combined = new byte[timestampBytes.Length + data.Length];
        timestampBytes.CopyTo(combined, 0);
        data.CopyTo(combined, timestampBytes.Length);

        return (SHA256.HashData(combined), timestamp);
    }

    /// <summary>
    /// Verifies a timestamped hash, checking both integrity and freshness.
    /// </summary>
    /// <param name="maxAgeMs">Maximum age of the hash in milliseconds (default: 5 minutes)</param>
    public static bool VerifyTimestampedHash(byte[] data, byte[] hash, long timestamp, long maxAgeMs = 300_000)
    {
        // Check freshness
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var age = now - timestamp;

        if (age < 0 || age > maxAgeMs)
            return false;

        // Verify hash
        var timestampBytes = BitConverter.GetBytes(timestamp);
        var combined = new byte[timestampBytes.Length + data.Length];
        timestampBytes.CopyTo(combined, 0);
        data.CopyTo(combined, timestampBytes.Length);

        var expectedHash = SHA256.HashData(combined);
        return CryptographicOperations.FixedTimeEquals(hash, expectedHash);
    }

    /// <summary>
    /// Computes a Merkle tree root hash from a list of chunk hashes.
    /// Useful for verifying complete file integrity.
    /// </summary>
    public static byte[] ComputeMerkleRoot(IReadOnlyList<byte[]> chunkHashes)
    {
        if (chunkHashes.Count == 0)
            return SHA256.HashData([]);

        if (chunkHashes.Count == 1)
            return chunkHashes[0];

        var level = new List<byte[]>(chunkHashes);

        while (level.Count > 1)
        {
            var nextLevel = new List<byte[]>();

            for (var i = 0; i < level.Count; i += 2)
            {
                if (i + 1 < level.Count)
                {
                    // Hash pair of nodes
                    var combined = new byte[level[i].Length + level[i + 1].Length];
                    level[i].CopyTo(combined, 0);
                    level[i + 1].CopyTo(combined, level[i].Length);
                    nextLevel.Add(SHA256.HashData(combined));
                }
                else
                {
                    // Odd node - promote to next level
                    nextLevel.Add(level[i]);
                }
            }

            level = nextLevel;
        }

        return level[0];
    }
}
