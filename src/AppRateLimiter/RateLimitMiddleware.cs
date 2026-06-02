using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace AppRateLimiter
{
    /// <summary>
    /// Evaluates a set of rules for the current request. Registered once per pipeline stage:
    /// IP rules before authentication, claim rules after. The first rule that is exceeded
    /// short-circuits the request with HTTP 429.
    /// </summary>
    public sealed class RateLimitMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IReadOnlyList<RateLimitRule> _rules;
        private readonly IRateLimitStore _store;

        public RateLimitMiddleware(RequestDelegate next, IReadOnlyList<RateLimitRule> rules, IRateLimitStore store)
        {
            _next = next;
            _rules = rules;
            _store = store;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < _rules.Count; i++)
            {
                RateLimitRule rule = _rules[i];
                string? key = rule.KeySelector(context);
                if (key == null) continue;

                // Namespacing by rule name isolates buckets so one rule's keys cannot collide
                // with another's.
                RateLimitResult result = await _store
                    .HitAsync(rule.Name + "\u001f" + key, rule.PermitLimit, rule.Window, now)
                    .ConfigureAwait(false);
                if (!result.Allowed)
                {
                    await Reject(context, result).ConfigureAwait(false);
                    return;
                }
            }
            await _next(context).ConfigureAwait(false);
        }

        private static Task Reject(HttpContext context, RateLimitResult result)
        {
            int seconds = (int)Math.Ceiling(result.RetryAfter.TotalSeconds);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = seconds.ToString(CultureInfo.InvariantCulture);
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(
                "{\"error\":\"rate_limit_exceeded\",\"retryAfterSeconds\":" + seconds + "}");
        }
    }
}
