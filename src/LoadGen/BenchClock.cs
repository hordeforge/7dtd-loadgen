using System.Diagnostics;

namespace SevenDTD.LoadGen;

/// <summary>
/// Cohort-level measurement clock for benchmark runs. The cohort joins during a
/// warm-up phase (ramp + settle) and the measurement window is the interval
/// [warmupMs, warmupMs + windowMs) after the cohort start. Action/death/respawn
/// events are counted only inside the window; the active-client curve is sampled
/// once per second so the report shows the load shape. Thread-safe: each bot
/// pokes it from its own task.
/// </summary>
public sealed class BenchClock
{
    private readonly long _startTicks = Stopwatch.GetTimestamp();
    private readonly Func<long> _elapsedMs;
    private readonly int _warmupMs;
    private readonly int _windowMs;
    private int _actionsInWindow;
    private int _deathsInWindow;
    private int _respawnsInWindow;
    private readonly List<(int Ms, int Active)> _activeCurve = new();
    private int _activeMin = int.MaxValue;
    private int _activeMax;

    public BenchClock(int warmupMs, int windowMs)
        : this(warmupMs, windowMs, null)
    {
    }

    /// <summary>Test seam: inject a controllable elapsed-ms provider.</summary>
    internal BenchClock(int warmupMs, int windowMs, Func<long>? elapsedMs)
    {
        _warmupMs = Math.Max(0, warmupMs);
        _windowMs = Math.Max(0, windowMs);
        _elapsedMs = elapsedMs ?? (() =>
            (long)((Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency));
    }

    /// <summary>Window disabled - the clock only samples the active curve.</summary>
    public bool WindowEnabled => _windowMs > 0;

    public int WarmupMs => _warmupMs;

    public int WindowMs => _windowMs;

    /// <summary>Milliseconds since the cohort start.</summary>
    public long ElapsedMs => _elapsedMs();

    /// <summary>True when the current time is inside the measurement window.</summary>
    public bool InWindow => WindowEnabled && ElapsedMs >= _warmupMs && ElapsedMs < _warmupMs + _windowMs;

    /// <summary>Window start and end in ms since cohort start (0 when disabled).</summary>
    public (long StartMs, long EndMs) WindowBounds =>
        WindowEnabled ? (_warmupMs, (long)_warmupMs + _windowMs) : (0, 0);

    public void OnAction()
    {
        if (InWindow) Interlocked.Increment(ref _actionsInWindow);
    }

    public void OnDeath()
    {
        if (InWindow) Interlocked.Increment(ref _deathsInWindow);
    }

    public void OnRespawn()
    {
        if (InWindow) Interlocked.Increment(ref _respawnsInWindow);
    }

    /// <summary>
    /// Record one active-cohort sample. Called by the orchestrator sampler about
    /// once per second; the first sample seeds the curve, so the very first tick
    /// also captures t=0.
    /// </summary>
    public void SampleActive(int active)
    {
        long now = ElapsedMs;
        lock (_activeCurve)
        {
            // Do not duplicate a sample on the same second.
            if (_activeCurve.Count > 0 && now - _activeCurve[_activeCurve.Count - 1].Ms < 500)
                _activeCurve[_activeCurve.Count - 1] = ((int)now, active);
            else
                _activeCurve.Add(((int)now, active));
            if (active < _activeMin) _activeMin = active;
            if (active > _activeMax) _activeMax = active;
        }
    }

    public (int Actions, int Deaths, int Respawns) WindowCounts =>
        (Volatile.Read(ref _actionsInWindow), Volatile.Read(ref _deathsInWindow),
         Volatile.Read(ref _respawnsInWindow));

    public int ActiveMin => _activeMin == int.MaxValue ? 0 : _activeMin;

    public int ActiveMax => _activeMax;

    /// <summary>Active clients at (or just before) the window start; 0 when no sample yet.</summary>
    public int ActiveAtWindowStart
    {
        get
        {
            lock (_activeCurve)
            {
                int last = 0;
                foreach (var (ms, active) in _activeCurve)
                {
                    if (ms > _warmupMs) break;
                    last = active;
                }
                return last;
            }
        }
    }

    /// <summary>Active clients at the last sample; 0 when no sample yet.</summary>
    public int ActiveAtWindowEnd
    {
        get
        {
            lock (_activeCurve)
                return _activeCurve.Count > 0 ? _activeCurve[_activeCurve.Count - 1].Active : 0;
        }
    }

    /// <summary>Copy of the (ms, active) curve, ms relative to cohort start.</summary>
    public List<(int Ms, int Active)> ActiveCurve()
    {
        lock (_activeCurve)
            return new List<(int, int)>(_activeCurve);
    }
}
