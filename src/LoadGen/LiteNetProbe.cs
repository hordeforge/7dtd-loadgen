using System.Diagnostics;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SevenDTD.LoadGen;

public static class LiteNetProbe
{
    public static ProbeResult Run(
        string host, int port, string key, int timeoutMs, int clientId,
        Action<string>? writeLine = null, bool keepLines = true)
    {
        var lines = keepLines ? new List<string>() : new List<string>(0);
        var sw = Stopwatch.StartNew();
        void Log(string msg)
        {
            string line = $"[{DateTime.UtcNow:O}] [fake#{clientId}] {msg}";
            writeLine?.Invoke(line);
            if (keepLines) lines.Add(line);
        }
        Log($"loadtest client starting host={host} port={port} timeoutMs={timeoutMs}");
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 500;
            udp.Connect(host, port);
            Log("STAGE udp_socket_open: ok");
        }
        catch (Exception ex)
        {
            Log($"STAGE udp_socket_open: fail {ex.GetType().Name}: {ex.Message}");
            return new ProbeResult
            {
                Pass = false,
                Stages = new HashSet<string>(),
                Connected = false,
                Lines = lines,
                ElapsedMs = sw.ElapsedMilliseconds,
            };
        }

        var listener = new EventBasedNetListener();
        var net = new NetManager(listener) { AutoRecycle = true, DisconnectTimeout = timeoutMs, UpdateTime = 15 };
        bool connected = false, disconnected = false;
        string? disconnectReason = null;
        int packets = 0;
        var stages = new HashSet<string> { "udp_socket_open" };
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
        if (!net.Start())
        {
            Log("STAGE litenet_start: fail");
            return Fail(lines, stages, connected, disconnectReason, sw.ElapsedMilliseconds);
        }
        stages.Add("litenet_start");
        Log("STAGE litenet_start: ok");
        // Stop() must run on every exit path: LoadRunner drives up to thousands
        // of probes in one process, so an exception skipping the stop would
        // accumulate live UDP sockets + managers until process death.
        try
        {
            var data = new NetDataWriter();
            if (!string.IsNullOrEmpty(key)) data.Put(key);
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
                if (connected && (packets > 0 || sw.ElapsedMilliseconds > 1500)) break;
                if (disconnected && !connected) break;
                Thread.Sleep(10);
            }
            net.PollEvents();
            sw.Stop();
            bool pastSocket = stages.Contains("litenet_peer_connected") || stages.Contains("litenet_receive")
                || stages.Contains("litenet_peer_disconnected") || stages.Contains("protocol_bytes");
            bool pass = pastSocket || (stages.Contains("litenet_connect_call") && (connected || disconnectReason != null));
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
