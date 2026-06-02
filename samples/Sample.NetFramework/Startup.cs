using System;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using AppRateLimiter;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Sample.NetFramework
{
    // ASP.NET Core 2.2 on .NET Framework 4.7.2 — the classic Startup hosting model.
    // Same library, same API as the modern sample; only the host/JWT plumbing differs.
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            // On ASP.NET Core 2.x JwtBearer uses JwtSecurityTokenHandler, so the legacy way to
            // keep raw claim names (e.g. "sub") is clearing the inbound map (there is no
            // MapInboundClaims option on this version).
            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = "sample-issuer",
                        ValidateAudience = true,
                        ValidAudience = "sample-audience",
                        ValidateIssuerSigningKey = true,
                        // dev-only demo key; load from configuration/secret in production.
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes("dev-only-signing-key-change-me-please-0123456789")),
                    };
                });

            services.AddAppRateLimiter();
        }

        public void Configure(IApplicationBuilder app)
        {
            // IP limiting before authentication.
            app.UseRateLimiting(RateLimitRules.ByIp(permitLimit: 5, window: TimeSpan.FromMinutes(1)));
            app.UseAuthentication();
            // Claim limiting after authentication.
            app.UseRateLimiting(RateLimitRules.ByClaim("sub", permitLimit: 3, window: TimeSpan.FromMinutes(1)));

            app.Run(ctx => ctx.Response.WriteAsync("ok"));
        }
    }
}
