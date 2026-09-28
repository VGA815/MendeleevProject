using System.Diagnostics;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Admin.Broadcasts;
using Mendeleev.Domain.Broadcasts;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Users;
using Mendeleev.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.IntegrationTests
{
    /// <summary>ТЗ 50, сценарий 13 (FR-ADM-08, FR-BOT-14) и темп рассылки (NFR-18).</summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class BroadcastTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private const long AdminChat = 900;
        private TestApp _app = null!;

        public async Task InitializeAsync() => _app = await TestApp.CreateAsync(postgres);

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task Broadcast_KeepsTo25PerSecond_MarksWhoBlockedTheBot_AndReportsExactly()
        {
            long staffId = await _app.AddStaffAsync(AdminChat, StaffRole.Admin);
            const int recipients = 60;
            for (long telegramId = 8001; telegramId < 8001 + recipients; telegramId++)
            {
                await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(telegramId));
            }
            _app.Messenger.BlockedChats[8007] = true;

            BroadcastDraft draft = (await _app.SendAsync<CreateBroadcastCommand, BroadcastDraft>(
                new CreateBroadcastCommand(staffId, AdminChat, BroadcastSegment.All, "Плановые работы в 03:00 МСК.", WithUpdateButton: false))).Value;
            draft.Recipients.ShouldBe(recipients);
            (await _app.SendAsync<StartBroadcastCommand, int>(new StartBroadcastCommand(staffId, draft.BroadcastId))).Value.ShouldBe(recipients);

            await using (AsyncServiceScope scope = _app.Provider.CreateAsyncScope())
            {
                (await scope.ServiceProvider.GetRequiredService<IBroadcastDeliveryService>().RunNextAsync(CancellationToken.None)).ShouldBeTrue();
            }

            // NFR-18: never more than 25 messages within any second.
            long[] sentAt = [.. _app.Messenger.BroadcastSentAt];
            sentAt.Length.ShouldBe(recipients);
            for (int i = 25; i < sentAt.Length; i++)
            {
                Stopwatch.GetElapsedTime(sentAt[i - 25], sentAt[i]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.99), $"messages {i - 25}…{i}");
            }

            // The user who blocked the bot is marked and left out of the next broadcasts (FR-BOT-14).
            User blocked = await _app.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.TelegramId == 8007));
            blocked.BotBlocked.ShouldBeTrue();

            // The report matches what happened (FR-ADM-08).
            Broadcast done = await _app.WithDbAsync(db => db.Broadcasts.AsNoTracking().SingleAsync(b => b.Id == draft.BroadcastId));
            done.Status.ShouldBe(BroadcastStatus.Done);
            done.Sent.ShouldBe(recipients - 1);
            done.BotBlocked.ShouldBe(1);
            string report = _app.Messenger.Texts.Single(t => t.ChatId == AdminChat).Html;
            report.ShouldContain($"Отправлено: {recipients - 1}");
            report.ShouldContain("заблокировали бота: 1");

            BroadcastDraft next = (await _app.SendAsync<CreateBroadcastCommand, BroadcastDraft>(
                new CreateBroadcastCommand(staffId, AdminChat, BroadcastSegment.All, "Ещё одно сообщение.", WithUpdateButton: false))).Value;
            next.Recipients.ShouldBe(recipients - 1);
        }
    }
}
