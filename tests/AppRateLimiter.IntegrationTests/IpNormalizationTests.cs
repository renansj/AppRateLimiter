using System.Net;
using AppRateLimiter;
using Xunit;

namespace AppRateLimiter.IntegrationTests;

// Covers the public IP normalization helper that adapters (e.g. System.Web) reuse, so its
// behavior stays locked: IPv4 as-is, IPv4-mapped folds to IPv4, IPv6 collapses to /64.
public class IpNormalizationTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]   // IPv4-mapped folds to IPv4
    [InlineData("2001:db8:1::1", "2001:db8:1::/64")]
    [InlineData("2001:db8:1::abcd", "2001:db8:1::/64")] // same /64
    public void NormalizeIp_ProducesExpectedKey(string input, string expected)
        => Assert.Equal(expected, IpAddressResolver.NormalizeIp(IPAddress.Parse(input)));

    [Fact]
    public void NormalizeIp_DifferentSlash64_DiffersFromSameSlash64()
    {
        string a = IpAddressResolver.NormalizeIp(IPAddress.Parse("2001:db8:1::1"));
        string b = IpAddressResolver.NormalizeIp(IPAddress.Parse("2001:db8:2::1"));
        Assert.NotEqual(a, b);
    }
}
