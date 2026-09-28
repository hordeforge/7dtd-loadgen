using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

public sealed class PressureWaveLogTests
{
    [Fact]
    public void Admin_DropsAConsecutiveRepeatAndKeepsAChange()
    {
        // A refused console port answers the same line every wave. At the
        // default 20s cadence that is 180 identical lines an hour, and the
        // transcript still reads like a healthy run.
        var lines = new List<string>();
        var report = new PressureWaveLog("spawn", lines.Add);
        report.Admin("TELNET connect fail: Connection refused");
        report.Admin("TELNET connect fail: Connection refused");
        report.Admin("TELNET connected 127.0.0.1:8081");

        Assert.Equal(2, lines.Count);
        Assert.Contains("connect fail", lines[0]);
        Assert.Contains("connected", lines[1]);
    }

    [Fact]
    public void Unreachable_ReportsTheOutageOnceAndTheSummaryCountsEveryWave()
    {
        var lines = new List<string>();
        var report = new PressureWaveLog("spawn", lines.Add);
        for (int wave = 1; wave <= 5; wave++)
        {
            report.WaveStarted();
            report.Unreachable();
        }

        Assert.Single(lines);
        Assert.Contains("WARN", lines[0]);
        Assert.Contains("wave=1", lines[0]);
        string summary = report.Summary;
        Assert.Contains("waves=5", summary);
        Assert.Contains("unreachable=5", summary);
    }

    [Fact]
    public void WaveApplied_ReportsRecoveryOnceAndRestartsTheDedupe()
    {
        var lines = new List<string>();
        var report = new PressureWaveLog("horde", lines.Add);
        report.WaveStarted();
        report.Unreachable();
        report.WaveStarted();
        report.Admin("TELNET connected 127.0.0.1:8081");
        report.WaveApplied();
        report.WaveStarted();
        report.WaveApplied();

        // One outage, one recovery, and the reconnect line is not swallowed by
        // the dedupe the outage had already suppressed.
        Assert.Equal(3, lines.Count);
        Assert.Contains("WARN", lines[0]);
        Assert.Contains("connected", lines[1]);
        Assert.Contains("console back", lines[2]);
    }
}
