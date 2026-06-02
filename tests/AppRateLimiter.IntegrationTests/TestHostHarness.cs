using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.Tokens;

namespace AppRateLimiter.IntegrationTests;

/// <summary>
/// Shared test harness. Each test class gets its own factory (and therefore its own in-memory
/// store), so state is isolated between test classes. The JWT signing key/issuer/audience are
/// forced to known test values so the harness can mint valid tokens.
/// </summary>
public sealed class TestHostHarness : IDisposable
{
    public const string Key = "integration-test-signing-key-0123456789-abcdef";
    public const string Issuer = "sample-issuer";
    public const string Audience = "sample-audience";

    private readonly WebApplicationFactory<Program> _factory;

    public TestHostHarness()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Jwt:Key", Key)
             .UseSetting("Jwt:Issuer", Issuer)
             .UseSetting("Jwt:Audience", Audience));
    }

    /// <summary>Builds a request with a chosen client IP and optional bearer token.</summary>
    public Task<(int Status, string RetryAfter)> SendAsync(string path, string ip, string? token = null)
        => SendAsync(_factory.Server, path, ip, token);

    /// <summary>Same as above but against any server, so several "pod" factories can share it.</summary>
    public static async Task<(int Status, string RetryAfter)> SendAsync(
        TestServer server, string path, string ip, string? token = null)
    {
        var context = await server.SendAsync(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("localhost");
            ctx.Request.Path = path;
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            if (token != null) ctx.Request.Headers["Authorization"] = "Bearer " + token;
        });
        return (context.Response.StatusCode, context.Response.Headers["Retry-After"].ToString());
    }

    public static string Jwt(params Claim[] claims)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Audience, claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(30), creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>A token signed with the wrong key, used to prove forged tokens never reach the limiter.</summary>
    public static string ForgedJwt(params Claim[] claims)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("totally-different-wrong-key-0123456789-xyz")),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Audience, claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(30), creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose() => _factory.Dispose();
}
