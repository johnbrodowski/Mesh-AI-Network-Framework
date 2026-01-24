using System.Security.Cryptography;
using MeshAI.Core.Crypto;
using Xunit;

namespace MeshAI.Tests.Unit;

/// <summary>
/// Unit tests for cryptographic components.
/// </summary>
public class CryptoTests
{
    [Fact]
    public void PayloadEncryption_GenerateKey_Creates32ByteKey()
    {
        // Act
        var key = PayloadEncryption.GenerateKey();

        // Assert
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void PayloadEncryption_EncryptDecrypt_RoundTrip()
    {
        // Arrange
        var key = PayloadEncryption.GenerateKey();
        var plaintext = "Hello, World!"u8.ToArray();

        // Act
        var ciphertext = PayloadEncryption.Encrypt(plaintext, key);
        var decrypted = PayloadEncryption.Decrypt(ciphertext, key);

        // Assert
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void PayloadEncryption_Encrypt_ProducesDifferentCiphertextEachTime()
    {
        // Arrange
        var key = PayloadEncryption.GenerateKey();
        var plaintext = "Same message"u8.ToArray();

        // Act
        var ciphertext1 = PayloadEncryption.Encrypt(plaintext, key);
        var ciphertext2 = PayloadEncryption.Encrypt(plaintext, key);

        // Assert - Different nonces should produce different ciphertext
        Assert.NotEqual(ciphertext1, ciphertext2);
    }

    [Fact]
    public void PayloadEncryption_Decrypt_FailsWithWrongKey()
    {
        // Arrange
        var key1 = PayloadEncryption.GenerateKey();
        var key2 = PayloadEncryption.GenerateKey();
        var plaintext = "Secret message"u8.ToArray();

        // Act
        var ciphertext = PayloadEncryption.Encrypt(plaintext, key1);

        // Assert
        Assert.Throws<CryptographicException>(() =>
            PayloadEncryption.Decrypt(ciphertext, key2));
    }

    [Fact]
    public void PayloadEncryption_Decrypt_FailsWithTamperedData()
    {
        // Arrange
        var key = PayloadEncryption.GenerateKey();
        var plaintext = "Important data"u8.ToArray();

        // Act
        var ciphertext = PayloadEncryption.Encrypt(plaintext, key);
        ciphertext[20] ^= 0xFF; // Tamper with ciphertext

        // Assert
        Assert.ThrowsAny<CryptographicException>(() =>
            PayloadEncryption.Decrypt(ciphertext, key));
    }

    [Fact]
    public void PayloadEncryption_WithHardwareBinding_RequiresMatchingHash()
    {
        // Arrange
        var key = PayloadEncryption.GenerateKey();
        var plaintext = "Bound data"u8.ToArray();
        var hardwareHash = "abc123";

        // Act
        var ciphertext = PayloadEncryption.EncryptWithHardwareBinding(plaintext, key, hardwareHash);
        var decrypted = PayloadEncryption.DecryptWithHardwareBinding(ciphertext, key, hardwareHash);

        // Assert
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void PayloadEncryption_WithHardwareBinding_FailsWithWrongHash()
    {
        // Arrange
        var key = PayloadEncryption.GenerateKey();
        var plaintext = "Bound data"u8.ToArray();

        // Act
        var ciphertext = PayloadEncryption.EncryptWithHardwareBinding(plaintext, key, "correct_hash");

        // Assert
        Assert.Throws<CryptographicException>(() =>
            PayloadEncryption.DecryptWithHardwareBinding(ciphertext, key, "wrong_hash"));
    }

    [Fact]
    public void PayloadEncryption_DeriveKey_ProducesDeterministicKey()
    {
        // Arrange
        var sharedSecret = new byte[32];
        RandomNumberGenerator.Fill(sharedSecret);
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        // Act
        var key1 = PayloadEncryption.DeriveKey(sharedSecret, salt);
        var key2 = PayloadEncryption.DeriveKey(sharedSecret, salt);

        // Assert
        Assert.Equal(key1, key2);
        Assert.Equal(32, key1.Length);
    }

    [Fact]
    public void ChunkHasher_ComputeHash_ProducesConsistentResult()
    {
        // Arrange
        var data = "Test data for hashing"u8.ToArray();

        // Act
        var hash1 = ChunkHasher.ComputeHash(data);
        var hash2 = ChunkHasher.ComputeHash(data);

        // Assert
        Assert.Equal(hash1, hash2);
        Assert.Equal(32, hash1.Length); // SHA-256
    }

    [Fact]
    public void ChunkHasher_VerifyHash_ReturnsTrueForValidHash()
    {
        // Arrange
        var data = "Verify this"u8.ToArray();
        var hash = ChunkHasher.ComputeHash(data);

        // Act
        var valid = ChunkHasher.VerifyHash(data, hash);

        // Assert
        Assert.True(valid);
    }

    [Fact]
    public void ChunkHasher_VerifyHash_ReturnsFalseForInvalidHash()
    {
        // Arrange
        var data = "Original"u8.ToArray();
        var hash = ChunkHasher.ComputeHash(data);
        var tamperedData = "Modified"u8.ToArray();

        // Act
        var valid = ChunkHasher.VerifyHash(tamperedData, hash);

        // Assert
        Assert.False(valid);
    }

    [Fact]
    public void ChunkHasher_ComputeHashHex_ReturnsLowercaseHex()
    {
        // Arrange
        var data = "Test"u8.ToArray();

        // Act
        var hashHex = ChunkHasher.ComputeHashHex(data);

        // Assert
        Assert.Equal(64, hashHex.Length);
        Assert.Equal(hashHex.ToLowerInvariant(), hashHex);
    }

    [Fact]
    public void ChunkHasher_TimestampedHash_VerifiesWithinTimeWindow()
    {
        // Arrange
        var data = "Timestamped data"u8.ToArray();

        // Act
        var (hash, timestamp) = ChunkHasher.ComputeTimestampedHash(data);
        var valid = ChunkHasher.VerifyTimestampedHash(data, hash, timestamp);

        // Assert
        Assert.True(valid);
    }

    [Fact]
    public void ChunkHasher_TimestampedHash_FailsWhenExpired()
    {
        // Arrange
        var data = "Old data"u8.ToArray();
        var oldTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();

        // Create hash with old timestamp manually
        var timestampBytes = BitConverter.GetBytes(oldTimestamp);
        var combined = new byte[timestampBytes.Length + data.Length];
        timestampBytes.CopyTo(combined, 0);
        data.CopyTo(combined, timestampBytes.Length);
        var hash = System.Security.Cryptography.SHA256.HashData(combined);

        // Act
        var valid = ChunkHasher.VerifyTimestampedHash(data, hash, oldTimestamp, maxAgeMs: 60000);

        // Assert
        Assert.False(valid);
    }

    [Fact]
    public void ChunkHasher_ComputeMerkleRoot_SingleChunk()
    {
        // Arrange
        var chunkHash = ChunkHasher.ComputeHash("Chunk 1"u8.ToArray());

        // Act
        var merkleRoot = ChunkHasher.ComputeMerkleRoot([chunkHash]);

        // Assert
        Assert.Equal(chunkHash, merkleRoot);
    }

    [Fact]
    public void ChunkHasher_ComputeMerkleRoot_MultipleChunks()
    {
        // Arrange
        var hash1 = ChunkHasher.ComputeHash("Chunk 1"u8.ToArray());
        var hash2 = ChunkHasher.ComputeHash("Chunk 2"u8.ToArray());
        var hash3 = ChunkHasher.ComputeHash("Chunk 3"u8.ToArray());

        // Act
        var merkleRoot = ChunkHasher.ComputeMerkleRoot([hash1, hash2, hash3]);

        // Assert
        Assert.NotNull(merkleRoot);
        Assert.Equal(32, merkleRoot.Length);
    }

    [Fact]
    public void ChunkHasher_ComputeMerkleRoot_IsDeterministic()
    {
        // Arrange
        var hashes = new[]
        {
            ChunkHasher.ComputeHash("A"u8.ToArray()),
            ChunkHasher.ComputeHash("B"u8.ToArray()),
            ChunkHasher.ComputeHash("C"u8.ToArray()),
            ChunkHasher.ComputeHash("D"u8.ToArray())
        };

        // Act
        var root1 = ChunkHasher.ComputeMerkleRoot(hashes);
        var root2 = ChunkHasher.ComputeMerkleRoot(hashes);

        // Assert
        Assert.Equal(root1, root2);
    }

    [Fact]
    public void KeyExchange_GeneratesValidPublicKey()
    {
        // Act
        using var exchange = new KeyExchange();

        // Assert
        Assert.NotNull(exchange.PublicKey);
        Assert.True(exchange.PublicKey.Length > 0);
    }

    [Fact]
    public void KeyExchange_DerivesSharedSecret()
    {
        // Arrange
        using var alice = new KeyExchange();
        using var bob = new KeyExchange();

        // Act
        var aliceSecret = alice.DeriveSharedSecret(bob.PublicKey);
        var bobSecret = bob.DeriveSharedSecret(alice.PublicKey);

        // Assert
        Assert.Equal(aliceSecret, bobSecret);
    }

    [Fact]
    public void KeyExchange_DerivesEncryptionKey()
    {
        // Arrange
        using var alice = new KeyExchange();
        using var bob = new KeyExchange();
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        // Act
        var aliceKey = alice.DeriveEncryptionKey(bob.PublicKey, salt);
        var bobKey = bob.DeriveEncryptionKey(alice.PublicKey, salt);

        // Assert
        Assert.Equal(aliceKey, bobKey);
        Assert.Equal(32, aliceKey.Length);
    }

    [Fact]
    public void SessionKeys_EstablishesMatchingKeys()
    {
        // Arrange
        using var alice = new KeyExchange();
        using var bob = new KeyExchange();

        // Act
        using var aliceSession = SessionKeys.Establish(alice, bob.PublicKey);
        using var bobSession = SessionKeys.Establish(bob, alice.PublicKey);

        // Assert - Send key of one should match receive key of other
        Assert.Equal(aliceSession.SendKey, bobSession.ReceiveKey);
        Assert.Equal(aliceSession.ReceiveKey, bobSession.SendKey);
        Assert.Equal(aliceSession.SessionId, bobSession.SessionId);
    }

    [Fact]
    public void SessionKeys_CanEncryptAndDecrypt()
    {
        // Arrange
        using var alice = new KeyExchange();
        using var bob = new KeyExchange();
        using var aliceSession = SessionKeys.Establish(alice, bob.PublicKey);
        using var bobSession = SessionKeys.Establish(bob, alice.PublicKey);

        var message = "Secret message"u8.ToArray();

        // Act - Alice encrypts with send key
        var encrypted = PayloadEncryption.Encrypt(message, aliceSession.SendKey);

        // Bob decrypts with receive key
        var decrypted = PayloadEncryption.Decrypt(encrypted, bobSession.ReceiveKey);

        // Assert
        Assert.Equal(message, decrypted);
    }

    [Fact]
    public void KeyExchange_ExportImport_PreservesKeyPair()
    {
        // Arrange
        using var original = new KeyExchange();
        var exported = original.ExportPrivateKey();

        // Act
        using var imported = KeyExchange.ImportPrivateKey(exported);

        // Assert
        Assert.Equal(original.PublicKey, imported.PublicKey);
    }
}
