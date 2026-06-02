# AppRateLimiter.Redis

Redis-backed distributed store for [AppRateLimiter](https://www.nuget.org/packages/AppRateLimiter).

Use this when you run more than one instance of your app (for example several Kubernetes or EKS pods). The default in-memory store counts requests per process, so with N replicas the effective limit becomes `limit x N`, and a client load balanced across pods can slip past any single pod's counter. This package keeps one shared counter in Redis so the limit holds globally, no matter which instance serves each request.

## Install

```bash
dotnet add package AppRateLimiter.Redis
```

This package depends on `AppRateLimiter`, so the core middleware comes with it.

## Usage

Register the Redis store instead of `AddAppRateLimiter()`. Everything else (the middleware, the IP and claim rules) works exactly as documented in the core package.

```csharp
// Connect with a configuration string:
builder.Services.AddRedisRateLimiter("my-redis:6379");

// Optionally namespace the keys (defaults to "rl:"):
builder.Services.AddRedisRateLimiter("my-redis:6379", keyPrefix: "rl:prod:");
```

If you already manage your own connection, pass an existing `IConnectionMultiplexer` and the store will reuse it:

```csharp
var mux = ConnectionMultiplexer.Connect("my-redis:6379");
builder.Services.AddRedisRateLimiter(mux, keyPrefix: "rl:prod:");
```

Then place the middleware in the pipeline the same way as with the in-memory store:

```csharp
var app = builder.Build();

// IP limiting before authentication.
app.UseRateLimiting(RateLimitRules.ByIp(permitLimit: 100, window: TimeSpan.FromMinutes(1)));

app.UseAuthentication();
app.UseAuthorization();

// Claim limiting after authentication.
app.UseRateLimiting(RateLimitRules.ByClaim("sub", permitLimit: 1000, window: TimeSpan.FromMinutes(1)));

app.Run();
```

## How it stays correct and fast

* **Race free across pods.** The whole sliding window read modify write runs inside a single Lua script, which Redis executes atomically. Concurrent requests from any number of instances cannot race, so there is no over admission.
* **One round trip per check.** The script is cached server side and called via EVALSHA, so each decision is a single pipelined round trip on the shared multiplexer, typically sub millisecond inside the same VPC or AZ. The store is fully async and never blocks thread pool threads.
* **No pod clock skew.** Time comes from the Redis server clock (via `TIME`), not from each pod's clock.
* **Bounded memory.** Each bucket is given a TTL of twice the window (the sliding window counter only needs the current and previous window), so idle keys expire on their own with no manual cleanup.

## Securing Redis in production

The rate limit keys hold client IPs and claim values (such as `sub`), so treat the Redis instance as sensitive infrastructure:

* **Keep it private.** Never expose Redis to the public internet. Put it in a private subnet and allow inbound `6379` only from the app's security group. On EKS, prefer a managed endpoint (such as ElastiCache or MemoryDB) reachable only from the cluster. Leave `protected-mode` on.
* **Require authentication with least privilege.** Use a Redis ACL user limited to the commands this store needs: `EVAL`, `EVALSHA`, `SCRIPT`, `HMGET`, `HSET`, `PEXPIRE`, `TIME`, `PING`.

  ```
  ACL SETUSER ratelimiter on >REPLACE_WITH_STRONG_SECRET ~rl:* +eval +evalsha +script +hmget +hset +pexpire +time +ping
  ```
* **Encrypt in transit.** Enable `ssl=true` with a real hostname, and turn on in transit and at rest encryption on managed Redis.
* **Never hardcode the connection string.** Load it from a secret (a Kubernetes `Secret` or AWS Secrets Manager), not from source:

  ```csharp
  builder.Services.AddRedisRateLimiter(
      builder.Configuration.GetConnectionString("Redis")!,
      keyPrefix: "rl:prod:");
  ```

  A secured connection string looks like:
  `my-redis.internal:6380,ssl=true,user=ratelimiter,password=<secret>,abortConnect=false`
* **Namespace the keyspace.** Use a distinct `keyPrefix` per app and environment so multiple services can safely share one cluster while their ACL scope (`~rl:*`) stays contained.

## Fails closed

If Redis is unreachable the request errors rather than silently skipping the limit, so an outage cannot be used to bypass rate limiting.
