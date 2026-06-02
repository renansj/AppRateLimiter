using System;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace AppRateLimiter.Redis
{
    public static class RedisRateLimiterExtensions
    {
        /// <summary>
        /// Registers the Redis-backed distributed store. Use this INSTEAD of AddAppRateLimiter
        /// when running multiple instances (e.g. several EKS pods) so they share one counter.
        /// </summary>
        public static IServiceCollection AddRedisRateLimiter(
            this IServiceCollection services, string configuration, string keyPrefix = "rl:")
        {
            var mux = ConnectionMultiplexer.Connect(configuration);
            return services.AddRedisRateLimiter(mux, keyPrefix);
        }

        /// <summary>Registers the Redis-backed store using an existing connection multiplexer.</summary>
        public static IServiceCollection AddRedisRateLimiter(
            this IServiceCollection services, IConnectionMultiplexer multiplexer, string keyPrefix = "rl:")
        {
            services.AddSingleton(multiplexer);
            services.AddSingleton<IRateLimitStore>(_ => new RedisRateLimitStore(multiplexer, keyPrefix));
            return services;
        }
    }
}
