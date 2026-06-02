using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AppRateLimiter.IntegrationTests;

/// <summary>
/// Integration tests over the sample API. Limits configured in the sample are:
/// IP = 5/min, claim "sub" = 3/min, claim "tenant_id" = 4/min.
/// Each test uses a distinct client IP and distinct claim values, so the shared in-memory
/// store cannot leak counters between tests.
/// </summary>
public sealed class RateLimitingTests : IClassFixture<TestHostHarness>
{
    private const int IpLimit = 5;
    private const int SubLimit = 3;
    private const int TenantLimit = 4;

    private readonly TestHostHarness _h;
    public RateLimitingTests(TestHostHarness h) => _h = h;

    // (1) IP limiting happens before authentication, on the anonymous endpoint.
    [Fact]
    public async Task Ip_RejectsAfterLimit_AndReturnsRetryAfter()
    {
        const string ip = "198.51.100.1";
        for (int i = 0; i < IpLimit; i++)
            Assert.Equal(StatusCodes.Status200OK, (await _h.SendAsync("/public", ip)).Status);

        var (status, retryAfter) = await _h.SendAsync("/public", ip);
        Assert.Equal(StatusCodes.Status429TooManyRequests, status);
        Assert.True(int.Parse(retryAfter) > 0);
    }

    // (2) Different client IPs are tracked independently (no shared bucket).
    [Fact]
    public async Task Ip_DifferentClients_AreIndependent()
    {
        for (int i = 0; i <= IpLimit; i++) await _h.SendAsync("/public", "198.51.100.2");
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await _h.SendAsync("/public", "198.51.100.2")).Status);

        Assert.Equal(StatusCodes.Status200OK, (await _h.SendAsync("/public", "198.51.100.3")).Status);
    }

    // (2b) IPv6 addresses within one /64 share a bucket (anti-rotation); a different /64 does not.
    [Fact]
    public async Task Ip_IPv6_SameSlash64_SharesBucket()
    {
        for (int i = 0; i < IpLimit; i++)
        {
            var ip = (i % 2 == 0) ? "2001:db8:1::1" : "2001:db8:1::2"; // same /64
            Assert.Equal(StatusCodes.Status200OK, (await _h.SendAsync("/public", ip)).Status);
        }
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await _h.SendAsync("/public", "2001:db8:1::dead")).Status);
        Assert.Equal(StatusCodes.Status200OK, (await _h.SendAsync("/public", "2001:db8:2::1")).Status); // different /64
    }

    // (3) JWT claim limiting happens after authentication, keyed on the validated "sub".
    [Fact]
    public async Task Jwt_Subject_RejectsAfterLimit()
    {
        const string ip = "198.51.100.4";
        var token = TestHostHarness.Jwt(new Claim("sub", "user-a"), new Claim("tenant_id", "t-a"));
        for (int i = 0; i < SubLimit; i++)
            Assert.Equal(StatusCodes.Status200OK, (await _h.SendAsync("/secure", ip, token)).Status);

        Assert.Equal(StatusCodes.Status429TooManyRequests, (await _h.SendAsync("/secure", ip, token)).Status);
    }

    // (4) Distinct subjects keep separate quotas: one user cannot exhaust or block another
    // (counters are bound to the validated identity, preventing IDOR-style cross-user impact).
    [Fact]
    public async Task Jwt_DifferentSubjects_AreIndependent()
    {
        const string ip = "198.51.100.5";
        var victim = TestHostHarness.Jwt(new Claim("sub", "user-b"), new Claim("tenant_id", "t-b"));
        for (int i = 0; i < SubLimit; i++)
            Assert.Equal(StatusCodes.Status200OK, (await _h.SendAsync("/secure", ip, victim)).Status);
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await _h.SendAsync("/secure", ip, victim)).Status);

        var other = TestHostHarness.Jwt(new Claim("sub", "user-c"), new Claim("tenant_id", "t-c"));
        Assert.Equal(StatusCodes.Status200OK, (await _h.SendAsync("/secure", ip, other)).Status);
    }

    // (5) A second, dynamically defined claim ("tenant_id") enforces its own limit, even when
    // every request carries a different "sub" (so the sub limit never triggers).
    [Fact]
    public async Task Claims_TenantLimit_RejectsAcrossDistinctSubjects()
    {
        const string ip = "198.51.100.6";
        for (int i = 0; i < TenantLimit; i++)
        {
            var token = TestHostHarness.Jwt(new Claim("sub", $"u{i}"), new Claim("tenant_id", "acme"));
            Assert.Equal(StatusCodes.Status200OK, (await _h.SendAsync("/secure", ip, token)).Status);
        }

        var last = TestHostHarness.Jwt(new Claim("sub", "u-last"), new Claim("tenant_id", "acme"));
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await _h.SendAsync("/secure", ip, last)).Status);
    }

    // (6) A forged token (wrong signature) is rejected by authentication, so it never reaches
    // the claim limiter — claims can't be spoofed to target another principal's bucket.
    [Fact]
    public async Task Jwt_ForgedToken_IsUnauthorized()
    {
        var forged = TestHostHarness.ForgedJwt(new Claim("sub", "attacker"));
        Assert.Equal(StatusCodes.Status401Unauthorized, (await _h.SendAsync("/secure", "198.51.100.7", forged)).Status);
    }

    // (7) Unauthenticated requests to a protected endpoint are rejected by auth; the claim rule
    // is simply skipped for anonymous traffic (which remains covered by the IP rule).
    [Fact]
    public async Task Secure_WithoutToken_IsUnauthorized()
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, (await _h.SendAsync("/secure", "198.51.100.8")).Status);
    }

    // (8) Under concurrent load on a single key, the limiter admits EXACTLY the limit and no
    // more — proving the counter increments are race-free.
    [Fact]
    public async Task Concurrency_DoesNotOverAdmit()
    {
        const string ip = "198.51.100.9";
        const int parallel = 50;
        var tasks = Enumerable.Range(0, parallel).Select(_ => _h.SendAsync("/public", ip));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(IpLimit, results.Count(r => r.Status == StatusCodes.Status200OK));
        Assert.Equal(parallel - IpLimit, results.Count(r => r.Status == StatusCodes.Status429TooManyRequests));
    }
}
