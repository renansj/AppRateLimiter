using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using AppRateLimiter;
using Xunit;

namespace AppRateLimiter.Redis.Tests;

public sealed class RedisRateLimitStoreTests : IClassFixture<RedisFixture>
{
    private const string Prefix = "test-rl:";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly RedisFixture _fx;
    public RedisRateLimitStoreTests(RedisFixture fx) => _fx = fx;

    private static string NewKey() => Guid.NewGuid().ToString("N");

    // A single store enforces exactly the limit, then reports a positive Retry-After.
    [SkippableFact]
    public async Task SingleInstance_EnforcesLimit()
    {
        Skip.IfNot(_fx.Available, "Redis not reachable at " + _fx.Config);
        var store = new RedisRateLimitStore(_fx.Mux!, Prefix);
        var key = NewKey();

        for (int i = 0; i < 5; i++)
            Assert.True((await store.HitAsync(key, 5, Window, DateTimeOffset.UtcNow)).Allowed);

        var blocked = await store.HitAsync(key, 5, Window, DateTimeOffset.UtcNow);
        Assert.False(blocked.Allowed);
        Assert.True(blocked.RetryAfter > TimeSpan.Zero);
    }

    // Two stores on independent connections (simulating two pods) share ONE limit through Redis.
    // This is the scenario the in-memory store cannot satisfy.
    [SkippableFact]
    public async Task MultipleInstances_ShareASingleLimit()
    {
        Skip.IfNot(_fx.Available, "Redis not reachable at " + _fx.Config);
        using var mux2 = _fx.Connect();
        var pod1 = new RedisRateLimitStore(_fx.Mux!, Prefix);
        var pod2 = new RedisRateLimitStore(mux2, Prefix);
        var key = NewKey();

        int allowed = 0;
        for (int i = 0; i < 12; i++)
        {
            var store = (i % 2 == 0) ? pod1 : pod2;       // alternate between "pods"
            if ((await store.HitAsync(key, 5, Window, DateTimeOffset.UtcNow)).Allowed) allowed++;
        }

        Assert.Equal(5, allowed); // global limit honored regardless of which instance served the request
    }

    // The bucket key is given a TTL of 2 x window so idle keys expire automatically.
    [SkippableFact]
    public async Task SetsTtl_ToTwiceTheWindow()
    {
        Skip.IfNot(_fx.Available, "Redis not reachable at " + _fx.Config);
        var store = new RedisRateLimitStore(_fx.Mux!, Prefix);
        var key = NewKey();
        await store.HitAsync(key, 5, Window, DateTimeOffset.UtcNow);

        TimeSpan? ttl = _fx.Mux!.GetDatabase().KeyTimeToLive(Prefix + key);
        Assert.NotNull(ttl);
        Assert.True(ttl <= TimeSpan.FromTicks(Window.Ticks * 2) && ttl > Window);
    }

    // Atomic Lua guarantees no over-admission even under heavy parallel load on one key.
    [SkippableFact]
    public async Task Concurrency_DoesNotOverAdmit()
    {
        Skip.IfNot(_fx.Available, "Redis not reachable at " + _fx.Config);
        var store = new RedisRateLimitStore(_fx.Mux!, Prefix);
        var key = NewKey();

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => store.HitAsync(key, 5, Window, DateTimeOffset.UtcNow).AsTask()));

        Assert.Equal(5, results.Count(r => r.Allowed));
    }

    // Configuring via AddRedisRateLimiter(connectionString, keyPrefix) registers a working
    // store (same path the sample app uses) and the custom keyPrefix is applied to the keys.
    [SkippableFact]
    public async Task AddRedisRateLimiter_ConfiguresStore_AndAppliesKeyPrefix()
    {
        Skip.IfNot(_fx.Available, "Redis not reachable at " + _fx.Config);
        const string prefix = "cfg-rl:";
        var provider = new ServiceCollection()
            .AddRedisRateLimiter(_fx.Config, prefix)
            .BuildServiceProvider();

        var store = provider.GetRequiredService<IRateLimitStore>();
        var key = NewKey();

        for (int i = 0; i < 5; i++)
            Assert.True((await store.HitAsync(key, 5, Window, DateTimeOffset.UtcNow)).Allowed);
        Assert.False((await store.HitAsync(key, 5, Window, DateTimeOffset.UtcNow)).Allowed);

        // The configured prefix is the one actually used for the Redis key.
        Assert.True(_fx.Mux!.GetDatabase().KeyExists(prefix + key));
    }
}
