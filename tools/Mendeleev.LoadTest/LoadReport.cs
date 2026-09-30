namespace Mendeleev.LoadTest
{
    /// <summary>Percentiles against the targets of ТЗ 50: bot reply p95 ≤ 2 s (NFR-05), access ≤ 30 s (NFR-07).</summary>
    public static class LoadReport
    {
        private static readonly TimeSpan ReplyTarget = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AccessTarget = TimeSpan.FromSeconds(30);

        /// <summary>Prints the report and says whether the run met the targets.</summary>
        public static bool Print(IReadOnlyList<UserResult> results, TextWriter output)
        {
            int failed = results.Count(r => r.Failure is not null);
            output.WriteLine();
            output.WriteLine($"users: {results.Count}, got the link: {results.Count - failed}, failed: {failed}");
            output.WriteLine($"{"",-16}{"p50",8}{"p95",8}{"max",8}   target");

            bool passed = failed == 0;
            passed &= Line(output, "/start reply", results.Select(r => r.StartReply), ReplyTarget, "NFR-05");
            passed &= Line(output, "trial reply", results.Select(r => r.TrialReply), ReplyTarget, "NFR-05");
            passed &= Line(output, "link issued", results.Select(r => r.Access), AccessTarget, "NFR-07");

            foreach (IGrouping<string, UserResult> failure in results.Where(r => r.Failure is not null).GroupBy(r => r.Failure!))
            {
                output.WriteLine($"  {failure.Count()} × {failure.Key}");
            }

            output.WriteLine(passed ? "PASS" : "FAIL");
            return passed;
        }

        private static bool Line(TextWriter output, string name, IEnumerable<TimeSpan?> values, TimeSpan target, string requirement)
        {
            TimeSpan[] sorted = values.OfType<TimeSpan>().Order().ToArray();
            if (sorted.Length == 0)
            {
                output.WriteLine($"{name,-16}{"—",8}{"—",8}{"—",8}   p95 ≤ {target.TotalSeconds:0} s ({requirement})");
                return false;
            }

            TimeSpan p95 = Percentile(sorted, 0.95);
            output.WriteLine($"{name,-16}{Seconds(Percentile(sorted, 0.5)),8}{Seconds(p95),8}{Seconds(sorted[^1]),8}   p95 ≤ {target.TotalSeconds:0} s ({requirement})");
            return p95 <= target;
        }

        /// <summary>Nearest rank.</summary>
        private static TimeSpan Percentile(TimeSpan[] sorted, double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];

        private static string Seconds(TimeSpan value) => $"{value.TotalSeconds:0.00}s";
    }
}
