using System.Text;
using AppRateLimiter;
using AppRateLimiter.Redis;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var cfg = builder.Configuration;
// Require a real signing key outside Development. Never fall back to a hardcoded key in prod —
// a key that lives in source means anyone can forge tokens.
var key = cfg["Jwt:Key"];
if (string.IsNullOrEmpty(key))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Configure Jwt:Key (e.g. from a secret) outside Development.");
    key = "dev-only-signing-key-change-me-please-0123456789";
}
var issuer = cfg["Jwt:Issuer"] ?? "sample-issuer";
var audience = cfg["Jwt:Audience"] ?? "sample-audience";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep original JWT claim names (e.g. "sub") instead of remapping them to long URIs,
        // so claim-based rate limiting can target the raw claim types directly.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateLifetime = true,
        };
    });
builder.Services.AddAuthorization();

// Register the rate limiter. Use Redis across multiple instances (e.g. EKS pods); fall back to
// the in-memory store for single-instance/dev when no Redis connection is configured.
var redisCfg = cfg["Redis:Configuration"];
if (string.IsNullOrEmpty(redisCfg))
    builder.Services.AddAppRateLimiter();
else
    builder.Services.AddRedisRateLimiter(redisCfg, cfg["Redis:KeyPrefix"] ?? "rl:");

var app = builder.Build();

// (A) IP rate limiting runs FIRST, before authentication, so anonymous floods are
// rejected as early as possible.
app.UseRateLimiting(RateLimitRules.ByIp(permitLimit: 5, window: TimeSpan.FromMinutes(1)));

app.UseAuthentication();
app.UseAuthorization();

// (B) Claim-based rate limiting runs AFTER authentication, reading the validated identity.
// Add as many dynamic claims as needed.
app.UseRateLimiting(
    RateLimitRules.ByClaim("sub", permitLimit: 3, window: TimeSpan.FromMinutes(1)),
    RateLimitRules.ByClaim("tenant_id", permitLimit: 4, window: TimeSpan.FromMinutes(1)));

app.MapGet("/public", () => "public ok");                       // anonymous: IP-limited only
app.MapGet("/secure", () => "secure ok").RequireAuthorization(); // authenticated: claim-limited

app.Run();

// Exposes the implicit Program class to the integration test project.
public partial class Program { }
