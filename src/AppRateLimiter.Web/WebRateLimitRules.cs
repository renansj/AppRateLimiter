using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Web;

namespace AppRateLimiter.Web
{
    /// <summary>
    /// Built-in rule factories for classic System.Web, mirroring the core
    /// <see cref="RateLimitRules"/>. Same keying and security properties; only the context type
    /// differs (<see cref="HttpContextBase"/> instead of ASP.NET Core's HttpContext).
    /// </summary>
    public static class WebRateLimitRules
    {
        /// <summary>Limit by client IP. Apply this BEFORE authentication (BeginRequest).</summary>
        public static WebRateLimitRule ByIp(int permitLimit, TimeSpan window,
            ISet<IPAddress>? trustedProxies = null, string name = "ip")
        {
            return new WebRateLimitRule(name, permitLimit, window, ctx =>
            {
                IPAddress? client = ResolveIp(ctx, trustedProxies);
                return client == null ? null : IpAddressResolver.NormalizeIp(client);
            });
        }

        /// <summary>
        /// Limit by a claim. Apply this AFTER authentication (PostAuthenticateRequest) so the
        /// claim is taken from the validated <see cref="HttpContext.User"/> and never from
        /// client-controlled input (prevents IDOR / impersonation). Unauthenticated requests are
        /// skipped and remain covered by the IP rule.
        /// </summary>
        public static WebRateLimitRule ByClaim(string claimType, int permitLimit, TimeSpan window, string? name = null)
        {
            if (string.IsNullOrEmpty(claimType)) throw new ArgumentNullException(nameof(claimType));
            return new WebRateLimitRule(name ?? "claim:" + claimType, permitLimit, window, ctx =>
            {
                if (ctx.User?.Identity?.IsAuthenticated != true) return null;
                string? value = (ctx.User as ClaimsPrincipal)?.FindFirst(claimType)?.Value;
                return string.IsNullOrEmpty(value) ? null : value;
            });
        }

        // X-Forwarded-For is honored only when the direct peer is a configured trusted proxy;
        // we then walk the chain right-to-left skipping trusted hops to find the real client.
        private static IPAddress? ResolveIp(HttpContextBase ctx, ISet<IPAddress>? trustedProxies)
        {
            HttpRequestBase request = ctx.Request;
            IPAddress? remote = IPAddress.TryParse(request.UserHostAddress, out IPAddress? r) ? r : null;

            if (remote != null && trustedProxies != null && trustedProxies.Count > 0 && trustedProxies.Contains(remote))
            {
                string? forwarded = request.Headers["X-Forwarded-For"];
                if (!string.IsNullOrEmpty(forwarded))
                {
                    string[] hops = forwarded!.Split(',');
                    for (int i = hops.Length - 1; i >= 0; i--)
                    {
                        if (IPAddress.TryParse(hops[i].Trim(), out IPAddress? parsed) && !trustedProxies.Contains(parsed))
                            return parsed;
                    }
                }
            }
            return remote;
        }
    }
}
