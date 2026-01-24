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
        // Act
        var fingerprint1 = HardwareFingerprint.GetFingerprint();
        var fingerprint2 = HardwareFingerprint.GetFingerprint();

        // Assert
        Assert.Equal(fingerprint1, fingerprint2);
        Assert.Equal(64, fingerprint1.Length); // SHA-256 = 64 hex chars
    }

    [Fact]
    public void ClientIdentity_Generate_CreatesValidIdentity()
    {
        // Act
        var identity = ClientIdentity.Generate();

        // Assert
        Assert.NotNull(identity.ClientId);
        Assert.NotNull(identity.HardwareHash);
        Assert.Equal(64, identity.ClientId.Length);
        Assert.Equal(16, identity.ShortId.Length);
    }

    [Fact]
    public void ClientIdentity_Generate_WithSalt_ProducesDifferentId()
    {
        // Act
        var identity1 = ClientIdentity.Generate("salt1");
        var identity2 = ClientIdentity.Generate("salt2");

        // Assert
        Assert.NotEqual(identity1.ClientId, identity2.ClientId);
    }

    [Fact]
    public void ClientIdentity_Generate_WithSameSalt_ProducesSameId()
    {
        // Act
        var identity1 = ClientIdentity.Generate("same_salt");
        var identity2 = ClientIdentity.Generate("same_salt");

        // Assert
        Assert.Equal(identity1.ClientId, identity2.ClientId);
    }

    [Fact]
    public void ClientIdentity_FromId_CreatesIdentityFromExisting()
    {
        // Arrange
        var originalId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // Act
        var identity = ClientIdentity.FromId(originalId);

        // Assert
        Assert.Equal(originalId, identity.ClientId);
        Assert.Empty(identity.HardwareHash);
    }

    [Fact]
    public void ClientIdentity_VerifyHardwareMatch_ReturnsTrueForLocalIdentity()
    {
        // Arrange
        var identity = ClientIdentity.Generate();

        // Act
        var matches = identity.VerifyHardwareMatch();

        // Assert
        Assert.True(matches);
    }

    [Fact]
    public void ClientIdentity_VerifyHardwareMatch_ReturnsFalseForRemoteIdentity()
    {
        // Arrange
        var identity = ClientIdentity.FromId("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        // Act
        var matches = identity.VerifyHardwareMatch();

        // Assert
        Assert.False(matches); // No hardware hash stored
    }

    [Fact]
    public void ClientIdentity_Equality_WorksCorrectly()
    {
        // Arrange
        var identity1 = ClientIdentity.Generate("test");
        var identity2 = ClientIdentity.Generate("test");
        var identity3 = ClientIdentity.Generate("different");

        // Assert
        Assert.Equal(identity1, identity2);
        Assert.NotEqual(identity1, identity3);
        Assert.True(identity1 == identity2);
        Assert.True(identity1 != identity3);
    }

    [Fact]
    public void ClientIdentity_ToString_ReturnsShortForm()
    {
        // Arrange
        var identity = ClientIdentity.Generate();

        // Act
        var str = identity.ToString();

        // Assert
        Assert.StartsWith("Client[", str);
        Assert.EndsWith("]", str);
        Assert.Contains(identity.ShortId, str);
    }

    [Fact]
    public void ClientIdentity_CreateHardwareBoundToken_CanBeVerified()
    {
        // Arrange
        var identity = ClientIdentity.Generate();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var token = identity.CreateHardwareBoundToken(data);
        var verified = identity.VerifyHardwareBoundToken(data, token);

        // Assert
        Assert.True(verified);
        Assert.Equal(32, token.Length); // SHA-256
    }

    [Fact]
    public void ClientIdentity_CreateHardwareBoundToken_FailsWithWrongData()
    {
        // Arrange
        var identity = ClientIdentity.Generate();
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var wrongData = new byte[] { 5, 4, 3, 2, 1 };

        // Act
        var token = identity.CreateHardwareBoundToken(data);
        var verified = identity.VerifyHardwareBoundToken(wrongData, token);

        // Assert
        Assert.False(verified);
    }
}
