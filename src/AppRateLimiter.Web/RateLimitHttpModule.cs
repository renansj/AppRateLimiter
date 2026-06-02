using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Web;

namespace AppRateLimiter.Web
{
    /// <summary>
    /// Async <see cref="IHttpModule"/> that brings AppRateLimiter to classic ASP.NET
    /// (WebForms / MVC 5 / Web API 2 on .NET Framework). IP rules run pre-auth on
    /// <c>BeginRequest</c>; claim rules run post-auth on <c>PostAuthenticateRequest</c>, reading
    /// the validated <see cref="HttpContext.User"/>. The store and rule sets are supplied once via
    /// <see cref="Configure"/> from <c>Global.asax</c> <c>Application_Start</c>, because classic
    /// modules cannot use dependency injection.
    /// <para>
    /// Register in web.config:
    /// <c>&lt;system.webServer&gt;&lt;modules&gt;&lt;add name="AppRateLimiter"
    /// type="AppRateLimiter.Web.RateLimitHttpModule, AppRateLimiter.Web"/&gt;&lt;/modules&gt;&lt;/system.webServer&gt;</c>
    /// </para>
    /// </summary>
    public sealed class RateLimitHttpModule : IHttpModule
    {
        private static IRateLimitStore? _store;
        private static IReadOnlyList<WebRateLimitRule> _ipRules = Array.Empty<WebRateLimitRule>();
        private static IReadOnlyList<WebRateLimitRule> _claimRules = Array.Empty<WebRateLimitRule>();

        /// <summary>
        /// Supplies the shared store and rule sets. Call once from Application_Start. Use the
        /// in-memory store for a single server, or the Redis store (AppRateLimiter.Redis) so an
        /// IIS web farm behind a load balancer shares one global counter.
        /// </summary>
        public static void Configure(
            IRateLimitStore store,
            IEnumerable<WebRateLimitRule>? ipRules = null,
            IEnumerable<WebRateLimitRule>? claimRules = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _ipRules = ipRules == null ? Array.Empty<WebRateLimitRule>() : new List<WebRateLimitRule>(ipRules);
            _claimRules = claimRules == null ? Array.Empty<WebRateLimitRule>() : new List<WebRateLimitRule>(claimRules);
        }

        public void Init(HttpApplication context)
        {
            // EventHandlerTaskAsyncHelper bridges the Task-based handlers into the classic
            // Begin/End async event model, so we await HitAsync without blocking a thread.
            var begin = new EventHandlerTaskAsyncHelper(OnBeginRequest);
            context.AddOnBeginRequestAsync(begin.BeginEventHandler, begin.EndEventHandler);

            var postAuth = new EventHandlerTaskAsyncHelper(OnPostAuthenticateRequest);
            context.AddOnPostAuthenticateRequestAsync(postAuth.BeginEventHandler, postAuth.EndEventHandler);
        }

        private static Task OnBeginRequest(object sender, EventArgs e)
            => Evaluate(new HttpContextWrapper(((HttpApplication)sender).Context), _ipRules);

        private static Task OnPostAuthenticateRequest(object sender, EventArgs e)
            => Evaluate(new HttpContextWrapper(((HttpApplication)sender).Context), _claimRules);

        // Test hook: runs the exact evaluate-and-reject path against a supplied context, using a
        // supplied store and rules, without needing an IIS-hosted HttpApplication. Internal so it
        // does not widen the public API. Exposed to the test project via InternalsVisibleTo.
        internal static Task EvaluateForTest(
            IRateLimitStore store, IReadOnlyList<WebRateLimitRule> rules, HttpContextBase context)
            => Evaluate(context, rules, store);

        // Mirrors RateLimitMiddleware.InvokeAsync: first exceeded rule short-circuits with 429.
        private static Task Evaluate(HttpContextBase context, IReadOnlyList<WebRateLimitRule> rules)
            => Evaluate(context, rules, _store);

        private static async Task Evaluate(HttpContextBase context, IReadOnlyList<WebRateLimitRule> rules, IRateLimitStore? store)
        {
            if (store == null || rules.Count == 0) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < rules.Count; i++)
            {
                WebRateLimitRule rule = rules[i];
                string? key = rule.KeySelector(context);
                if (key == null) continue;

                // Same name-based namespacing and separator as the ASP.NET Core middleware,
                // so both adapters share buckets when pointed at the same store.
                RateLimitResult result = await store
                    .HitAsync(rule.Name + RateLimitMiddleware.KeySeparator + key, rule.PermitLimit, rule.Window, now)
                    .ConfigureAwait(false);
                if (!result.Allowed)
                {
                    Reject(context, result);
                    return;
                }
            }
        }

        private static void Reject(HttpContextBase context, RateLimitResult result)
        {
            int seconds = (int)Math.Ceiling(result.RetryAfter.TotalSeconds);
            HttpResponseBase response = context.Response;
            response.StatusCode = 429;
            response.Headers["Retry-After"] = seconds.ToString(CultureInfo.InvariantCulture);
            response.ContentType = "application/json";
            response.Write("{\"error\":\"rate_limit_exceeded\",\"retryAfterSeconds\":" + seconds + "}");
            // Stop the pipeline so no further handlers run for this rejected request.
            context.ApplicationInstance?.CompleteRequest();
        }

        public void Dispose() { }
    }
}
