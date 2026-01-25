using System.Security.Cryptography;

namespace MeshAI.Core.Crypto;

/// <summary>
/// Provides Elliptic Curve Diffie-Hellman key exchange for secure key establishment.
/// Uses NIST P-256 curve for compatibility.
/// </summary>
public sealed class KeyExchange : IDisposable
{
    private readonly ECDiffieHellman _ecdh;
    private bool _disposed;

    /// <summary>
    /// The public key to share with the peer.
    /// </summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// Creates a new key exchange instance with a fresh key pair.
    /// </summary>
    public KeyExchange()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// Derives a shared secret from the peer's public key.
    /// </summary>
    public byte[] DeriveSharedSecret(byte[] peerPublicKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(peerPublicKey);

        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(peerPublicKey, out _);

        return _ecdh.DeriveKeyMaterial(peerKey.PublicKey);
    }

    /// <summary>
    /// Derives an encryption key from the peer's public key using HKDF.
    /// </summary>
    public byte[] DeriveEncryptionKey(byte[] peerPublicKey, byte[] salt)
    {
        var sharedSecret = DeriveSharedSecret(peerPublicKey);
        return PayloadEncryption.DeriveKey(sharedSecret, salt);
    }

    /// <summary>
    /// Exports the key pair for persistence (if needed).
    /// </summary>
    public byte[] ExportPrivateKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _ecdh.ExportPkcs8PrivateKey();
    }

    /// <summary>
    /// Imports a previously exported key pair.
    /// </summary>
    public static KeyExchange ImportPrivateKey(byte[] privateKey)
    {
        var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(privateKey, out _);
        return new KeyExchange(ecdh);
    }

    private KeyExchange(ECDiffieHellman ecdh)
    {
        _ecdh = ecdh;
        PublicKey = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _ecdh.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Represents a completed key exchange with derived session keys.
/// </summary>
public sealed class SessionKeys : IDisposable
{
    /// <summary>
    /// Key for encrypting outgoing messages.
    /// </summary>
    public byte[] SendKey { get; }

    /// <summary>
    /// Key for decrypting incoming messages.
    /// </summary>
    public byte[] ReceiveKey { get; }

    /// <summary>
    /// Session identifier derived from the key exchange.
    /// </summary>
    public byte[] SessionId { get; }

    private bool _disposed;

    private SessionKeys(byte[] sendKey, byte[] receiveKey, byte[] sessionId)
    {
        SendKey = sendKey;
        ReceiveKey = receiveKey;
        SessionId = sessionId;
    }

    /// <summary>
    /// Establishes session keys from a key exchange.
    /// Uses ordered public keys to ensure both sides derive the same keys.
    /// </summary>
    public static SessionKeys Establish(KeyExchange localExchange, byte[] peerPublicKey)
    {
        var sharedSecret = localExchange.DeriveSharedSecret(peerPublicKey);
        var localPublicKey = localExchange.PublicKey;

        // Determine key ordering based on public key comparison
        var comparison = CompareBytes(localPublicKey, peerPublicKey);

        // Generate session ID in deterministic order (sorted public keys)
        byte[] sessionId;
        if (comparison < 0)
        {
            sessionId = SHA256.HashData([.. localPublicKey, .. peerPublicKey]);
        }
        else
        {
            sessionId = SHA256.HashData([.. peerPublicKey, .. localPublicKey]);
        }

        // Derive keys with different info for send/receive
        // The "lower" key holder uses key1 for send, key2 for receive
        // The "higher" key holder uses key2 for send, key1 for receive
        var salt = sessionId;
        var key1 = PayloadEncryption.DeriveKey(sharedSecret, salt, "key1"u8.ToArray());
        var key2 = PayloadEncryption.DeriveKey(sharedSecret, salt, "key2"u8.ToArray());

        // Assign keys based on ordering - lower public key gets key1 as send
        if (comparison < 0)
        {
            return new SessionKeys(key1, key2, sessionId);
        }
        else
        {
            return new SessionKeys(key2, key1, sessionId);
        }
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            if (a[i] != b[i])
                return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(SendKey);
            CryptographicOperations.ZeroMemory(ReceiveKey);
            _disposed = true;
        }
    }
}
