using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Mendeleev.LoadTest
{
    /// <summary>
    /// What the service sent to a chat and when it reached the Bot API. The message with the subscription link is
    /// recognised by its «copy link» button (<c>copy_text</c>), which only that notification carries here.
    /// </summary>
    public sealed record BotCall(string Method, DateTime At, int MessageId, bool GivesAccess)
    {
        private static readonly HashSet<string> VisibleMethods = new(StringComparer.OrdinalIgnoreCase) { "sendMessage", "editMessageText", "sendPhoto" };

        /// <summary>A change the user sees: a new or an edited message, not the end of a button's spinner.</summary>
        public bool IsVisible => VisibleMethods.Contains(Method);
    }

    /// <summary>
    /// Stands in for api.telegram.org while the service's <c>Telegram:ApiBaseUrl</c> points here: records every call
    /// per chat and answers after <paramref name="latency"/>, the round trip to Telegram from the Russian DC.
    /// </summary>
    public sealed class BotApiStub(TimeSpan latency)
    {
        private static readonly HashSet<string> MessageMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            "sendMessage", "sendPhoto", "sendDocument", "editMessageText", "editMessageCaption", "editMessageReplyMarkup",
        };

        private readonly ConcurrentDictionary<long, Channel<BotCall>> _chats = new();
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _messageId;

        /// <summary>Completes on the first call of any kind: the service is talking to the stub, not to Telegram.</summary>
        public Task Connected => _firstCall.Task;

        public ChannelReader<BotCall> Chat(long chatId) => ChannelOf(chatId).Reader;

        public void Map(IEndpointRouteBuilder app) => app.MapPost("/bot{token}/{method}", HandleAsync);

        private async Task<IResult> HandleAsync(string method, HttpRequest request, CancellationToken cancellationToken)
        {
            DateTime at = DateTime.UtcNow;
            _firstCall.TrySetResult();
            (long? chatId, string? markup) = await ReadAsync(request, cancellationToken);

            int messageId = MessageMethods.Contains(method) ? Interlocked.Increment(ref _messageId) : 0;
            if (chatId is long id)
            {
                bool givesAccess = markup?.Contains("copy_text", StringComparison.Ordinal) == true;
                ChannelOf(id).Writer.TryWrite(new BotCall(method, at, messageId, givesAccess));
            }

            await Task.Delay(latency, cancellationToken);

            object result = method.Equals("getMe", StringComparison.OrdinalIgnoreCase)
                ? new { id = 1, is_bot = true, first_name = "Stub", username = "stub_bot" }
                : messageId > 0 && chatId is long chat
                    ? new { message_id = messageId, date = new DateTimeOffset(at).ToUnixTimeSeconds(), chat = new { id = chat, type = "private" } }
                    : true;
            return Results.Json(new { ok = true, result });
        }

        private Channel<BotCall> ChannelOf(long chatId) => _chats.GetOrAdd(chatId, _ => Channel.CreateUnbounded<BotCall>());

        /// <summary>JSON for most methods, multipart for uploads (the QR code); no body for getMe.</summary>
        private static async Task<(long? ChatId, string? Markup)> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            if (request.HasFormContentType)
            {
                IFormCollection form = await request.ReadFormAsync(cancellationToken);
                return (long.TryParse(form["chat_id"], out long formChat) ? formChat : null, form["reply_markup"]);
            }

            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                return (null, null);
            }

            long? chatId = body?["chat_id"] switch
            {
                JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.GetValue<long>(),
                JsonValue value when long.TryParse(value.ToString(), out long parsed) => parsed,
                _ => null,
            };
            return (chatId, body?["reply_markup"]?.ToJsonString());
        }
    }
}
