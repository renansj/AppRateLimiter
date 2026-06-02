using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AppRateLimiter
{
    /// <summary>
    /// Lock-per-key, sliding-window-counter limiter. Each key keeps only the current and
    /// previous window counts (O(1) memory), and the estimated rate is a weighted blend of
    /// the two windows, which smooths out the burst-at-the-boundary problem of fixed windows.
    /// A background sweep evicts idle keys so attackers rotating keys cannot exhaust memory.
    /// </summary>
    public sealed class InMemoryRateLimitStore : IRateLimitStore, IDisposable
    {
        private sealed class Counter
        {
            public long Window;
            public int Count;
            public int PrevCount;
            public DateTimeOffset Expiry;
        }

        private readonly ConcurrentDictionary<string, Counter> _counters =
            new ConcurrentDictionary<string, Counter>(StringComparer.Ordinal);
        private readonly Timer _cleanup;

        public InMemoryRateLimitStore()
        {
            _cleanup = new Timer(Sweep, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public ValueTask<RateLimitResult> HitAsync(string key, int permitLimit, TimeSpan window, DateTimeOffset now)
            => new ValueTask<RateLimitResult>(Hit(key, permitLimit, window, now));

        private RateLimitResult Hit(string key, int permitLimit, TimeSpan window, DateTimeOffset now)
        {
            long windowTicks = window.Ticks;
            long currentWindow = now.UtcTicks / windowTicks;
            var counter = _counters.GetOrAdd(key, _ => new Counter());

            lock (counter)
            {
                long delta = currentWindow - counter.Window;
                if (delta == 1) { counter.PrevCount = counter.Count; counter.Count = 0; }
                else if (delta > 1) { counter.PrevCount = 0; counter.Count = 0; }
                counter.Window = currentWindow;
                counter.Expiry = now + TimeSpan.FromTicks(windowTicks * 2);

                double elapsedFraction = (now.UtcTicks % windowTicks) / (double)windowTicks;
                double estimated = counter.PrevCount * (1d - elapsedFraction) + counter.Count;

                if (estimated >= permitLimit)
                {
                    long ticksToNextWindow = windowTicks - (now.UtcTicks % windowTicks);
                    return new RateLimitResult(false, 0, TimeSpan.FromTicks(ticksToNextWindow));
                }

                counter.Count++;
                int remaining = (int)Math.Max(0, permitLimit - estimated - 1);
                return new RateLimitResult(true, remaining, TimeSpan.Zero);
            }
        }

        private void Sweep(object? state)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (KeyValuePair<string, Counter> kv in _counters)
            {
                var counter = kv.Value;
                lock (counter)
                {
                    if (now <= counter.Expiry) continue;
                }
                ((ICollection<KeyValuePair<string, Counter>>)_counters).Remove(kv);
            }
        }

        public void Dispose() => _cleanup.Dispose();
    }
}
