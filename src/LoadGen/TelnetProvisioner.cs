namespace SevenDTD.LoadGen;

/// <summary>
/// Cohort-shared telnet console for per-life server provisioning (the dynamite
/// grant). Each bot used to open its own TCP session and block its action loop
/// for the full round trip, so at cohort scale every life cost a connection
/// plus a blocking read, and the dedicated's single console serialized all of
/// them. One connection, one worker, one bounded queue: a bot enqueues and
/// keeps walking, which is what a load generator should be doing.
/// </summary>
public sealed class TelnetProvisioner : IDisposable
{
    readonly Func<TelnetAdmin> _createAdmin;
    readonly Action<string> _log;
    readonly Queue<Request> _queue = new();
    readonly object _gate = new();
    readonly Thread _worker;
    volatile bool _stopping;
    long _dropped;

    // A queued grant is worthless once it is minutes old, so the queue is a
    // window onto the next few seconds of lives, not a backlog to drain
    // forever. Overflow drops the oldest, which is also the one whose bot has
    // lived the longest without dynamite.
    internal const int MaxQueued = 1024;
    internal const int ShutdownWaitMs = 2_000;

    readonly record struct Request(string Command, int EntityId, Action<string>? Log);

    public TelnetProvisioner(Func<TelnetAdmin> createAdmin, Action<string> log)
    {
        _createAdmin = createAdmin;
        _log = log;
        _worker = new Thread(Work) { IsBackground = true, Name = "telnet-provisioner" };
        _worker.Start();
    }

    /// <summary>Grants that never reached the console because the queue was
    /// full. Non-zero means the cohort outran the console, not a silent loss.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queue a console command. Never blocks the caller: a full queue
    /// drops the oldest request rather than stalling the bot's action loop.</summary>
    public void Enqueue(string command, int entityId, Action<string>? log)
    {
        if (_stopping) return;
        lock (_gate)
        {
            while (_queue.Count >= MaxQueued)
            {
                _queue.Dequeue();
                Interlocked.Increment(ref _dropped);
            }
            _queue.Enqueue(new Request(command, entityId, log));
        }
    }

    bool TryDequeue(out Request request)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                request = default;
                return false;
            }
            request = _queue.Dequeue();
            return true;
        }
    }

    void Work()
    {
        TelnetAdmin? admin = null;
        try
        {
            while (true)
            {
                if (!TryDequeue(out var req))
                {
                    if (_stopping) return;
                    Thread.Sleep(ReadPollIntervalMs);
                    continue;
                }
                if (admin == null)
                {
                    try
                    {
                        admin = _createAdmin();
                        if (!admin.Connect())
                        {
                            // The session is down, not the request: report the
                            // entity once and retry on the next grant rather
                            // than reconnecting per bot life.
                            Report(req, $"telnet connect failed entity={req.EntityId}");
                            admin.Dispose();
                            admin = null;
                            // A console that refuses costs a full reconnect
                            // attempt; without this the queue turns a downed
                            // console into a connect-per-grant spin.
                            Thread.Sleep(ReconnectBackoffMs);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Report(req, $"telnet setup failed entity={req.EntityId} {ex.GetType().Name}: {ex.Message}");
                        admin?.Dispose();
                        admin = null;
                        continue;
                    }
                }
                try
                {
                    string response = admin.Exec(req.Command);
                    req.Log?.Invoke(
                        $"[{DateTime.UtcNow:O}] PROVISION entity={req.EntityId} "
                        + $"cmd={req.Command} response={response.Trim()}");
                }
                catch (Exception ex)
                {
                    // A dropped console session must not kill the worker: the
                    // next request reconnects, and the cohort keeps walking.
                    Report(req, $"telnet exec failed entity={req.EntityId} {ex.GetType().Name}: {ex.Message}");
                    admin.Dispose();
                    admin = null;
                }
            }
        }
        finally
        {
            admin?.Dispose();
        }
    }

    void Report(Request req, string message) => (req.Log ?? _log)($"[{DateTime.UtcNow:O}] {message}");

    const int ReadPollIntervalMs = 20;
    const int ReconnectBackoffMs = 200;

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        if (!_worker.Join(ShutdownWaitMs))
            _log($"[{DateTime.UtcNow:O}] PROVISION shutdown timeout; dropped={Dropped}");
        if (Dropped > 0)
            _log($"[{DateTime.UtcNow:O}] PROVISION dropped={Dropped}");
    }
}
