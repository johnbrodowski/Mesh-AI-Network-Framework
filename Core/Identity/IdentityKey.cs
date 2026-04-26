using System.Security.Cryptography;
using System.Text;

namespace MeshAI.Core.Identity;

/// <summary>
/// Long-term ECDSA signing keypair used to prove ownership of a ClientId.
/// The ClientId is derived from the public key, so possessing the private key
/// is necessary and sufficient to prove identity.
/// </summary>
public sealed class IdentityKey : IDisposable
{
    private readonly ECDsa _ecdsa;
    private bool _disposed;

    /// <summary>
    /// Public key in SubjectPublicKeyInfo (SPKI) format.
    /// </summary>
    public byte[] PublicKey { get; }

    private IdentityKey(ECDsa ecdsa)
    {
        _ecdsa = ecdsa;
        PublicKey = _ecdsa.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// Generates a fresh random identity key (P-256).
    /// </summary>
    public static IdentityKey Generate()
    {
        return new IdentityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));
    }

    /// <summary>
    /// Derives a deterministic identity key from a seed.
    /// The same seed produces the same key on every machine.
    /// </summary>
    public static IdentityKey FromSeed(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        // Derive 32 bytes for the P-256 private scalar via HKDF.
        // If the resulting scalar is invalid (>= curve order or zero), retry with a counter.
        for (byte counter = 0; counter < 16; counter++)
        {
            var info = Encoding.UTF8.GetBytes($"MeshAI-IdentityKey-v1-{counter}");
            var d = HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, 32, salt: null, info: info);
            try
            {
                var sec1 = BuildSec1PrivateKey(d);
                var ecdsa = ECDsa.Create();
                ecdsa.ImportECPrivateKey(sec1, out _);
                return new IdentityKey(ecdsa);
            }
            catch (CryptographicException)
            {
                // Invalid scalar (vanishingly rare); try next counter.
            }
        }
        throw new CryptographicException("Failed to derive a valid identity key from seed");
    }

    /// <summary>
    /// Signs the given data, returning the raw IEEE P1363 signature.
    /// </summary>
    public byte[] Sign(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Verifies a signature using the given public key (SPKI format).
    /// </summary>
    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hand-builds a SEC1 ECPrivateKey ASN.1 structure with the given private scalar
    /// for P-256, omitting the public key (the runtime will derive it).
    /// </summary>
    private static byte[] BuildSec1PrivateKey(byte[] d)
    {
        // SEQUENCE {
        //   INTEGER 1               (version)
        //   OCTET STRING (32 bytes) (private scalar D)
        //   [0] EXPLICIT { OID 1.2.840.10045.3.1.7 (P-256 namedCurve) }
        // }
        var bytes = new byte[51];
        int i = 0;
        bytes[i++] = 0x30; bytes[i++] = 0x31;                   // SEQUENCE, len 49
        bytes[i++] = 0x02; bytes[i++] = 0x01; bytes[i++] = 0x01; // INTEGER 1
        bytes[i++] = 0x04; bytes[i++] = 0x20;                   // OCTET STRING, len 32
        Buffer.BlockCopy(d, 0, bytes, i, 32); i += 32;
        bytes[i++] = 0xA0; bytes[i++] = 0x0A;                   // [0] EXPLICIT, len 10
        bytes[i++] = 0x06; bytes[i++] = 0x08;                   // OID, len 8
        // P-256 OID: 1.2.840.10045.3.1.7
        bytes[i++] = 0x2A; bytes[i++] = 0x86; bytes[i++] = 0x48; bytes[i++] = 0xCE;
        bytes[i++] = 0x3D; bytes[i++] = 0x03; bytes[i++] = 0x01; bytes[i++] = 0x07;
        return bytes;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _ecdsa.Dispose();
            _disposed = true;
        }
    }
}
