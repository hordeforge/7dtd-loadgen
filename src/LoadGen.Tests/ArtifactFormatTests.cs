using System.Globalization;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// The report lines are read by other tools (bench_stock.sh greps
/// BENCH_SUMMARY, test_realearth_scenarios.py splits JOIN_SUMMARY on '='), so
/// their number formatting is a wire format, not a display choice. These run
/// under a comma-decimal culture to pin that: the same run must produce the
/// same bytes on every operator machine.
/// </summary>
public sealed class ArtifactFormatTests
{
    /// <summary>Text up to the first space or percent sign: the digits and the
    /// separator between them, which is what a machine-to-machine path reads.</summary>
    static string Digits(string formatted) =>
        formatted.Split(' ', '%')[0].Trim();

    static void UnderCulture(string name, Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            body();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Percent_UsesADotRegardlessOfCulture() =>
        UnderCulture("de-DE", () =>
        {
            // The space before % is a plain space under the invariant culture
            // and U+00A0 under de-DE, before and after this change; the decimal
            // separator is the part these lines must agree on.
            Assert.Equal("98.55", Digits(ArtifactFormat.Percent(0.9855)));
            Assert.Equal("100.00", Digits(ArtifactFormat.Percent(1.0)));
            Assert.Equal("0.00", Digits(ArtifactFormat.Percent(0.0)));
        });

    [Fact]
    public void Percent0_UsesADotRegardlessOfCulture() =>
        UnderCulture("fr-FR", () => Assert.Equal("50", Digits(ArtifactFormat.Percent0(0.5))));

    [Fact]
    public void Fixed_UsesADotRegardlessOfCulture() =>
        UnderCulture("de-DE", () =>
        {
            Assert.Equal("12.50", ArtifactFormat.Fixed(12.5, 2));
            Assert.Equal("0.125", ArtifactFormat.Fixed(0.125, 3));
            Assert.Equal("7.00", ArtifactFormat.Fixed(7, 2));
        });

    [Fact]
    public void LoadSummaryLine_ParsesUnderACommaDecimalCulture()
    {
        var summary = new LoadSummary { Total = 4, Pass = 3, Fail = 1 };
        UnderCulture("de-DE", () =>
        {
            string line = summary.ToReportLines()[0];
            // "passRate=75,00 %" reads back as a value and a stray field.
            Assert.StartsWith("LOAD_SUMMARY total=4 pass=3 fail=1 passRate=75.00", line);
            Assert.DoesNotContain(',', line);
        });
    }
}
