namespace Mendeleev.Domain.Common
{
    /// <summary>
    /// Everything user-facing and every schedule in the ТЗ is in Moscow time (quiet hours, 04:00
    /// reconciliation, 10:00 summary, "today" in statistics). Storage stays in UTC.
    /// </summary>
    public static class MoscowTime
    {
        public static readonly TimeZoneInfo Zone = Resolve();

        public static DateTime FromUtc(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

        public static DateTime ToUtc(DateTime moscowLocal) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(moscowLocal, DateTimeKind.Unspecified), Zone);

        public static DateOnly Today(DateTime utcNow) => DateOnly.FromDateTime(FromUtc(utcNow));

        public static DateTime StartOfDayUtc(DateOnly day) => ToUtc(day.ToDateTime(TimeOnly.MinValue));

        private static TimeZoneInfo Resolve()
        {
            foreach (string id in new[] { "Europe/Moscow", "Russian Standard Time" })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }

            // Moscow has been UTC+3 without DST since 2014.
            return TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK");
        }
    }
}
