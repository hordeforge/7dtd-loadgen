using System.Diagnostics;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SevenDTD.LoadGen;

public static class LiteNetProbe
{
    /// <summary>How long to keep polling after the peer connects while waiting
    /// for the first application packet. Measured from the connect call, not
    /// from probe start: a slow DNS resolution or connect handshake would
    /// otherwise consume the whole window before the peer is even up.</summary>
    private const int GraceAfterConnectMs = 1500;

    public static ProbeResult Run(
        string host, int port, string key, int timeoutMs, int clientId,
        Action<string>? writeLine = null, bool keepLines = true)
    {
        var lines = new List<string>();
        var stages = new HashSet<string>();
        var sw = Stopwatch.StartNew();
        void Log(string msg)
        {
            string line = RunReport.Event("INFO", $"[fake#{clientId}] {msg}");
            writeLine?.Invoke(line);
            if (keepLines) lines.Add(line);
        }
        Log($"loadtest client starting host={host} port={port} timeoutMs={timeoutMs}");
        try
        {
            using var udp = new UdpClient();
            udp.Connect(host, port);
            Log("STAGE udp_socket_open: ok");
            stages.Add("udp_socket_open");
        }
        catch (Exception ex)
        {
            Log($"STAGE udp_socket_open: fail {ex.GetType().Name}: {ex.Message}");
            return Fail(lines, stages, connected: false, disc: null, sw.ElapsedMilliseconds);
        }

        var listener = new EventBasedNetListener();
        var net = new NetManager(listener) { AutoRecycle = true, DisconnectTimeout = timeoutMs, UpdateTime = 15 };
        bool connected = false, disconnected = false;
        string? disconnectReason = null;
        int packets = 0;
        listener.PeerConnectedEvent += peer =>
        {
            connected = true;
            stages.Add("litenet_peer_connected");
            Log($"STAGE litenet_peer_connected: {peer.Address}:{peer.Port}");
        };
        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            disconnected = true;
            disconnectReason = info.Reason.ToString();
            stages.Add("litenet_peer_disconnected");
            Log($"STAGE litenet_peer_disconnected: reason={info.Reason}");
        };
        listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            packets++;
            stages.Add("litenet_receive");
            if (reader.AvailableBytes > 0) stages.Add("protocol_bytes");
            reader.Recycle();
        };
        // Stop() must run on every exit path, and the Start() call is inside
        // that guarantee: LoadRunner drives up to thousands of probes in one
        // process, so a failed Start (or an exception out of it) skipping the
        // stop would accumulate live UDP sockets + managers until process
        // death. LiteNetLib can bind the socket and still fail the rest of
        // Start, and a throw escapes the method entirely.
        try
        {
            if (!net.Start())
            {
                Log("STAGE litenet_start: fail");
                return Fail(lines, stages, connected, disconnectReason, sw.ElapsedMilliseconds);
            }
            stages.Add("litenet_start");
            Log("STAGE litenet_start: ok");
            var data = new NetDataWriter();
            if (!string.IsNullOrEmpty(key)) data.Put(key);
            long connectStartMs = sw.ElapsedMilliseconds;
            var peer = net.Connect(host, port, data);
            if (peer == null)
            {
                Log("STAGE litenet_connect_call: fail");
                return Fail(lines, stages, connected, disconnectReason, sw.ElapsedMilliseconds);
            }
            stages.Add("litenet_connect_call");
            Log("STAGE litenet_connect_call: ok");
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                net.PollEvents();
                if (connected && (packets > 0 || sw.ElapsedMilliseconds - connectStartMs > GraceAfterConnectMs)) break;
                if (disconnected && !connected) break;
                Thread.Sleep(10);
            }
            net.PollEvents();
            sw.Stop();
            // The probe reached the server if the peer ever connected or the
            // server ever hung up: a disconnect with a reason is as much proof
            // of a live socket as a connect is. Every stage that implies one of
            // those is set by the same events, so key off the two outcomes.
            bool reachedServer = connected || disconnectReason != null;
            bool pass = reachedServer && stages.Contains("litenet_connect_call");
            Log($"SUMMARY stages=[{string.Join(",", stages.OrderBy(s => s))}] connected={connected} packets={packets}");
            return new ProbeResult
            {
                Pass = pass,
                Stages = stages,
                Connected = connected,
                DisconnectReason = disconnectReason,
                Lines = lines,
                ElapsedMs = sw.ElapsedMilliseconds,
            };
        }
        finally
        {
            try { net.Stop(); } catch { /* release must not mask the result */ }
        }
    }

    static ProbeResult Fail(List<string> lines, HashSet<string> stages, bool connected, string? disc, long ms) =>
        new() { Pass = false, Stages = stages, Connected = connected, DisconnectReason = disc, Lines = lines, ElapsedMs = ms };
}
