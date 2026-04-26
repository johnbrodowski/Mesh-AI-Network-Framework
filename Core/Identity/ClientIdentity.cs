using System.Security.Cryptography;
using System.Text;

namespace MeshAI.Core.Identity;

/// <summary>
/// Represents a client's identity in the mesh network.
/// ClientId is derived from a public signing key (cryptographically self-vouching).
/// The signing key is deterministically derived from the local hardware fingerprint plus an optional salt,
/// so the same machine produces the same ClientId across runs.
/// </summary>
public sealed class ClientIdentity : IDisposable
{
    private readonly IdentityKey _signingKey;
    private bool _disposed;

    /// <summary>
    /// The unique client identifier (SHA-256 hash of the public signing key, hex-encoded).
    /// </summary>
    public string ClientId { get; }

    /// <summary>
    /// The raw hardware fingerprint (for local verification only).
    /// Empty when this identity represents a remote client.
    /// </summary>
    public string HardwareHash { get; }

    /// <summary>
    /// Public signing key in SPKI format. Distribute this to peers.
    /// </summary>
    public byte[] PublicSigningKey => _signingKey.PublicKey;

    /// <summary>
    /// Timestamp when this identity was generated.
    /// </summary>
    public DateTime GeneratedAt { get; }

    /// <summary>
    /// Short form of ClientId for display purposes.
    /// </summary>
    public string ShortId => ClientId[..16];

    private ClientIdentity(IdentityKey signingKey, string hardwareHash, DateTime generatedAt)
    {
        _signingKey = signingKey;
        HardwareHash = hardwareHash;
        GeneratedAt = generatedAt;
        ClientId = ComputeClientId(signingKey.PublicKey);
    }

    /// <summary>
    /// Generates a client identity from the current machine's hardware fingerprint and an optional salt.
    /// The same machine + salt always produces the same ClientId.
    /// </summary>
    public static ClientIdentity Generate(string? salt = null)
    {
        var hardwareHash = HardwareFingerprint.GetFingerprint();
        var seedInput = $"{hardwareHash}|{salt ?? string.Empty}";
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(seedInput));
        var signingKey = IdentityKey.FromSeed(seed);
        return new ClientIdentity(signingKey, hardwareHash, DateTime.UtcNow);
    }

    /// <summary>
    /// Computes the ClientId for a given public signing key.
    /// </summary>
    public static string ComputeClientId(byte[] publicSigningKey)
    {
        var hash = SHA256.HashData(publicSigningKey);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies that a public signing key belongs to a claimed ClientId.
    /// </summary>
    public static bool VerifyClientIdBinding(string claimedClientId, byte[] publicSigningKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedClientId);
        ArgumentNullException.ThrowIfNull(publicSigningKey);
        var expected = ComputeClientId(publicSigningKey);
        return string.Equals(expected, claimedClientId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Signs data with this identity's private signing key.
    /// </summary>
    public byte[] Sign(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _signingKey.Sign(data);
    }

    /// <summary>
    /// Verifies that the current hardware matches this identity (local-only check).
    /// </summary>
    public bool VerifyHardwareMatch()
    {
        if (string.IsNullOrEmpty(HardwareHash))
            return false;

        var currentHash = HardwareFingerprint.GetFingerprint();
        return string.Equals(currentHash, HardwareHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a hardware-bound token that can be verified on this machine only.
    /// </summary>
    public byte[] CreateHardwareBoundToken(byte[] data)
    {
        var combined = new byte[data.Length + Encoding.UTF8.GetByteCount(HardwareHash)];
        data.CopyTo(combined, 0);
        Encoding.UTF8.GetBytes(HardwareHash).CopyTo(combined, data.Length);
        return SHA256.HashData(combined);
    }

    /// <summary>
    /// Verifies a hardware-bound token.
    /// </summary>
    public bool VerifyHardwareBoundToken(byte[] data, byte[] token)
    {
        var expected = CreateHardwareBoundToken(data);
        return CryptographicOperations.FixedTimeEquals(expected, token);
    }

    public override string ToString() => $"Client[{ShortId}]";

    public override bool Equals(object? obj)
    {
        return obj is ClientIdentity other &&
               string.Equals(ClientId, other.ClientId, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode() => ClientId.GetHashCode(StringComparison.OrdinalIgnoreCase);

    public static bool operator ==(ClientIdentity? left, ClientIdentity? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.Equals(right);
    }

    public static bool operator !=(ClientIdentity? left, ClientIdentity? right) => !(left == right);

    public void Dispose()
    {
        if (!_disposed)
        {
            _signingKey.Dispose();
            _disposed = true;
        }
    }
}
