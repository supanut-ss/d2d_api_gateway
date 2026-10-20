using System.Text;
using D2D.Infrastructure.Line;

namespace D2D.Tests;

public class LineSignatureTests
{
    private const string Secret = "channel-secret";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"destination":"U1","events":[]}""");

    [Fact]
    public void Accepts_signature_computed_with_channel_secret() =>
        Assert.True(LineSignature.IsValid(Secret, Body, LineSignature.Sign(Secret, Body)));

    [Fact]
    public void Rejects_tampered_body()
    {
        var signature = LineSignature.Sign(Secret, Body);
        var tampered = Encoding.UTF8.GetBytes("""{"destination":"U2","events":[]}""");
        Assert.False(LineSignature.IsValid(Secret, tampered, signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")]
    public void Rejects_missing_or_malformed_signature(string? signature) =>
        Assert.False(LineSignature.IsValid(Secret, Body, signature));
}
