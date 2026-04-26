using MeshAI.Core.Identity;
using Xunit;

namespace MeshAI.Tests.Unit;

/// <summary>
/// Unit tests for identity components.
/// </summary>
public class IdentityTests
{
    [Fact]
    public void HardwareFingerprint_GeneratesConsistentHash()
    {
        var fingerprint1 = HardwareFingerprint.GetFingerprint();
        var fingerprint2 = HardwareFingerprint.GetFingerprint();

        Assert.Equal(fingerprint1, fingerprint2);
        Assert.Equal(64, fingerprint1.Length); // SHA-256 = 64 hex chars
    }

    [Fact]
    public void ClientIdentity_Generate_CreatesValidIdentity()
    {
        using var identity = ClientIdentity.Generate();

        Assert.NotNull(identity.ClientId);
        Assert.NotNull(identity.HardwareHash);
        Assert.Equal(64, identity.ClientId.Length);
        Assert.Equal(16, identity.ShortId.Length);
        Assert.NotEmpty(identity.PublicSigningKey);
    }

    [Fact]
    public void ClientIdentity_Generate_WithSalt_ProducesDifferentId()
    {
        using var identity1 = ClientIdentity.Generate("salt1");
        using var identity2 = ClientIdentity.Generate("salt2");

        Assert.NotEqual(identity1.ClientId, identity2.ClientId);
    }

    [Fact]
    public void ClientIdentity_Generate_WithSameSalt_ProducesSameId()
    {
        using var identity1 = ClientIdentity.Generate("same_salt");
        using var identity2 = ClientIdentity.Generate("same_salt");

        Assert.Equal(identity1.ClientId, identity2.ClientId);
    }

    [Fact]
    public void ClientIdentity_ClientId_IsBoundToPublicSigningKey()
    {
        using var identity = ClientIdentity.Generate("test");

        Assert.True(ClientIdentity.VerifyClientIdBinding(identity.ClientId, identity.PublicSigningKey));
    }

    [Fact]
    public void ClientIdentity_VerifyClientIdBinding_FailsForMismatch()
    {
        using var identity1 = ClientIdentity.Generate("a");
        using var identity2 = ClientIdentity.Generate("b");

        Assert.False(ClientIdentity.VerifyClientIdBinding(identity1.ClientId, identity2.PublicSigningKey));
    }

    [Fact]
    public void ClientIdentity_Sign_ProducesVerifiableSignature()
    {
        using var identity = ClientIdentity.Generate();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var signature = identity.Sign(data);

        Assert.True(IdentityKey.Verify(identity.PublicSigningKey, data, signature));
    }

    [Fact]
    public void ClientIdentity_Sign_VerifyFailsWithWrongData()
    {
        using var identity = ClientIdentity.Generate();

        var signature = identity.Sign([1, 2, 3, 4, 5]);

        Assert.False(IdentityKey.Verify(identity.PublicSigningKey, [9, 9, 9], signature));
    }

    [Fact]
    public void ClientIdentity_Sign_VerifyFailsWithWrongKey()
    {
        using var identity1 = ClientIdentity.Generate("a");
        using var identity2 = ClientIdentity.Generate("b");
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var signature = identity1.Sign(data);

        Assert.False(IdentityKey.Verify(identity2.PublicSigningKey, data, signature));
    }

    [Fact]
    public void ClientIdentity_VerifyHardwareMatch_ReturnsTrueForLocalIdentity()
    {
        using var identity = ClientIdentity.Generate();

        Assert.True(identity.VerifyHardwareMatch());
    }

    [Fact]
    public void ClientIdentity_Equality_WorksCorrectly()
    {
        using var identity1 = ClientIdentity.Generate("test");
        using var identity2 = ClientIdentity.Generate("test");
        using var identity3 = ClientIdentity.Generate("different");

        Assert.Equal(identity1, identity2);
        Assert.NotEqual(identity1, identity3);
        Assert.True(identity1 == identity2);
        Assert.True(identity1 != identity3);
    }

    [Fact]
    public void ClientIdentity_ToString_ReturnsShortForm()
    {
        using var identity = ClientIdentity.Generate();

        var str = identity.ToString();

        Assert.StartsWith("Client[", str);
        Assert.EndsWith("]", str);
        Assert.Contains(identity.ShortId, str);
    }

    [Fact]
    public void ClientIdentity_CreateHardwareBoundToken_CanBeVerified()
    {
        using var identity = ClientIdentity.Generate();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var token = identity.CreateHardwareBoundToken(data);
        var verified = identity.VerifyHardwareBoundToken(data, token);

        Assert.True(verified);
        Assert.Equal(32, token.Length);
    }

    [Fact]
    public void ClientIdentity_CreateHardwareBoundToken_FailsWithWrongData()
    {
        using var identity = ClientIdentity.Generate();
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var wrongData = new byte[] { 5, 4, 3, 2, 1 };

        var token = identity.CreateHardwareBoundToken(data);
        var verified = identity.VerifyHardwareBoundToken(wrongData, token);

        Assert.False(verified);
    }
}
