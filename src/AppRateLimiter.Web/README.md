# AppRateLimiter.Web

Classic ASP.NET (System.Web) integration for [AppRateLimiter](https://www.nuget.org/packages/AppRateLimiter).

The core middleware targets ASP.NET Core, so it covers modern .NET and ASP.NET Core 2.x on .NET Framework. This package adds an async `IHttpModule` for the classic `System.Web` pipeline, which is what WebForms, MVC 5, and Web API 2 use. That is the common legacy Windows scenario: an IIS web farm behind a load balancer. Point the module at the Redis store and every server in the farm shares one global counter.

## Install

```bash
dotnet add package AppRateLimiter.Web
```

Targets net472. It brings in the core `AppRateLimiter`, and `AppRateLimiter.Redis` for the distributed store.

## Configure (Global.asax)

Classic modules cannot use dependency injection, so you supply the store and rules once at startup through a static entry point.

```csharp
using System;
using AppRateLimiter;
using AppRateLimiter.Redis;
using AppRateLimiter.Web;

public class Global : System.Web.HttpApplication
{
    protected void Application_Start()
    {
        // Single server: in-memory store.
        IRateLimitStore store = new InMemoryRateLimitStore();

        // IIS web farm behind a load balancer: shared Redis store instead, so the limit is
        // global across all servers.
        // IRateLimitStore store = new RedisRateLimitStore(
        //     StackExchange.Redis.ConnectionMultiplexer.Connect("my-redis:6379"), "rl:");

        RateLimitHttpModule.Configure(
            store,
            ipRules: new[]
            {
                WebRateLimitRules.ByIp(permitLimit: 100, window: TimeSpan.FromMinutes(1)),
            },
            claimRules: new[]
            {
                WebRateLimitRules.ByClaim("sub", permitLimit: 1000, window: TimeSpan.FromMinutes(1)),
            });
    }
}
```

## Register the module (web.config)

```xml
<configuration>
  <system.webServer>
    <modules>
      <add name="AppRateLimiter"
           type="AppRateLimiter.Web.RateLimitHttpModule, AppRateLimiter.Web" />
    </modules>
  </system.webServer>
</configuration>
```

That is all. IP rules run before authentication on `BeginRequest`, and claim rules run after authentication on `PostAuthenticateRequest`, reading the validated `HttpContext.User`.

## When a limit is exceeded

The request short-circuits with the same contract as the ASP.NET Core middleware:

* `429 Too Many Requests`
* `Retry-After: <seconds>` header
* body `{"error":"rate_limit_exceeded","retryAfterSeconds":<n>}`

## What it preserves

This adapter keeps the same security properties as the core:

* **Atomic counting, no over-admission.** It calls the same `IRateLimitStore` (in-memory or the atomic Redis Lua script), and awaits `HitAsync` through `EventHandlerTaskAsyncHelper` rather than blocking a thread.
* **Claims from the validated identity only.** `ByClaim` reads from `HttpContext.User` after authentication and skips unauthenticated requests, so a client cannot point a counter at another principal's bucket.
* **No X-Forwarded-For spoofing.** The client IP comes from the connection. `X-Forwarded-For` is honored only when the direct peer is one of the trusted proxies you pass in, walking the chain right to left and skipping trusted hops.
* **IPv6 rotation contained.** IPv6 clients are keyed by their /64 prefix, and IPv4-mapped addresses fold to plain IPv4, exactly like the core.
* **Same key namespacing.** Rules use the same name based key separator as the core, so when the module and the ASP.NET Core middleware share one store they also share buckets.

## Authentication note

Populate `HttpContext.User` with a `ClaimsPrincipal` before `PostAuthenticateRequest` completes (for example via your existing forms/JWT/OWIN authentication). `ByClaim` reads claims with `ClaimsPrincipal.FindFirst(type)`.
