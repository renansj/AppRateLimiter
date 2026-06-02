using StackExchange.Redis;

namespace AppRateLimiter.Redis.Tests;

/// <summary>
/// Connects once to Redis (REDIS env var, or localhost:6379). Exposes Available so tests can
/// skip gracefully when no Redis is reachable, and run for real when it is.
/// </summary>
public sealed class RedisFixture : IDisposable
{
    public string Config { get; }
    public IConnectionMultiplexer? Mux { get; }
    public bool Available { get; }

    public RedisFixture()
    {
        // abortConnect defaults to true, so Connect blocks until connected or throws — a
        // deterministic availability gate (no IsConnected race against a background connect).
        Config = (Environment.GetEnvironmentVariable("REDIS") ?? "localhost:6379") + ",connectTimeout=5000,connectRetry=3";
        try
        {
            Mux = ConnectionMultiplexer.Connect(Config);
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    public IConnectionMultiplexer Connect() => ConnectionMultiplexer.Connect(Config);

    public void Dispose() => Mux?.Dispose();
}
