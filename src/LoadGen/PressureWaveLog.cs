namespace SevenDTD.LoadGen;

/// <summary>Reporting for one periodic telnet pressure source (the zombie
/// trickle, the wandering hordes). The loop runs for the whole run on a fixed
/// cadence, so its log lines are the only record of whether the pressure the
/// run header announced was actually applied.
///
/// Three things the raw admin log could not say. A wave that never opened its
/// console session used to print one line per wave and nothing else: at the
/// default 20s cadence a downed telnet port put 180 identical lines in an
/// hour's transcript, and the transcript still read like a healthy run. The
/// repeated line is dropped and the outage is reported once, on the
/// transition, with the wave number; the recovery is reported on the way back.
/// The wave counter and the unreachable count land in the loop's closing
/// summary, so a run that spent its whole life without pressure says so
/// instead of leaving it to be inferred from an absence.</summary>
public sealed class PressureWaveLog
{
    readonly string _label;
    readonly Action<string> _write;
    string? _lastAdminLine;
    int _waves;
    int _unreachable;
    bool _reportedDown;

    public PressureWaveLog(string label, Action<string> write)
    {
        _label = label;
        _write = write;
    }

    /// <summary>Line from the per-wave console session. Identical consecutive
    /// lines are dropped: a refused connection repeats verbatim every wave.</summary>
    public void Admin(string line)
    {
        if (line == _lastAdminLine) return;
        _lastAdminLine = line;
        _write(line);
    }

    /// <summary>Count a wave attempt, before the session is opened.</summary>
    public void WaveStarted() => _waves++;

    /// <summary>The wave's console session never opened, so the wave did not run.</summary>
    public void Unreachable()
    {
        _unreachable++;
        if (_reportedDown) return;
        _reportedDown = true;
        _write(RunReport.Event("WARN",
            $"TELNET {_label} wave={_waves} console unreachable; pressure off until it returns"));
    }

    /// <summary>The wave ran on a connected console.</summary>
    public void WaveApplied()
    {
        // Clear the dedupe as well: after an outage the reconnect line is the
        // first evidence the port is back, and it is otherwise the line the
        // outage suppressed.
        if (_reportedDown) _lastAdminLine = null;
        if (!_reportedDown) return;
        _reportedDown = false;
        _write(RunReport.Event("INFO",
            $"TELNET {_label} wave={_waves} console back; pressure resumed"));
    }

    /// <summary>A wave faulted after the session opened.</summary>
    public void Fault(Exception ex) =>
        _write(RunReport.Event("ERROR", $"TELNET {_label} wave={_waves} err {RunReport.FaultText(_label, ex)}"));

    /// <summary>Closing line: what the loop did over the run's lifetime.</summary>
    public string Summary =>
        RunReport.Event("INFO", $"TELNET {_label} waves={_waves} unreachable={_unreachable}");
}
