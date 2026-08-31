using System;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class GoogleCheckinCodecTests
{
    [Fact]
    public void CreateRequestMatchesReferenceWirePayload()
    {
        byte[] payload = GoogleCheckinCodec.CreateRequest();

        Assert.Equal(
            "IhVgA2oRCAISCzYzLjAuMzIzNC4wGAFwA7ABAA==",
            Convert.ToBase64String(payload));
    }

    [Fact]
    public void ParseResponseReadsUnsignedFixed64CredentialsAndSkipsOtherFields()
    {
        const ulong expectedAndroidId = 0xFEDCBA9876543210;
        const ulong expectedSecurityToken = 0x8877665544332211;
        byte[] response =
        [
            0x08, 0x01,
            0x1A, 0x03, 0x66, 0x6F, 0x6F,
            0x39, 0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE,
            0x41, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        ];

        GoogleCheckinCredentials credentials = GoogleCheckinCodec.ParseResponse(response);

        Assert.Equal(expectedAndroidId, credentials.AndroidId);
        Assert.Equal(expectedSecurityToken, credentials.SecurityToken);
    }

    [Fact]
    public void ParseResponseRejectsOverflowingTenByteVarint()
    {
        byte[] response =
        [
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0x02,
        ];

        Assert.Throws<FormatException>(() => GoogleCheckinCodec.ParseResponse(response));
    }

    [Fact]
    public void ParseResponseRejectsMissingCredentials()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => GoogleCheckinCodec.ParseResponse([0x08, 0x01]));

        Assert.Contains("credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
