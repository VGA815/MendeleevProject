using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Mendeleev.LoadTest
{
    public sealed class LoadOptions
    {
        /// <summary>The service inside the compose network; updates go straight to its webhook.</summary>
        public string Target { get; init; } = "http://app:8080";

        /// <summary>The service's <c>Telegram__WebhookSecret</c>.</summary>
        public string Secret { get; init; } = string.Empty;

        public int Users { get; init; } = 300;

        /// <summary>New users arrive evenly over this time: 300 in 60 minutes is the first wave (ТЗ 50).</summary>
        public int Minutes { get; init; } = 60;

        /// <summary>Round trip to the Bot API from the management VPS, through the proxy if one is used.</summary>
        public int LatencyMs { get; init; } = 300;

        public string Listen { get; init; } = "http://0.0.0.0:8081";

        /// <summary>Telegram ID of the first user; a random range by default, so that every run gets fresh trials.</summary>
        public long FirstId { get; init; }
    }

    /// <summary>One user's way: <c>/start</c> → «Попробовать бесплатно» → the message with the link.</summary>
    public sealed record UserResult(TimeSpan? StartReply, TimeSpan? TrialReply, TimeSpan? Access, string? Failure);

    /// <summary>
    /// ТЗ 50, «Нагрузочные»: a wave of new users through the real webhook, database, outbox and staging panel.
    /// Only Telegram is replaced, by <see cref="BotApiStub"/>.
    /// </summary>
    public sealed class LoadRun(LoadOptions options, BotApiStub stub, HttpClient http)
    {
        private static readonly TimeSpan ThinkTime = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan AccessTimeout = TimeSpan.FromMinutes(2);

        private int _updateId = Random.Shared.Next(1_000_000_000, 2_000_000_000);

        public async Task<IReadOnlyList<UserResult>> RunAsync(long firstId, CancellationToken cancellationToken)
        {
            TimeSpan step = TimeSpan.FromMinutes(options.Minutes) / Math.Max(options.Users, 1);
            var clock = Stopwatch.StartNew();
            var users = new List<Task<UserResult>>(options.Users);

            for (int i = 0; i < options.Users; i++)
            {
                TimeSpan wait = step * i - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }

                users.Add(RunUserAsync(firstId + i, cancellationToken));
                if ((i + 1) % 50 == 0)
                {
                    Console.WriteLine($"{clock.Elapsed:hh\\:mm\\:ss} {i + 1} users started");
                }
            }

            return await Task.WhenAll(users);
        }

        private async Task<UserResult> RunUserAsync(long id, CancellationToken cancellationToken)
        {
            ChannelReader<BotCall> chat = stub.Chat(id);
            var user = new User { Id = id, IsBot = false, FirstName = "Load" };
            var privateChat = new Chat { Id = id, Type = ChatType.Private };
            TimeSpan? startReply = null, trialReply = null;

            try
            {
                DateTime sent = DateTime.UtcNow;
                await PostAsync(new Update
                {
                    Id = NextUpdateId(),
                    Message = new Message
                    {
                        Id = 1,
                        Date = sent,
                        Chat = privateChat,
                        From = user,
                        Text = "/start",
                        Entities = [new MessageEntity { Type = MessageEntityType.BotCommand, Offset = 0, Length = 6 }],
                    },
                }, cancellationToken);
                BotCall menu = await NextAsync(chat, c => c.IsVisible, ReplyTimeout, "reply to /start", cancellationToken);
                startReply = menu.At - sent;

                await Task.Delay(ThinkTime, cancellationToken);

                sent = DateTime.UtcNow;
                await PostAsync(new Update
                {
                    Id = NextUpdateId(),
                    CallbackQuery = new CallbackQuery
                    {
                        Id = $"load{id}",
                        From = user,
                        ChatInstance = "load",
                        Data = "trial",
                        Message = new Message { Id = menu.MessageId, Date = menu.At, Chat = privateChat },
                    },
                }, cancellationToken);
                BotCall screen = await NextAsync(chat, c => c.IsVisible, ReplyTimeout, "reply to the trial button", cancellationToken);
                trialReply = screen.At - sent;

                BotCall access = screen.GivesAccess
                    ? screen
                    : await NextAsync(chat, c => c.GivesAccess, AccessTimeout, "message with the link", cancellationToken);
                return new UserResult(startReply, trialReply, access.At - sent, null);
            }
            catch (Exception ex) when (ex is TimeoutException or HttpRequestException)
            {
                return new UserResult(startReply, trialReply, null, ex.Message);
            }
        }

        private int NextUpdateId() => Interlocked.Increment(ref _updateId);

        private async Task PostAsync(Update update, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/telegram")
            {
                Content = new StringContent(JsonSerializer.Serialize(update, JsonBotAPI.Options), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", options.Secret);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"webhook answered {(int)response.StatusCode}");
            }
        }

        private static async Task<BotCall> NextAsync(
            ChannelReader<BotCall> chat, Func<BotCall, bool> match, TimeSpan timeout, string waitingFor, CancellationToken cancellationToken)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    BotCall call = await chat.ReadAsync(timer.Token);
                    if (match(call))
                    {
                        return call;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"no {waitingFor} within {timeout.TotalSeconds:0} s");
            }
        }
    }
}
