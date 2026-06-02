using System;
using Microsoft.AspNetCore.Http;

namespace AppRateLimiter
{
    /// <summary>
    /// A single rate-limit policy. <paramref name="KeySelector"/> returns the partition key
    /// for the request, or <c>null</c> to skip the rule (e.g. claim missing / not authenticated).
    /// </summary>
    public sealed class RateLimitRule
    {
        public string Name { get; }
        public int PermitLimit { get; }
        public TimeSpan Window { get; }
        public Func<HttpContext, string?> KeySelector { get; }

        public RateLimitRule(string name, int permitLimit, TimeSpan window, Func<HttpContext, string?> keySelector)
        {
            if (permitLimit <= 0) throw new ArgumentOutOfRangeException(nameof(permitLimit));
            if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            PermitLimit = permitLimit;
            Window = window;
            KeySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        }
    }
}
