using System;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace AppRateLimiter.Redis
{
    /// <summary>
    /// Distributed store backed by Redis. The entire sliding-window-counter read-modify-write
    /// runs inside one Lua script, which Redis executes atomically — so concurrent requests from
    /// any number of instances cannot race. Time is taken from the Redis server clock (via TIME)
    /// to avoid per-pod clock skew, and each key gets PEXPIRE = 2 x window so idle buckets
    /// auto-expire (bounded memory, no manual cleanup).
    /// </summary>
    public sealed class RedisRateLimitStore : IRateLimitStore
    {
        // KEYS[1] = bucket key, ARGV[1] = limit, ARGV[2] = window length in ms.
        // Returns { allowed (0/1), retryAfterMs }.
        private const string Lua = @"
local limit = tonumber(ARGV[1])
local windowMs = tonumber(ARGV[2])
local t = redis.call('TIME')
local nowMs = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
local current = math.floor(nowMs / windowMs)
local d = redis.call('HMGET', KEYS[1], 'w', 'c', 'p')
local w = tonumber(d[1])
local c = tonumber(d[2]) or 0
local p = tonumber(d[3]) or 0
if w == nil then w = current end
local delta = current - w
if delta == 1 then p = c; c = 0
elseif delta > 1 then p = 0; c = 0 end
local frac = (nowMs % windowMs) / windowMs
local estimated = (p * (1 - frac)) + c
local allowed = 0
local retry = 0
if estimated < limit then
  c = c + 1
  allowed = 1
else
  retry = windowMs - (nowMs % windowMs)
end
redis.call('HSET', KEYS[1], 'w', current, 'c', c, 'p', p)
redis.call('PEXPIRE', KEYS[1], windowMs * 2)
return { allowed, retry }";

        private readonly IDatabase _db;
        private readonly string _prefix;

        public RedisRateLimitStore(IConnectionMultiplexer multiplexer, string keyPrefix = "rl:")
        {
            _db = multiplexer.GetDatabase();
            _prefix = keyPrefix;
        }

        public async ValueTask<RateLimitResult> HitAsync(string key, int permitLimit, TimeSpan window, DateTimeOffset now)
        {
            long windowMs = (long)window.TotalMilliseconds;
            // ScriptEvaluate caches the script and uses EVALSHA, so only the hash travels per call.
            RedisResult raw = await _db.ScriptEvaluateAsync(
                Lua,
                new RedisKey[] { _prefix + key },
                new RedisValue[] { permitLimit, windowMs }).ConfigureAwait(false);

            RedisResult[] result = (RedisResult[])raw!;
            bool allowed = (long)result[0] == 1;
            long retryMs = (long)result[1];
            return new RateLimitResult(allowed, 0, TimeSpan.FromMilliseconds(retryMs));
        }
    }
}
