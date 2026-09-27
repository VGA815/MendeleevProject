using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Domain.Staff;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Mendeleev.Web.Bot.Infrastructure
{
    /// <summary>
    /// Staff commands are visible only in staff chats (FR-BOT-13): <c>setMyCommands</c> with a chat scope
    /// per staff member; everybody else sees only the user commands. Visibility is a convenience — every
    /// staff command is also checked on the server.
    /// </summary>
    internal sealed class StaffCommandsPublisher(
        ITelegramBotClient bot,
        IServiceScopeFactory scopeFactory,
        ILogger<StaffCommandsPublisher> logger)
    {
        private static readonly BotCommand[] UserCommands =
        [
            new() { Command = "start", Description = "Начать" },
            new() { Command = "menu", Description = "Главное меню" },
            new() { Command = "sub", Description = "Моя подписка" },
            new() { Command = "buy", Description = "Купить или продлить" },
            new() { Command = "devices", Description = "Устройства" },
            new() { Command = "help", Description = "Помощь" },
            new() { Command = "key", Description = "Вход на сайт" },
        ];

        public async Task PublishAllAsync(CancellationToken cancellationToken)
        {
            await bot.SetMyCommands(UserCommands, cancellationToken: cancellationToken);

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IApplicationDbContext db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var staff = await db.Staff.AsNoTracking().Select(s => new { s.TelegramId, s.Role, s.IsActive }).ToListAsync(cancellationToken);

            foreach (var member in staff)
            {
                await PublishAsync(member.TelegramId, member.IsActive ? member.Role : null, cancellationToken);
            }
        }

        public async Task PublishAsync(long telegramId, StaffRole? role, CancellationToken cancellationToken)
        {
            try
            {
                if (role is null)
                {
                    await bot.DeleteMyCommands(BotCommandScope.Chat(telegramId), cancellationToken: cancellationToken);
                    return;
                }

                await bot.SetMyCommands(CommandsFor(role.Value), BotCommandScope.Chat(telegramId), cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A staff member who never opened the bot cannot get chat-scoped commands yet.
                logger.LogWarning(ex, "Could not publish commands for a staff chat");
            }
        }

        private static IEnumerable<BotCommand> CommandsFor(StaffRole role)
        {
            var commands = new List<BotCommand>(UserCommands)
            {
                new() { Command = "find", Description = "Найти пользователя" },
            };

            if (StaffPolicy.Allows(role, StaffPermission.Broadcast))
            {
                commands.Add(new() { Command = "broadcast", Description = "Рассылка" });
            }
            if (StaffPolicy.Allows(role, StaffPermission.ViewStats))
            {
                commands.Add(new() { Command = "stats", Description = "Статистика" });
            }
            if (StaffPolicy.Allows(role, StaffPermission.ManageStaff))
            {
                commands.Add(new() { Command = "staff", Description = "Сотрудники" });
            }
            if (StaffPolicy.Allows(role, StaffPermission.ViewAudit))
            {
                commands.Add(new() { Command = "audit", Description = "Журнал аудита" });
            }
            if (StaffPolicy.Allows(role, StaffPermission.ViewTariffs))
            {
                commands.Add(new() { Command = "tariffs", Description = "Тарифы" });
            }

            commands.Add(new() { Command = "cancel", Description = "Отменить ввод" });
            return commands;
        }
    }
}
