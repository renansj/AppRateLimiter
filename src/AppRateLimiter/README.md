# AppRateLimiter

Flexible, thread-safe **application-level rate limiting** for ASP.NET Core, packaged as a
single NuGet library. It limits by **client IP** (before authentication) and by **dynamic
JWT claims** (after authentication), and is built to avoid the common pitfalls: IDOR,
race conditions, and rate-limit bypasses.

Because it targets **`netstandard2.0`**, it works in any **ASP.NET Core** app, from the oldest
runtimes through the newest:

* .NET Framework 4.6.1+ — **via ASP.NET Core 2.1/2.2** (the last ASP.NET Core that runs on
  Full Framework). See `samples/Sample.NetFramework` (net472).
* .NET Core 2.0+
* .NET 5 / 6 / 7 / 8 / 9 / 10

This is the point: on those older targets there is no built-in limiter (native rate limiting
only arrived in .NET 7 via `System.Threading.RateLimiting`).

> It is **ASP.NET Core middleware**, so it plugs into the ASP.NET Core request pipeline. It is
> **not** for classic System.Web apps (WebForms / MVC 5 / Web API 2) — those don't have this
> middleware/`HttpContext` model.

---

## How it works

* **Algorithm:** sliding-window counter. Each key stores only the current and previous
  window counts (O(1) memory) and the effective rate is a weighted blend of both windows.
  This smooths the burst-at-the-boundary problem that plain fixed windows suffer from.
* **Concurrency:** every counter is updated under its own lock, so concurrent requests for
  the same key are serialized correctly (no lost increments / race conditions) while
  different keys never contend.
* **Memory safety:** a background sweep evicts idle keys, so an attacker rotating
  IPs/tokens cannot grow memory without bound.

---

## Install

```bash
dotnet add package AppRateLimiter
```

---

## Integration

### 1. Register the store

```csharp
builder.Services.AddAppRateLimiter();
```

### 2. Place the middleware in the pipeline

Order matters. Put **IP limiting first** (so unauthenticated floods are stopped before
any expensive work, including auth), then put **claim limiting right after authentication**
(so claims come from the validated token).

```csharp
var app = builder.Build();

// (A) IP limiting — BEFORE authentication, first real step of the request.
app.UseRateLimiting(
    RateLimitRules.ByIp(permitLimit: 100, window: TimeSpan.FromMinutes(1)));

app.UseAuthentication();
app.UseAuthorization();

// (B) Claim limiting — AFTER authentication. Define as many claims as you want.
app.UseRateLimiting(
    RateLimitRules.ByClaim("sub",       permitLimit: 1000, window: TimeSpan.FromMinutes(1)),
    RateLimitRules.ByClaim("tenant_id", permitLimit: 5000, window: TimeSpan.FromMinutes(1)),
    RateLimitRules.ByClaim("plan",      permitLimit: 60,   window: TimeSpan.FromSeconds(10)));

app.MapControllers();
app.Run();
```

> **Classic / legacy `Startup.cs`** uses the exact same calls inside `ConfigureServices`
> (`services.AddAppRateLimiter()`) and `Configure` (`app.UseRateLimiting(...)`).

When a limit is exceeded the request short-circuits with:

* `429 Too Many Requests`
* `Retry-After: <seconds>` header
* body `{"error":"rate_limit_exceeded","retryAfterSeconds":<n>}`

### Dynamic claims

`ByClaim` takes any claim type, so policies are data-driven — load limits from config/DB and
build the rule array at startup (or per request scope) however you like:

```csharp
var rules = config.GetSection("RateLimits")
    .Get<List<LimitConfig>>()
    .Select(c => RateLimitRules.ByClaim(c.Claim, c.Limit, TimeSpan.FromSeconds(c.WindowSeconds)))
    .ToArray();

app.UseRateLimiting(rules);
```

---

## Security notes

* **No IDOR / impersonation.** Claim rules read from `HttpContext.User`, which is populated
  by the authentication middleware from the *validated* JWT. The client cannot point the
  counter at someone else's bucket by sending arbitrary input. Claim rules are skipped for
  unauthenticated requests (those are already covered by the IP rule).
