using System;
using System.Threading.Tasks;

namespace AppRateLimiter
{
    /// <summary>
    /// Atomic counter store. The default is <see cref="InMemoryRateLimitStore"/>; implement this
    /// for a distributed backend (e.g. Redis) so every instance shares one counter.
    /// Returns a <see cref="ValueTask{T}"/> so in-memory hits complete without allocation while
    /// I/O-backed stores can run asynchronously.
    /// </summary>
    public interface IRateLimitStore
    {
        /// <summary>Registers a hit and returns whether it is allowed. Must be atomic/thread-safe.</summary>
        ValueTask<RateLimitResult> HitAsync(string key, int permitLimit, TimeSpan window, DateTimeOffset now);
    }
}
