using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using AppRateLimiter;
using Moq;
using Xunit;

namespace AppRateLimiter.Web.Tests
{
    public class RateLimitHttpModuleTests
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        // Captures what the module writes back to the client.
        private sealed class FakeResponse
        {
            public int StatusCode;
            public readonly NameValueCollection Headers = new NameValueCollection();
            public string? ContentType;
            public readonly StringBuilder Body = new StringBuilder();
        }

        private static (HttpContextBase Ctx, FakeResponse Resp) Context(string ip)
        {
            var resp = new FakeResponse();

            var request = new Mock<HttpRequestBase>();
            request.SetupGet(r => r.UserHostAddress).Returns(ip);
            request.SetupGet(r => r.Headers).Returns(new NameValueCollection());

            var response = new Mock<HttpResponseBase>();
            response.SetupSet(r => r.StatusCode = It.IsAny<int>()).Callback<int>(v => resp.StatusCode = v);
            response.SetupGet(r => r.Headers).Returns(resp.Headers);
            response.SetupSet(r => r.ContentType = It.IsAny<string>()).Callback<string>(v => resp.ContentType = v);
            response.Setup(r => r.Write(It.IsAny<string>())).Callback<string>(s => resp.Body.Append(s));

            var ctx = new Mock<HttpContextBase>();
            ctx.SetupGet(c => c.Request).Returns(request.Object);
            ctx.SetupGet(c => c.Response).Returns(response.Object);
            return (ctx.Object, resp);
        }

        // The module admits exactly the limit, then rejects with the same 429 contract as the
        // ASP.NET Core middleware (status, Retry-After header, JSON body).
        [Fact]
        public async Task Module_RejectsAfterLimit_WithRetryAfterAndJsonBody()
        {
            const string ip = "203.0.113.20";
            const int limit = 3;
            var store = new InMemoryRateLimitStore();
            var rules = new List<WebRateLimitRule> { WebRateLimitRules.ByIp(limit, Window) };

            for (int i = 0; i < limit; i++)
            {
                var (ctx, resp) = Context(ip);
                await RateLimitHttpModule.EvaluateForTest(store, rules, ctx);
                Assert.Equal(0, resp.StatusCode);   // not rejected: status untouched
            }

            var (blockedCtx, blocked) = Context(ip);
            await RateLimitHttpModule.EvaluateForTest(store, rules, blockedCtx);

            Assert.Equal(429, blocked.StatusCode);
            Assert.True(int.Parse(blocked.Headers["Retry-After"]) > 0);
            Assert.Equal("application/json", blocked.ContentType);
            Assert.Contains("\"error\":\"rate_limit_exceeded\"", blocked.Body.ToString());
            Assert.Contains("\"retryAfterSeconds\":", blocked.Body.ToString());
        }

        // Different clients keep independent counters (no shared bucket).
        [Fact]
        public async Task Module_DifferentClients_AreIndependent()
        {
            const int limit = 2;
            var store = new InMemoryRateLimitStore();
            var rules = new List<WebRateLimitRule> { WebRateLimitRules.ByIp(limit, Window) };

            for (int i = 0; i <= limit; i++)
            {
                var (ctx, _) = Context("203.0.113.21");
                await RateLimitHttpModule.EvaluateForTest(store, rules, ctx);
            }
            var (c1, r1) = Context("203.0.113.21");
            await RateLimitHttpModule.EvaluateForTest(store, rules, c1);
            Assert.Equal(429, r1.StatusCode);

            var (c2, r2) = Context("203.0.113.22"); // different client
            await RateLimitHttpModule.EvaluateForTest(store, rules, c2);
            Assert.Equal(0, r2.StatusCode);
        }
    }
}
