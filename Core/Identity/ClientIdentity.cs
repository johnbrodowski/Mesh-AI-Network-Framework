using System.Security.Cryptography;
using System.Text;

namespace MeshAI.Core.Identity;

/// <summary>
/// Represents a client's identity in the mesh network.
/// Identity is derived from hardware fingerprint and an optional salt.
/// </summary>
public sealed class ClientIdentity
{
    /// <summary>
    /// The unique client identifier (SHA256 hash of hardware fingerprint + salt).
    /// </summary>
    public string ClientId { get; }

    /// <summary>
    /// The raw hardware fingerprint (for local verification only).
    /// </summary>
    public string HardwareHash { get; }

    /// <summary>
    /// Timestamp when this identity was generated.
    /// </summary>
    public DateTime GeneratedAt { get; }

    /// <summary>
    /// Short form of ClientId for display purposes.
    /// </summary>
    public string ShortId => ClientId[..16];

    private ClientIdentity(string clientId, string hardwareHash, DateTime generatedAt)
    {
        ClientId = clientId;
        HardwareHash = hardwareHash;
        GeneratedAt = generatedAt;
    }

    /// <summary>
    /// Generates a new client identity from the current machine's hardware.
    /// </summary>
    /// <param name="salt">Optional salt for additional uniqueness.</param>
    public static ClientIdentity Generate(string? salt = null)
    {
        var hardwareHash = HardwareFingerprint.GetFingerprint();
        var combinedInput = hardwareHash + (salt ?? string.Empty);

        var bytes = Encoding.UTF8.GetBytes(combinedInput);
        var hash = SHA256.HashData(bytes);
        var clientId = Convert.ToHexString(hash).ToLowerInvariant();

        return new ClientIdentity(clientId, hardwareHash, DateTime.UtcNow);
    }

    /// <summary>
    /// Creates a ClientIdentity from an existing ID (for remote clients).
    /// </summary>
    public static ClientIdentity FromId(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return new ClientIdentity(clientId.ToLowerInvariant(), string.Empty, DateTime.UtcNow);
    }

    /// <summary>
    /// Verifies that the current hardware matches this identity.
    /// Used to ensure files are only usable on the matching machine.
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
}
