using System;

namespace AppRateLimiter
{
    /// <summary>Outcome of a single rate-limit check.</summary>
    public readonly struct RateLimitResult
    {
        public bool Allowed { get; }
        public int Remaining { get; }
        public TimeSpan RetryAfter { get; }

        public RateLimitResult(bool allowed, int remaining, TimeSpan retryAfter)
        {
            Allowed = allowed;
            Remaining = remaining;
            RetryAfter = retryAfter;
        }
    }
}
