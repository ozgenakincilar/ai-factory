using System.Net;
using System.Security.Cryptography;
using Factory.Infrastructure.Security;
using Factory.Tests.Orchestration;

namespace Factory.Tests.Security;

public class CustomerModelSecurityTests
{
    private static AesGcmSecretProtector Protector() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void SecretsRoundTripOnlyWithTheirOwnContextAndKey()
    {
        var protector = Protector();
        var first = protector.Protect("sk-customer-secret", "model-api-key:org-a:1");
        var second = protector.Protect("sk-customer-secret", "model-api-key:org-a:1");
        Assert.NotEqual(first, second); // fresh nonce every time
        Assert.Equal("sk-customer-secret", protector.Unprotect(first, "model-api-key:org-a:1"));
        Assert.DoesNotContain("sk-customer-secret", System.Text.Encoding.UTF8.GetString(first));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(first, "model-api-key:org-b:1"));
        Assert.ThrowsAny<CryptographicException>(() => Protector().Unprotect(first, "model-api-key:org-a:1"));
        var tampered = first.ToArray(); tampered[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered, "model-api-key:org-a:1"));
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[16]));
        Assert.Throws<InvalidOperationException>(() => AesGcmSecretProtector.FromConfiguration("not base64"));
        Assert.Null(AesGcmSecretProtector.FromConfiguration(null));
    }

    [Theory]
    [InlineData("https://api.example.com/v1", false, true)]
    [InlineData("http://api.example.com/v1", false, false)]
    [InlineData("https://user:pass@api.example.com/v1", false, false)]
    [InlineData("https://api.example.com/v1#x", false, false)]
    [InlineData("https://api.example.com/v1?key=x", false, false)]
    [InlineData("ftp://api.example.com", false, false)]
    [InlineData("/relative", false, false)]
    [InlineData("http://127.0.0.1:5000/v1", false, false)]
    [InlineData("http://127.0.0.1:5000/v1", true, true)]
    [InlineData("https://localhost/v1", false, false)]
    public void EndpointUrlsMustBeHttpsWithoutCredentials(string url, bool allowLoopback, bool valid)
    {
        var policy = new ModelEndpointPolicy(allowLoopback);
        if (valid) Assert.NotNull(policy.ValidateUrl(url)); else Assert.Throws<ArgumentException>(() => policy.ValidateUrl(url));
    }

    [Theory]
    [InlineData("8.8.8.8", true)] [InlineData("1.1.1.1", true)] [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)] [InlineData("::1", false)] [InlineData("10.1.2.3", false)] [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)] [InlineData("192.168.1.1", false)] [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)] [InlineData("0.0.0.0", false)] [InlineData("224.0.0.1", false)] [InlineData("255.255.255.255", false)]
    [InlineData("fc00::1", false)] [InlineData("fd12::1", false)] [InlineData("fe80::1", false)] [InlineData("ff02::1", false)] [InlineData("::", false)]
    [InlineData("::ffff:10.0.0.1", false)] [InlineData("::ffff:8.8.8.8", true)] [InlineData("64:ff9b::a00:1", false)] [InlineData("64:ff9b::808:808", true)]
    public void PrivateLoopbackLinkLocalAndMetadataAddressesAreRefused(string address, bool allowed)
        => Assert.Equal(allowed, new ModelEndpointPolicy(false).IsAllowed(IPAddress.Parse(address)));

    [Fact]
    public void LoopbackCanBeEnabledOnlyForDevelopmentOrTesting()
    {
        Assert.True(ModelEndpointPolicy.FromConfiguration(true, developmentOrTesting: true).AllowLoopback);
        Assert.Throws<InvalidOperationException>(() => ModelEndpointPolicy.FromConfiguration(true, developmentOrTesting: false));
        Assert.True(new ModelEndpointPolicy(true).IsAllowed(IPAddress.Loopback));
    }

    [Fact]
    public async Task ConnectionsAreRefusedAtConnectTimeEvenWhenTheUrlWasAccepted()
    {
        await using var server = await FakeOpenAiServer.StartAsync();
        // The URL policy is bypassed here on purpose: the socket-level check alone must refuse loopback,
        // including through a host name that resolves to it (DNS rebinding shape).
        using var refused = new ModelEndpointPolicy(false).CreateClient();
        foreach (var url in new[] { server.BaseUrl + "/models", server.BaseUrl.Replace("127.0.0.1", "localhost") + "/models" })
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => refused.GetAsync(url));
            Assert.Contains("model_endpoint_address_forbidden", error.ToString());
        }
        Assert.Equal(0, server.Requests);
        using var allowed = new ModelEndpointPolicy(true).CreateClient();
        using var response = await allowed.GetAsync(server.BaseUrl + "/models");
        Assert.Equal(1, server.Requests);
    }
}
