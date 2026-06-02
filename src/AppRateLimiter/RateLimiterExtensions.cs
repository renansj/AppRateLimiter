using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppRateLimiter
{
    public static class RateLimiterExtensions
    {
        /// <summary>Registers the default in-memory store. Call once in ConfigureServices.</summary>
        public static IServiceCollection AddAppRateLimiter(this IServiceCollection services)
        {
            services.TryAddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
            return services;
        }

        /// <summary>
        /// Adds a rate-limiting stage with the given rules. Register before
        /// <c>UseAuthentication</c> for IP rules and after it for claim rules.
        /// </summary>
        public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app, params RateLimitRule[] rules)
        {
            return app.UseMiddleware<RateLimitMiddleware>((object)rules);
        }
    }
}
