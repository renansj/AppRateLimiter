using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace AppRateLimiter
{
    /// <summary>
    /// Resolves a stable rate-limit key from the client IP without trusting spoofable headers.
    /// X-Forwarded-For is only honored when the direct connection comes from a configured
    /// trusted proxy; we then walk the chain right-to-left skipping trusted hops. IPv6 clients
    /// are collapsed to their /64 prefix, because a single client usually controls a whole /64
    /// and could otherwise rotate addresses to dodge the limit (and exhaust the store).
    /// </summary>
    public static class IpAddressResolver
    {
        public static string? GetClientIp(HttpContext context, ISet<IPAddress>? trustedProxies)
        {
            IPAddress? client = Resolve(context, trustedProxies);
            return client == null ? null : ToKey(client);
        }

        private static IPAddress? Resolve(HttpContext context, ISet<IPAddress>? trustedProxies)
        {
            IPAddress? remote = context.Connection.RemoteIpAddress;
            if (remote != null && trustedProxies != null && trustedProxies.Count > 0 && trustedProxies.Contains(remote))
            {
                string forwarded = context.Request.Headers["X-Forwarded-For"];
                if (!string.IsNullOrEmpty(forwarded))
                {
                    string[] hops = forwarded.Split(',');
                    for (int i = hops.Length - 1; i >= 0; i--)
                    {
                        if (IPAddress.TryParse(hops[i].Trim(), out IPAddress? parsed) && !trustedProxies.Contains(parsed))
                            return parsed;
                    }
                }
            }
            return remote;
        }

        private static string ToKey(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            if (b.Length != 16) return ip.ToString();          // IPv4 -> /32
            if (IsIPv4Mapped(b))                                // ::ffff:a.b.c.d -> a.b.c.d
                return new IPAddress(new[] { b[12], b[13], b[14], b[15] }).ToString();
            for (int i = 8; i < 16; i++) b[i] = 0;              // IPv6 -> /64 prefix
            return new IPAddress(b).ToString() + "/64";
        }

        private static bool IsIPv4Mapped(byte[] b)
        {
            for (int i = 0; i < 10; i++) if (b[i] != 0) return false;
            return b[10] == 0xff && b[11] == 0xff;
        }
    }
}
