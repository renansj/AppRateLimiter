using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Web;
using AppRateLimiter;
using Moq;
using Xunit;

namespace AppRateLimiter.Web.Tests
{
    public class WebRateLimitRulesTests
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        // Builds a faked classic HttpContext with a chosen client IP, optional XFF header, and
        // optional authenticated ClaimsPrincipal.
        private static HttpContextBase Context(string userHostAddress, string? xff = null, ClaimsPrincipal? user = null)
        {
            var headers = new NameValueCollection();
            if (xff != null) headers["X-Forwarded-For"] = xff;

            var request = new Mock<HttpRequestBase>();
            request.SetupGet(r => r.UserHostAddress).Returns(userHostAddress);
            request.SetupGet(r => r.Headers).Returns(headers);

            var ctx = new Mock<HttpContextBase>();
            ctx.SetupGet(c => c.Request).Returns(request.Object);
            ctx.SetupGet(c => c.User).Returns(() => user!);
            return ctx.Object;
        }

        private static ClaimsPrincipal Authenticated(params Claim[] claims)
            => new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));

        // ByIp produces exactly the same key the core resolver would for the same address.
        [Theory]
        [InlineData("203.0.113.7")]
        [InlineData("2001:db8:1::1")]
        public void ByIp_KeyMatchesCoreNormalization(string ip)
        {
            var rule = WebRateLimitRules.ByIp(10, Window);
            string? key = rule.KeySelector(Context(ip));
            Assert.Equal(IpAddressResolver.NormalizeIp(IPAddress.Parse(ip)), key);
        }

        // Two addresses in the same IPv6 /64 collapse to one key; a different /64 does not.
        [Fact]
        public void ByIp_IPv6_SameSlash64_SharesKey()
        {
            var rule = WebRateLimitRules.ByIp(10, Window);
            string? a = rule.KeySelector(Context("2001:db8:1::1"));
            string? b = rule.KeySelector(Context("2001:db8:1::2"));
            string? c = rule.KeySelector(Context("2001:db8:2::1"));
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }

        // X-Forwarded-For is ignored unless the direct peer is a configured trusted proxy.
        [Fact]
        public void ByIp_Xff_IgnoredWhenPeerNotTrusted()
        {
            var rule = WebRateLimitRules.ByIp(10, Window); // no trusted proxies
            string? key = rule.KeySelector(Context("10.0.0.9", xff: "203.0.113.7"));
            Assert.Equal(IpAddressResolver.NormalizeIp(IPAddress.Parse("10.0.0.9")), key);
        }

        // Behind a trusted proxy, the real client is taken from XFF (right-to-left, skip trusted).
        [Fact]
        public void ByIp_Xff_HonoredBehindTrustedProxy()
        {
            var trusted = new HashSet<IPAddress> { IPAddress.Parse("10.0.0.9") };
            var rule = WebRateLimitRules.ByIp(10, Window, trusted);
            string? key = rule.KeySelector(Context("10.0.0.9", xff: "203.0.113.7, 10.0.0.9"));
            Assert.Equal(IpAddressResolver.NormalizeIp(IPAddress.Parse("203.0.113.7")), key);
        }

        // ByClaim skips unauthenticated requests (no key), so anonymous traffic is left to ByIp.
        [Fact]
        public void ByClaim_SkipsWhenNotAuthenticated()
        {
            var rule = WebRateLimitRules.ByClaim("sub", 10, Window);
            Assert.Null(rule.KeySelector(Context("203.0.113.7")));
        }

        // ByClaim reads the value from the validated principal.
        [Fact]
        public void ByClaim_UsesValidatedClaimValue()
        {
            var rule = WebRateLimitRules.ByClaim("sub", 10, Window);
            var user = Authenticated(new Claim("sub", "user-a"));
            Assert.Equal("user-a", rule.KeySelector(Context("203.0.113.7", user: user)));
        }

        // Default rule names match the core conventions ("ip" and "claim:<type>").
        [Fact]
        public void DefaultRuleNames_MatchCore()
        {
            Assert.Equal("ip", WebRateLimitRules.ByIp(1, Window).Name);
            Assert.Equal("claim:sub", WebRateLimitRules.ByClaim("sub", 1, Window).Name);
        }
    }
}
