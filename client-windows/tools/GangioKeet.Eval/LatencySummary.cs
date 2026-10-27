namespace GangioKeet.Eval;

public sealed record LatencySummary(
    int Count,
    double? P50Ms,
    double? P95Ms,
    double? MaxMs,
    int Over4000,
    double? FractionOver4000)
{
    public static LatencySummary From(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        if (sorted.Length == 0) return new(0, null, null, null, 0, null);
        static double Percentile(double[] data, double q) => data[Math.Max(0, (int)Math.Ceiling(q * data.Length) - 1)];
        int over = sorted.Count(x => x > 4000);
        return new(sorted.Length, Percentile(sorted, .5), Percentile(sorted, .95), sorted[^1], over, (double)over / sorted.Length);
    }

    public static string Bucket(double seconds) => seconds <= 10 ? "0-10"
        : seconds <= 30 ? "10-30"
        : seconds <= 60 ? "30-60"
        : "over-60";
}
