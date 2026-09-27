using System.Threading.Channels;
using Telegram.Bot.Types;

namespace Mendeleev.Web.Bot.Infrastructure
{
    /// <summary>
    /// Incoming updates are answered with 200 at once and processed here (ТЗ 26: «Ответ на вебхук —
    /// сразу»). Updates of one chat always land in the same partition, so a user's clicks are handled in
    /// order; different users are served in parallel.
    /// </summary>
    public sealed class BotUpdateQueue
    {
        public const int Partitions = 4;

        private readonly Channel<Update>[] _channels = Enumerable.Range(0, Partitions)
            .Select(_ => Channel.CreateBounded<Update>(new BoundedChannelOptions(10_000)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            }))
            .ToArray();

        public void Enqueue(Update update)
        {
            long chatId = ChatIdOf(update) ?? 0;
            _channels[(int)((ulong)chatId % Partitions)].Writer.TryWrite(update);
        }

        public ChannelReader<Update> Reader(int partition) => _channels[partition].Reader;

        public static long? ChatIdOf(Update update) =>
            update.Message?.Chat.Id
            ?? update.CallbackQuery?.Message?.Chat.Id
            ?? update.CallbackQuery?.From.Id
            ?? update.MyChatMember?.Chat.Id;
    }
}
