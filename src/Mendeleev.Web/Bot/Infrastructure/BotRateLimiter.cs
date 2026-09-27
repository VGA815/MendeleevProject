using System.Collections.Concurrent;
using Mendeleev.SharedKernel;

namespace Mendeleev.Web.Bot.Infrastructure
{
    public enum RateDecision
    {
        Allow,

        /// <summary>First action over the limit: answer once «Слишком много запросов».</summary>
        Warn,

        /// <summary>Further actions in the same window are ignored silently.</summary>
        Drop,
    }

    /// <summary>Not more than 20 actions a minute per user (ТЗ 26, «Бизнес-правила»; FR-BOT-15).</summary>
    public sealed class BotRateLimiter(IDateTimeProvider clock)
    {
        public const int ActionsPerMinute = 20;

        private readonly ConcurrentDictionary<long, Window> _windows = new();

        public RateDecision Check(long telegramId)
        {
            DateTime now = clock.UtcNow;
            Window window = _windows.AddOrUpdate(
                telegramId,
                _ => new Window(now, 1, false),
                (_, current) => now - current.Start >= TimeSpan.FromMinutes(1)
                    ? new Window(now, 1, false)
                    : current with { Count = current.Count + 1 });

            if (window.Count <= ActionsPerMinute)
            {
                return RateDecision.Allow;
            }

            if (window.Warned)
            {
                return RateDecision.Drop;
            }

            _windows[telegramId] = window with { Warned = true };
            Prune(now);
            return RateDecision.Warn;
        }

        private void Prune(DateTime now)
        {
            if (_windows.Count < 10_000)
            {
                return;
            }

            foreach (KeyValuePair<long, Window> pair in _windows.Where(p => now - p.Value.Start > TimeSpan.FromMinutes(2)))
            {
                _windows.TryRemove(pair.Key, out _);
            }
        }

        private sealed record Window(DateTime Start, int Count, bool Warned);
    }
}
