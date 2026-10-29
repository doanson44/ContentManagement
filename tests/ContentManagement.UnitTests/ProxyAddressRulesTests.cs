using System.Net;
using ContentManagement.Server.Services;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class ProxyAddressRulesTests
{
    [Theory]
    [InlineData("http://8.8.8.8:8080", "http://8.8.8.8:8080", "http")]
    [InlineData("  SOCKS5://203.0.113.10:1080  ", "socks5://203.0.113.10:1080", "socks5")]
    [InlineData("socks4://198.51.100.7:1080/", "socks4://198.51.100.7:1080", "socks4")]
    [InlineData("https://203.0.113.10:443", "https://203.0.113.10:443", "https")]
    public void TryNormalize_AcceptsSupportedEndpoints(string candidate, string expectedAddress, string expectedProtocol)
    {
        Assert.True(ProxyAddressRules.TryNormalize(candidate, out var address, out var protocol));
        Assert.Equal(expectedAddress, address);
        Assert.Equal(expectedProtocol, protocol);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("8.8.8.8:8080")]
    [InlineData("http://proxy.example.com:8080")]
    [InlineData("http://user:pass@8.8.8.8:8080")]
    [InlineData("http://8.8.8.8:8080/relay")]
    [InlineData("http://8.8.8.8:8080?debug=1")]
    [InlineData("http://8.8.8.8:8080#fragment")]
    [InlineData("ftp://8.8.8.8:21")]
    [InlineData("socks5h://8.8.8.8:1080")]
    [InlineData("http://8.8.8.8:0")]
    public void TryNormalize_RejectsInvalidEndpoints(string? candidate)
    {
        Assert.False(ProxyAddressRules.TryNormalize(candidate, out var address, out var protocol));
        Assert.Equal("", address);
        Assert.Equal("", protocol);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.10")]
    [InlineData("2606:4700:4700::1111")]
    public void IsPublicAddress_AcceptsPublicAddresses(string value) =>
        Assert.True(ProxyAddressRules.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.5.4")]
    [InlineData("192.0.0.8")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.5")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void IsPublicAddress_RejectsNonPublicAddresses(string value) =>
        Assert.False(ProxyAddressRules.IsPublicAddress(IPAddress.Parse(value)));

    [Fact]
    public void IsSupportedScheme_RejectsUnknownSchemes()
    {
        Assert.True(ProxyAddressRules.IsSupportedScheme("socks4"));
        Assert.False(ProxyAddressRules.IsSupportedScheme("socks5h"));
        Assert.False(ProxyAddressRules.IsSupportedScheme("HTTP"));
    }
}
