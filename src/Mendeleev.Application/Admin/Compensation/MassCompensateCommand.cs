using FluentValidation;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Admin.Compensation
{
    /// <summary>Whom a mass compensation extends (FR-SUB-16; решение 07.10).</summary>
    public enum CompensationSegment
    {
        /// <summary>Everyone with access right now.</summary>
        ActiveNow,

        /// <summary>
        /// Everyone who had access at some moment of the outage, including those whose term ran out during it: they
        /// get access back for the given days.
        /// </summary>
        ActiveDuringOutage,
    }

    /// <param name="IncludeTrial">Trials too; without it, only subscriptions on a paid tariff.</param>
    /// <param name="OutageFrom">For <see cref="CompensationSegment.ActiveDuringOutage"/>: the outage, UTC.</param>
    public sealed record MassCompensationSpec(
        int Days,
        CompensationSegment Segment,
        bool IncludeTrial,
        DateTime? OutageFrom = null,
        DateTime? OutageTo = null);

    /// <summary>How many subscriptions the spec takes right now — the number on the confirmation button.</summary>
    public sealed record CountMassCompensationQuery(long StaffId, MassCompensationSpec Spec) : IQuery<int>;

    /// <summary>
    /// An admin extends many subscriptions with one command after an outage (FR-SUB-16, FR-ADM-16). Every user is
    /// locked and checked again before the extension, which goes by FR-SUB-04 like a staff compensation; each gets a
    /// message with the reason, and the audit keeps a record per user plus one for the whole action. Users are taken
    /// in batches, so payments of the others do not wait for the whole run.
    /// </summary>
    public sealed record MassCompensateCommand(long StaffId, MassCompensationSpec Spec, string Reason) : ICommand<MassCompensationResult>;

    /// <param name="Skipped">Taken at the start, but changed meanwhile (blocked, archived, paid after the outage…).</param>
    public sealed record MassCompensationResult(int Extended, int Skipped);

    internal sealed class MassCompensateCommandValidator : AbstractValidator<MassCompensateCommand>
    {
        public MassCompensateCommandValidator()
        {
            RuleFor(x => x.Spec).Must(spec => MassCompensationTargets.Validate(spec) is null)
                .WithMessage(x => MassCompensationTargets.Validate(x.Spec)?.Description ?? string.Empty);
            RuleFor(x => x.Reason).Must(r => r?.Trim().Length is >= 3 and <= 300).WithMessage("Причина — от 3 до 300 символов: её получит каждый пользователь.");
        }
    }

    internal sealed class CountMassCompensationQueryHandler(IApplicationDbContext db, IStaffAuthorizer authorizer, IDateTimeProvider clock)
        : IQueryHandler<CountMassCompensationQuery, int>
    {
        public async Task<Result<int>> Handle(CountMassCompensationQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.MassCompensate, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }
            if (MassCompensationTargets.Validate(query.Spec) is Error invalid)
            {
                return invalid;
            }

            return await MassCompensationTargets.Query(db.Subscriptions, query.Spec, clock.UtcNow).CountAsync(cancellationToken);
        }
    }

    internal sealed class MassCompensateCommandHandler(IApplicationDbContext db, IStaffAuthorizer authorizer, IDateTimeProvider clock)
        : ICommandHandler<MassCompensateCommand, MassCompensationResult>
    {
        private const int BatchSize = 100;

        public async Task<Result<MassCompensationResult>> Handle(MassCompensateCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.MassCompensate, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            DateTime startedAt = clock.UtcNow;
            string reason = command.Reason.Trim();
            string campaign = $"mass:{startedAt.Ticks}";
            MassCompensationSpec spec = command.Spec;

            List<long> userIds = await MassCompensationTargets.Query(db.Subscriptions, spec, startedAt)
                .OrderBy(s => s.UserId)
                .Select(s => s.UserId)
                .ToListAsync(cancellationToken);

            int extended = 0;
            foreach (long[] batch in userIds.Chunk(BatchSize))
            {
                await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);
                DateTime now = clock.UtcNow;

                foreach (long userId in batch)
                {
                    User? user = await db.LockUserAsync(userId, cancellationToken);
                    if (user is null || user.IsBlocked)
                    {
                        continue;
                    }

                    Subscription? subscription = await db.Subscriptions
                        .Include(s => s.Tariff)
                        .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
                    if (subscription is null || !MassCompensationTargets.Matches(subscription, spec, startedAt))
                    {
                        continue;
                    }

                    DateTime before = subscription.ExpiresAt;
                    if (subscription.ExtendByStaff(spec.Days, campaign, now, reason).IsFailure)
                    {
                        continue;
                    }

                    db.AuditLog.Add(AuditLogEntry.ByStaff(
                        staff.Value.Id,
                        AuditActions.SubscriptionExtend,
                        userId,
                        Json.Serialize(new { days = spec.Days, reason, role = staff.Value.Role.ToString(), mass = campaign, before, after = subscription.ExpiresAt }),
                        now));
                    extended++;
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                db.DiscardChanges();
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staff.Value.Id,
                AuditActions.SubscriptionMassExtend,
                null,
                Json.Serialize(new
                {
                    campaign,
                    days = spec.Days,
                    segment = spec.Segment.ToString(),
                    includeTrial = spec.IncludeTrial,
                    outageFrom = spec.OutageFrom,
                    outageTo = spec.OutageTo,
                    reason,
                    extended,
                    skipped = userIds.Count - extended,
                }),
                clock.UtcNow));
            await db.SaveChangesAsync(cancellationToken);

            return new MassCompensationResult(extended, userIds.Count - extended);
        }
    }

    /// <summary>The segment as a query for the count and the snapshot, and as a check of each user under the lock.</summary>
    internal static class MassCompensationTargets
    {
        public static Error? Validate(MassCompensationSpec spec)
        {
            if (spec.Days is < 1 or > 365)
            {
                return Error.Validation("Compensation.Days", "Число дней — от 1 до 365.");
            }
            if (spec.Segment == CompensationSegment.ActiveDuringOutage
                && (spec.OutageFrom is not DateTime from || spec.OutageTo is not DateTime to || from >= to))
            {
                return Error.Validation("Compensation.Outage", "Укажите период сбоя: начало раньше конца.");
            }

            return null;
        }

        /// <summary>
        /// Active now: trial or active with the term ahead. During the outage: created before its end and either with
        /// access now or expired by time after its start — a trial that ran out of traffic and a refunded payment are
        /// not outage victims. Blocked (disabled) and archived subscriptions are never taken.
        /// </summary>
        public static IQueryable<Subscription> Query(IQueryable<Subscription> subscriptions, MassCompensationSpec spec, DateTime now)
        {
            bool includeTrial = spec.IncludeTrial;
            IQueryable<Subscription> query = subscriptions.Where(s => includeTrial || s.Tariff.Tier != TariffTier.Trial);

            if (spec.Segment == CompensationSegment.ActiveNow)
            {
                return query.Where(s => (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trial) && s.ExpiresAt > now);
            }

            DateTime from = spec.OutageFrom!.Value;
            DateTime to = spec.OutageTo!.Value;
            return query.Where(s => s.CreatedAt < to
                && ((s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trial) && s.ExpiresAt > now
                    || s.Status == SubscriptionStatus.Expired && s.ExpiredReason == ExpiredReason.Time && s.ExpiresAt > from));
        }

        public static bool Matches(Subscription s, MassCompensationSpec spec, DateTime now)
        {
            if (!spec.IncludeTrial && s.Tariff.IsTrial)
            {
                return false;
            }

            bool hasAccess = s.Status is SubscriptionStatus.Active or SubscriptionStatus.Trial && s.ExpiresAt > now;
            if (spec.Segment == CompensationSegment.ActiveNow)
            {
                return hasAccess;
            }

            return s.CreatedAt < spec.OutageTo
                && (hasAccess || s.Status == SubscriptionStatus.Expired && s.ExpiredReason == ExpiredReason.Time && s.ExpiresAt > spec.OutageFrom);
        }
    }
}
