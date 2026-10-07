using System.Globalization;
using System.Text;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Common;
using Mendeleev.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Anomalies
{
    /// <summary>
    /// The daily anomaly report (FR-ADM-17; ТЗ 28, «Аномалии и абуз»): frequent device resets, a day's traffic far
    /// above the median, Torrent Blocker events and series of unpaid invoices. The ТЗ wants them handled by hand:
    /// the report only points, an admin decides.
    /// </summary>
    /// <param name="TrafficDay">The Moscow day the traffic is about — yesterday, collected at 00:10.</param>
    public sealed record AnomalyReport(
        DateOnly TrafficDay,
        long? MedianBytes,
        IReadOnlyList<UserCount> Resets,
        IReadOnlyList<TrafficSpike> Traffic,
        IReadOnlyList<UserCount> Torrent,
        IReadOnlyList<UserCount> UnpaidInvoices)
    {
        public bool IsEmpty => Resets.Count == 0 && Traffic.Count == 0 && Torrent.Count == 0 && UnpaidInvoices.Count == 0;
    }

    public sealed record UserCount(long UserId, int Count);

    public sealed record TrafficSpike(long UserId, DateOnly Day, long Bytes, long MedianBytes);

    /// <summary>The anomaly flags of one user for the card (ТЗ 28, «Карточка пользователя»).</summary>
    public sealed record UserAnomalies(bool FrequentResets, int TorrentEvents30Days, int UnpaidInvoices24Hours, TrafficSpike? Spike)
    {
        public bool Any => FrequentResets || TorrentEvents30Days > 0 || UnpaidInvoices24Hours > 0 || Spike is not null;
    }

    internal static class AnomalyDetector
    {
        /// <summary>For the 10:00 summary: the last 24 hours, and yesterday's traffic.</summary>
        public static async Task<AnomalyReport> DailyAsync(IApplicationDbContext db, AnomalyOptions options, DateTime now, CancellationToken cancellationToken)
        {
            DateTime dayAgo = now.AddHours(-24);
            DateTime monthAgo = now.AddDays(-30);

            // A user stays above the threshold for a month: shown on the day of a new reset only.
            List<UserCount> resets = (await db.DeviceResets
                    .Where(r => r.CreatedAt > monthAgo)
                    .GroupBy(r => r.UserId)
                    .Select(g => new { UserId = g.Key, Count = g.Count(), Last = g.Max(r => r.CreatedAt) })
                    .Where(x => x.Count >= options.ResetsIn30Days && x.Last > dayAgo)
                    .ToListAsync(cancellationToken))
                .Select(x => new UserCount(x.UserId, x.Count))
                .OrderByDescending(x => x.Count)
                .ToList();

            DateOnly day = MoscowTime.Today(now).AddDays(-1);
            var traffic = await db.TrafficDaily
                .Where(t => t.Day == day && t.Bytes > 0)
                .Join(db.Subscriptions, t => t.SubscriptionId, s => s.Id, (t, s) => new { s.UserId, t.Bytes })
                .ToListAsync(cancellationToken);
            long? median = Median(traffic.Select(t => t.Bytes).ToList());
            List<TrafficSpike> spikes = median is long m
                ? traffic
                    .Where(t => t.Bytes >= Threshold(m, options))
                    .OrderByDescending(t => t.Bytes)
                    .Select(t => new TrafficSpike(t.UserId, day, t.Bytes, m))
                    .ToList()
                : [];

            List<UserCount> torrent = (await db.AuditLog
                    .Where(a => a.Action == AuditActions.TorrentBlockerReport && a.CreatedAt > dayAgo && a.TargetUserId != null)
                    .GroupBy(a => a.TargetUserId!.Value)
                    .Select(g => new { UserId = g.Key, Count = g.Count() })
                    .ToListAsync(cancellationToken))
                .Select(x => new UserCount(x.UserId, x.Count))
                .OrderByDescending(x => x.Count)
                .ToList();

            List<UserCount> unpaid = (await db.Payments
                    .Where(p => (p.Status == PaymentStatus.Canceled || p.Status == PaymentStatus.Failed) && p.UpdatedAt > dayAgo)
                    .GroupBy(p => p.UserId)
                    .Select(g => new { UserId = g.Key, Count = g.Count() })
                    .Where(x => x.Count >= options.FailedPaymentsPerDay)
                    .ToListAsync(cancellationToken))
                .Select(x => new UserCount(x.UserId, x.Count))
                .OrderByDescending(x => x.Count)
                .ToList();

            return new AnomalyReport(day, median, resets, spikes, torrent, unpaid);
        }

        /// <summary>The same signs for one user: the card shows them as flags.</summary>
        public static async Task<UserAnomalies> ForUserAsync(
            IApplicationDbContext db,
            AnomalyOptions options,
            long userId,
            long? subscriptionId,
            int resetsIn30Days,
            DateTime now,
            CancellationToken cancellationToken)
        {
            DateTime monthAgo = now.AddDays(-30);
            DateTime dayAgo = now.AddHours(-24);

            int torrent = await db.AuditLog.CountAsync(
                a => a.Action == AuditActions.TorrentBlockerReport && a.TargetUserId == userId && a.CreatedAt > monthAgo,
                cancellationToken);
            int unpaid = await db.Payments.CountAsync(
                p => p.UserId == userId && (p.Status == PaymentStatus.Canceled || p.Status == PaymentStatus.Failed) && p.UpdatedAt > dayAgo,
                cancellationToken);

            TrafficSpike? spike = null;
            if (subscriptionId is long id)
            {
                DateOnly weekAgo = MoscowTime.Today(now).AddDays(-7);
                var busiest = await db.TrafficDaily
                    .Where(t => t.SubscriptionId == id && t.Day >= weekAgo && t.Bytes > 0)
                    .OrderByDescending(t => t.Bytes)
                    .Select(t => new { t.Day, t.Bytes })
                    .FirstOrDefaultAsync(cancellationToken);

                if (busiest is not null)
                {
                    List<long> thatDay = await db.TrafficDaily
                        .Where(t => t.Day == busiest.Day && t.Bytes > 0)
                        .Select(t => t.Bytes)
                        .ToListAsync(cancellationToken);
                    if (Median(thatDay) is long median && busiest.Bytes >= Threshold(median, options))
                    {
                        spike = new TrafficSpike(userId, busiest.Day, busiest.Bytes, median);
                    }
                }
            }

            return new UserAnomalies(resetsIn30Days >= options.ResetsIn30Days, torrent, unpaid >= options.FailedPaymentsPerDay ? unpaid : 0, spike);
        }

        internal static long? Median(IReadOnlyList<long> values)
        {
            if (values.Count == 0)
            {
                return null;
            }

            List<long> sorted = [.. values.Order()];
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        internal static long Threshold(long median, AnomalyOptions options) =>
            Math.Max(options.TrafficFloorBytes, median * options.TrafficTimesMedian);
    }

    /// <summary>The report as a section of the daily summary to admins and tech admins (Telegram HTML).</summary>
    public static class AnomalyFormatter
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public static string Format(AnomalyReport report, int limit)
        {
            if (report.IsEmpty)
            {
                return "<b>Аномалии</b>: нет.";
            }

            var text = new StringBuilder("<b>Аномалии за сутки</b> — разобрать вручную, при необходимости предупредить или заблокировать (ТЗ 28)\n");
            Section(text, "Частые сбросы устройств (за 30 дней)", report.Resets, limit, x => $"u{x.UserId} — {x.Count}");
            Section(text, $"Трафик за {report.TrafficDay:dd.MM} выше медианы ({Gb(report.MedianBytes ?? 0)})", report.Traffic, limit,
                x => $"u{x.UserId} — {Gb(x.Bytes)}");
            Section(text, "События Torrent Blocker", report.Torrent, limit, x => $"u{x.UserId} — {x.Count}");
            Section(text, "Неоплаченные и несозданные счета (за сутки)", report.UnpaidInvoices, limit, x => $"u{x.UserId} — {x.Count}");
            return text.ToString().TrimEnd();
        }

        public static string Gb(long bytes) => (bytes / (1024d * 1024 * 1024)).ToString("0.0", Ru) + " ГБ";

        private static void Section<T>(StringBuilder text, string title, IReadOnlyList<T> items, int limit, Func<T, string> line)
        {
            if (items.Count == 0)
            {
                return;
            }

            text.Append(Ru, $"{title}: ");
            text.Append(string.Join(", ", items.Take(limit).Select(line)));
            if (items.Count > limit)
            {
                text.Append(Ru, $" и ещё {items.Count - limit}");
            }
            text.Append('\n');
        }
    }
}
