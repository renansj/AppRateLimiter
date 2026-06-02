# AppRateLimiter — solution

Application-level rate limiting for ASP.NET Core, shipped as a NuGet package, plus a runnable
sample and an integration test suite.

```
src/AppRateLimiter ............ the library (NuGet package, targets netstandard2.0)
src/AppRateLimiter.Redis ...... distributed store for multi-instance deployments (Redis)
samples/Sample.Api ............ minimal API showing IP + JWT-claim limiting in the right order
samples/Sample.NetFramework ... the SAME library on .NET Framework 4.7.2 (ASP.NET Core 2.2)
tests/AppRateLimiter.IntegrationTests ....... end-to-end tests over the sample (in-memory)
tests/AppRateLimiter.Redis.Tests ............ distributed store tests (need a Redis)
```

For library usage and integration instructions, see
[`src/AppRateLimiter/README.md`](src/AppRateLimiter/README.md) (also the NuGet package readme).

## Sample

`samples/Sample.Api` wires everything together:

* JWT bearer authentication with `MapInboundClaims = false` so claim names (e.g. `sub`) are
  kept verbatim and can be used directly as rate-limit keys.
* IP limiting (`5/min`) registered **before** `UseAuthentication`.
* Claim limiting (`sub` = `3/min`, `tenant_id` = `4/min`) registered **after** authentication.
* `GET /public` — anonymous, IP-limited only.
* `GET /secure` — requires a valid JWT, claim-limited.

```bash
dotnet run --project samples/Sample.Api
```

> The sample uses a dev-only default signing key. Supply `Jwt:Key` (and `Jwt:Issuer` /
> `Jwt:Audience`) via configuration or user-secrets for anything real.

`samples/Sample.NetFramework` proves the legacy story: the **same** library and API
(`AddAppRateLimiter` / `UseRateLimiting` / `RateLimitRules`) on **.NET Framework 4.7.2** using
ASP.NET Core 2.2's classic `Startup`. Only the host and JWT plumbing differ (on 2.x you clear
`JwtSecurityTokenHandler.DefaultInboundClaimTypeMap` instead of `MapInboundClaims`). Note: this
is ASP.NET Core middleware, so it targets ASP.NET Core apps on Full Framework — not classic
System.Web (WebForms / MVC 5).

```bash
dotnet build samples/Sample.NetFramework   # builds a net472 executable
```

## Run the tests

```bash
dotnet test
```

The integration tests boot the sample with `WebApplicationFactory` and drive the full
middleware pipeline (the test harness sets the client IP and `Authorization` header per
request via `TestServer.SendAsync`). They cover every required scenario:

| Test | Requirement covered |
|------|---------------------|
| `Ip_RejectsAfterLimit_AndReturnsRetryAfter` | IP limiting before auth; `429` + `Retry-After` |
| `Ip_DifferentClients_AreIndependent` | per-IP isolation |
| `Jwt_Subject_RejectsAfterLimit` | JWT/claim limiting after auth, keyed on validated `sub` |
| `Jwt_DifferentSubjects_AreIndependent` | per-principal quota isolation (no IDOR cross-impact) |
| `Claims_TenantLimit_RejectsAcrossDistinctSubjects` | a second, dynamically defined claim limit |
| `Jwt_ForgedToken_IsUnauthorized` | forged identity is rejected before the limiter (no bypass) |
| `Secure_WithoutToken_IsUnauthorized` | claim rule skipped for anonymous; auth enforced |
| `Concurrency_DoesNotOverAdmit` | exactly the limit admitted under parallel load (no race condition) |

## Distributed (multiple pods / EKS)

In-memory counters are per-process, so across replicas the limit multiplies and clients can
spread requests across pods to bypass it. Use `AppRateLimiter.Redis` so all instances share
one counter:

```csharp
builder.Services.AddRedisRateLimiter("my-redis:6379"); // instead of AddAppRateLimiter()
```

The sample switches to Redis automatically when `Redis:Configuration` is set:

```bash
dotnet run --project samples/Sample.Api --Redis:Configuration=localhost:6379
```

The whole sliding-window calculation runs in one atomic Lua script (race-free across pods),
uses the Redis server clock (no pod clock skew), and sets `PEXPIRE = 2 × window` as the TTL so
idle buckets expire on their own. One cached, pipelined round-trip per check keeps it fast, and
the store is async so it never blocks threads.

For production hardening (private networking, ACL/least-privilege auth, TLS, secrets), see the
**Securing Redis** section in [`src/AppRateLimiter/README.md`](src/AppRateLimiter/README.md).

### Running the Redis tests

They need a reachable Redis (`REDIS` env var, default `localhost:6379`) and **skip** if none is
found. Use whichever is easiest:

```bash
# Recommended: Docker — stable published port on localhost.
docker run -d -p 6379:6379 redis

# Windows without Docker: any native build listening on localhost:6379 works
# (e.g. Memurai, or a portable redis-server.exe run as a background process).

# WSL works too, but ONLY while the distro stays alive: it forwards localhost:6379 only
# while WSL is active, so the daemon stops reaching the host once the distro idles out.
# Run it in the SAME session as the tests (local dev only — not a secure config):
wsl -u root -- bash -lc "redis-server --daemonize yes --bind 0.0.0.0 --protected-mode no"

dotnet test    # runs every project; the Redis tests skip if none is reachable
```

Store-level tests (`tests/AppRateLimiter.Redis.Tests`):

| Test | What it proves |
|------|----------------|
| `SingleInstance_EnforcesLimit` | the Redis sliding window enforces the limit + `Retry-After` |
| `MultipleInstances_ShareASingleLimit` | two independent connections (≈ two pods) share one global limit |
| `SetsTtl_ToTwiceTheWindow` | each bucket gets TTL = 2 × window |
| `Concurrency_DoesNotOverAdmit` | atomic Lua admits exactly the limit under parallel load |

Full-pipeline multi-pod tests (`tests/AppRateLimiter.IntegrationTests`): two `WebApplicationFactory`
instances (= two pods) share one Redis and are driven over HTTP:

| Test | What it proves |
|------|----------------|
| `TwoPods_ShareGlobalIpLimit` | the IP limit is enforced globally across both pods |
| `TwoPods_ShareGlobalJwtSubjectLimit` | the JWT `sub` limit is enforced globally across both pods |
