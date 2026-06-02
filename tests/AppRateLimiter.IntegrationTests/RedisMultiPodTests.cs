using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using StackExchange.Redis;
using Xunit;

namespace AppRateLimiter.IntegrationTests;

/// <summary>
/// Boots TWO independent instances of the sample app (= two pods) backed by the SAME Redis and
/// drives them through the full HTTP pipeline. This is the scenario the in-memory store cannot
/// satisfy: the limit must hold GLOBALLY no matter which pod serves each request.
/// Skipped when no Redis is reachable (REDIS env var, default localhost:6379).
/// </summary>
public sealed class RedisMultiPodTests
{
    private const int IpLimit = 5;
    private const int SubLimit = 3;

    private static (bool Ok, string Config) TryRedis()
    {
        var config = Environment.GetEnvironmentVariable("REDIS") ?? "localhost:6379";
        try
        {
            // abortConnect defaults to true: Connect blocks until connected or throws.
            using var mux = ConnectionMultiplexer.Connect(config + ",connectTimeout=5000,connectRetry=3");
            return (true, config);
        }
        catch { return (false, config); }
    }

    /// <summary>One sample-app instance, configured to use the shared Redis under a given key prefix.</summary>
    private sealed class Pod : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;
        public Pod(string redisConfig, string prefix) =>
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
                .UseSetting("Jwt:Key", TestHostHarness.Key)
                .UseSetting("Jwt:Issuer", TestHostHarness.Issuer)
                .UseSetting("Jwt:Audience", TestHostHarness.Audience)
                .UseSetting("Redis:Configuration", redisConfig + ",abortConnect=false")
                .UseSetting("Redis:KeyPrefix", prefix));

        public Task<(int Status, string RetryAfter)> Send(string path, string ip, string? token = null)
            => TestHostHarness.SendAsync(_factory.Server, path, ip, token);

        public void Dispose() => _factory.Dispose();
    }

    // IP limit is global across pods: spreading requests over two pods still caps at IpLimit.
    [SkippableFact]
    public async Task TwoPods_ShareGlobalIpLimit()
    {
        var (ok, config) = TryRedis();
        Skip.IfNot(ok, "Redis not reachable at " + config);

        var prefix = "it:" + Guid.NewGuid().ToString("N") + ":";
        using var pod1 = new Pod(config, prefix);
        using var pod2 = new Pod(config, prefix);
        const string ip = "198.51.100.50";

        int allowed = 0;
        for (int i = 0; i < 12; i++)
        {
            var pod = (i % 2 == 0) ? pod1 : pod2; // alternate pods
            if ((await pod.Send("/public", ip)).Status == StatusCodes.Status200OK) allowed++;
        }

        Assert.Equal(IpLimit, allowed);
    }

    // JWT "sub" limit is global across pods too (claim limiting after auth, shared via Redis).
    [SkippableFact]
    public async Task TwoPods_ShareGlobalJwtSubjectLimit()
    {
        var (ok, config) = TryRedis();
        Skip.IfNot(ok, "Redis not reachable at " + config);

        var prefix = "it:" + Guid.NewGuid().ToString("N") + ":";
        using var pod1 = new Pod(config, prefix);
        using var pod2 = new Pod(config, prefix);
        const string ip = "198.51.100.51";
        var token = TestHostHarness.Jwt(new Claim("sub", "shared-user"), new Claim("tenant_id", "t"));

        int allowed = 0;
        for (int i = 0; i < 8; i++)
        {
            var pod = (i % 2 == 0) ? pod1 : pod2;
            if ((await pod.Send("/secure", ip, token)).Status == StatusCodes.Status200OK) allowed++;
        }

        Assert.Equal(SubLimit, allowed);
    }
}
