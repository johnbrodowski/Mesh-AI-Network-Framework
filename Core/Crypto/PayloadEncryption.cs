using System.Security.Cryptography;

namespace MeshAI.Core.Crypto;

/// <summary>
/// Handles payload-layer encryption for secure data transfer.
/// Uses AES-256-GCM for authenticated encryption.
/// </summary>
public static class PayloadEncryption
{
    private const int KeySize = 32;   // 256 bits
    private const int NonceSize = 12; // 96 bits for GCM
    private const int TagSize = 16;   // 128 bits authentication tag

    /// <summary>
    /// Generates a random encryption key.
    /// </summary>
    public static byte[] GenerateKey()
    {
        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// Derives an encryption key from a shared secret using HKDF.
    /// </summary>
    public static byte[] DeriveKey(byte[] sharedSecret, byte[] salt, byte[]? info = null)
    {
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            KeySize,
            salt,
            info ?? []);
    }

    /// <summary>
    /// Encrypts data using AES-256-GCM.
    /// Returns: nonce (12 bytes) + ciphertext + tag (16 bytes)
    /// </summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] key, byte[]? associatedData = null)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes", nameof(key));

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        // Combine: nonce + ciphertext + tag
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);

        return result;
    }

    /// <summary>
    /// Decrypts data encrypted with AES-256-GCM.
    /// Input format: nonce (12 bytes) + ciphertext + tag (16 bytes)
    /// </summary>
    public static byte[] Decrypt(byte[] encryptedData, byte[] key, byte[]? associatedData = null)
    {
        ArgumentNullException.ThrowIfNull(encryptedData);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes", nameof(key));

        if (encryptedData.Length < NonceSize + TagSize)
            throw new ArgumentException("Encrypted data is too short", nameof(encryptedData));

        var nonce = encryptedData.AsSpan(0, NonceSize);
        var ciphertextLength = encryptedData.Length - NonceSize - TagSize;
        var ciphertext = encryptedData.AsSpan(NonceSize, ciphertextLength);
        var tag = encryptedData.AsSpan(NonceSize + ciphertextLength, TagSize);

        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        return plaintext;
    }

    /// <summary>
    /// Encrypts data and binds it to a hardware hash.
    /// The hardware hash is used as associated data for authentication.
    /// </summary>
    public static byte[] EncryptWithHardwareBinding(byte[] plaintext, byte[] key, string hardwareHash)
    {
        var associatedData = System.Text.Encoding.UTF8.GetBytes(hardwareHash);
        return Encrypt(plaintext, key, associatedData);
    }

    /// <summary>
    /// Decrypts data that was bound to a hardware hash.
    /// Will fail if the hardware hash doesn't match.
    /// </summary>
    public static byte[] DecryptWithHardwareBinding(byte[] encryptedData, byte[] key, string hardwareHash)
    {
        var associatedData = System.Text.Encoding.UTF8.GetBytes(hardwareHash);
        return Decrypt(encryptedData, key, associatedData);
    }
}
