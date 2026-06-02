using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace AppRateLimiter
{
    /// <summary>Built-in rule factories. Compose any number of these per pipeline stage.</summary>
    public static class RateLimitRules
    {
        /// <summary>Limit by client IP. Register this BEFORE authentication.</summary>
        public static RateLimitRule ByIp(int permitLimit, TimeSpan window,
            ISet<IPAddress>? trustedProxies = null, string name = "ip")
        {
            return new RateLimitRule(name, permitLimit, window, ctx =>
            {
                string? ip = IpAddressResolver.GetClientIp(ctx, trustedProxies);
                return string.IsNullOrEmpty(ip) ? null : ip;
            });
        }

        /// <summary>
        /// Limit by a JWT claim. Register this AFTER authentication so the claim is taken from
        /// the validated <see cref="HttpContext.User"/> and never from client-controlled input
        /// (prevents IDOR / impersonation of another principal's bucket). Unauthenticated requests
        /// are skipped by this rule and remain covered by the IP rule.
        /// </summary>
        public static RateLimitRule ByClaim(string claimType, int permitLimit, TimeSpan window, string? name = null)
        {
            if (string.IsNullOrEmpty(claimType)) throw new ArgumentNullException(nameof(claimType));
            return new RateLimitRule(name ?? "claim:" + claimType, permitLimit, window, ctx =>
            {
                if (ctx.User?.Identity?.IsAuthenticated != true) return null;
                string? value = ctx.User.FindFirst(claimType)?.Value;
                return string.IsNullOrEmpty(value) ? null : value;
            });
        }
    }
}