* **No `X-Forwarded-For` spoofing.** By default the limiter uses the real connection IP.
  If you run behind a reverse proxy/load balancer, pass your trusted proxy addresses so the
  header is honored only when the direct peer is actually one of them:

  ```csharp
  var trusted = new HashSet<IPAddress> { IPAddress.Parse("10.0.0.1") };
  app.UseRateLimiting(RateLimitRules.ByIp(100, TimeSpan.FromMinutes(1), trusted));
  ```

  The resolver then walks the forwarded chain right-to-left, skipping trusted hops, to find
  the genuine client. (Alternatively, configure ASP.NET Core's `ForwardedHeaders` middleware
  with `KnownProxies` and just use the default connection IP.)
* **No bypass via concurrency.** Counters are updated atomically under a per-key lock.
* **IPv6 rotation is contained.** IPv6 clients are keyed by their `/64` prefix (and
  IPv4-mapped addresses fold to plain IPv4), so a client can't rotate through its `/64` to get
  unlimited fresh buckets or flood the store with keys.
* **Use stable, server-asserted claims.** Claim limiting is only as trustworthy as the claim.
  Key on identifiers your IdP sets and the user can't freely change (e.g. `sub`, a server-issued
  `tenant_id`). Limiting on a user-mutable claim would let a caller rotate its value for fresh
  buckets.
* **Fails closed.** If the store (e.g. Redis) is unreachable the request errors rather than
  silently skipping the limit — an outage can't be used to bypass limiting.

---

## Distributed / multi-instance deployments (e.g. EKS pods)

The in-memory store is **per-process**, so with multiple replicas the effective limit becomes
`limit × replicas` and a client load-balanced across pods can dodge any single pod's counter.
For that case use the companion package **`AppRateLimiter.Redis`**, which keeps one shared
counter in Redis:

```csharp
// Use this INSTEAD of AddAppRateLimiter when running multiple instances.
builder.Services.AddRedisRateLimiter("my-redis:6379");
```

How it stays correct and fast:

* **Race-free across pods.** The whole sliding-window read-modify-write runs in a single Lua
  script, which Redis executes atomically — no cross-pod race, no over-admission.
* **One round-trip per check.** The script is cached server-side (EVALSHA), so each decision
  is a single pipelined round-trip on the shared multiplexer — typically sub-millisecond
  inside the same VPC/AZ. The store is fully async, so it never blocks thread-pool threads.
* **No pod clock skew.** Time comes from the Redis server clock (`TIME`), not each pod's clock.
* **TTL = 2 × window.** Each bucket is given `PEXPIRE = 2 × window` (the sliding-window counter
  only needs the current and previous window), so idle keys expire automatically and memory
  stays bounded — no manual cleanup.

You can also plug any other backend by implementing `IRateLimitStore` yourself.

### Securing Redis (production)

The rate-limit keys hold client IPs and claim values (e.g. `sub`), so treat the Redis instance
as sensitive infrastructure:

* **Keep it private.** Never expose Redis to the public internet. Put it in a private subnet and
  allow inbound `6379` only from the app's security group. On EKS, prefer a managed endpoint
  (e.g. ElastiCache / MemoryDB) reachable only from the cluster's security group. Leave
  `protected-mode` on — `--bind 0.0.0.0 --protected-mode no` (used in the tests) is for local
  dev only.
* **Require authentication, least-privileged.** Use a Redis ACL user that can run only the
  commands this store needs — `EVAL`, `EVALSHA`, `SCRIPT`, `HMGET`, `HSET`, `PEXPIRE`, `TIME`,
  `PING` — instead of the default user. Example ACL:

  ```
  ACL SETUSER ratelimiter on >REPLACE_WITH_STRONG_SECRET ~rl:* +eval +evalsha +script +hmget +hset +pexpire +time +ping
  ```
* **Encrypt in transit (TLS).** Enable `ssl=true` and a real hostname; enable in-transit and
  at-rest encryption on managed Redis.
* **Never hardcode the connection string.** Load it from a secret (Kubernetes `Secret` mounted
  as env/`appsettings`, or AWS Secrets Manager), not from source:

  ```csharp
  builder.Services.AddRedisRateLimiter(
      builder.Configuration.GetConnectionString("Redis")!,   // e.g. from a mounted secret
      keyPrefix: "rl:prod:");                                 // namespace keys per app/env
  ```

  A secured connection string looks like:
  `my-redis.internal:6380,ssl=true,user=ratelimiter,password=<secret>,abortConnect=false`
* **Namespace the keyspace.** Use a distinct `keyPrefix` per app/environment so multiple
  services can safely share one cluster and ACL rules (`~rl:*`) stay scoped.

---

## Build the package locally

```bash
dotnet pack -c Release
# -> bin/Release/AppRateLimiter.1.0.0.nupkg
```
