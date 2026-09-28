using System.Globalization;

namespace SevenDTD.LoadGen;

/// <summary>Culture-independent number formatting for run artifacts and
/// console lines other tools read.
/// <para>
/// The report lines carry pass rates, per-second rates and millisecond values
/// that scripts/ and tools/ parse (bench_stock.sh greps BENCH_SUMMARY,
/// tests/test_realearth_scenarios.py splits JOIN_SUMMARY on '='), so the
/// separator is part of the contract, not a presentation choice. Interpolated
/// <c>{x:P2}</c> and <c>{x:0.00}</c> use CurrentCulture: under a
/// comma-decimal locale the same run writes "98,55 %" instead of "98.55 %",
/// and a decimal-comma value no longer parses as one number. The parse side
/// (Program.TryParseMinPassRate and friends) already reads the invariant
/// spelling, so the two ends disagreed.
/// </para></summary>
public static class ArtifactFormat
{
    /// <summary>Percentage with two decimals, e.g. "98.55 %". Matches the
    /// <c>P2</c> spec the report lines already used, minus the locale.</summary>
    public static string Percent(double fraction) =>
        fraction.ToString("P2", CultureInfo.InvariantCulture);

    /// <summary>Whole-number percentage, e.g. "50 %".</summary>
    public static string Percent0(double fraction) =>
        fraction.ToString("P0", CultureInfo.InvariantCulture);

    /// <summary>Fixed-point with the given number of decimals, e.g. 2 -> "12.50".</summary>
    public static string Fixed(double value, int decimals) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                       CultureInfo.InvariantCulture);
}
