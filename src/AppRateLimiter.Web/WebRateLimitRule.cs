using System;
using System.Web;

namespace AppRateLimiter.Web
{
    /// <summary>
    /// A single rate-limit policy for classic System.Web. Mirrors the core
    /// <see cref="RateLimitRule"/> but selects its key from an <see cref="HttpContextBase"/>.
    /// <paramref name="KeySelector"/> returns the partition key, or <c>null</c> to skip the rule
    /// (e.g. claim missing / not authenticated).
    /// </summary>
    public sealed class WebRateLimitRule
    {
        public string Name { get; }
        public int PermitLimit { get; }
        public TimeSpan Window { get; }
        public Func<HttpContextBase, string?> KeySelector { get; }

        public WebRateLimitRule(string name, int permitLimit, TimeSpan window, Func<HttpContextBase, string?> keySelector)
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
