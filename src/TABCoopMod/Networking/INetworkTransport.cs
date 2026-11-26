using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DXVision;
using Steamworks;
using TABCoopMod.Commands;
using TABCoopMod.Core;

namespace TABCoopMod.Networking
{
    public enum SendChannel : byte
    {
        ReliableOrdered = 0,     // Bulk reliable (snapshots, PosDriftSync, checksums)
        UnreliableSequenced = 1, // Unreliable low-latency (ally cursor/camera, RTT probe)
        ReliableCommand = 2      // High-priority reliable commands (inputs, turns, lobby, session control)
    }

    public enum PacketType : byte
    {
        HandshakeRequest = 0x01,
        HandshakeAccept = 0x02,
        LobbyProbe = 0x06,
        LobbyProbeAck = 0x07,
        LobbyRosterSync = 0x08,
        ClientReadyState = 0x09,
        LobbyHostNotice = 0x0A,
        DirectEndpointExchange = 0x0B,
        DirectCutoverSync = 0x0C,
        DirectFallbackBatch = 0x0D,
        SnapshotBegin = 0x10,
        SnapshotChunk = 0x11,
        SnapshotLoadedAck = 0x12,
        SessionStart = 0x13,
        ClientInputReport = 0x20,
        TurnInputBroadcast = 0x21,
        HostPauseKeepalive = 0x22,
        LevelEnded = 0x23,
        SymmetricTurnInput = 0x24,
        StateChecksumReport = 0x30,
        DesyncDetectedAlert = 0x31,
        PosDriftSyncChunk = 0x32,
        PosDriftStatsReport = 0x33,
        StateChecksumDetailRequest = 0x34,
        StateChecksumDetailResponse = 0x35,
        AllyCursorAndCamera = 0x40,
        TacticalPing = 0x41,
        RttPing = 0x42,
        RttPong = 0x43
    }

    public interface ITransportDiagnostics
    {
        bool IsDirectConnected { get; }
        string ActivePathSummary { get; }
        double ActiveDirectRttMs { get; }
        string GetDebugStatsLine();
    }

    public sealed class PeerInfo
    {
        public byte PlayerId { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public ulong SteamId64 { get; set; }
        public bool IsReady { get; set; }
    }

    public sealed class LocalProbeResult
    {
        public bool IsOnline { get; set; }
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public byte RoomPhase { get; set; }
        public string HostName { get; set; } = string.Empty;
    }

    public interface INetworkTransport : IDisposable
    {
        bool IsActive { get; }
        bool IsHost { get; }
        byte LocalPlayerId { get; }
        IReadOnlyList<PeerInfo> ConnectedPeers { get; }

        event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        event Action<PeerInfo> OnPeerConnected;
        event Action<byte> OnPeerDisconnected;
        event Action<string, string, string> OnVersionMismatch;

        void StartHost(int maxPlayers = CoopLimits.MaxPlayers);
        void ConnectToHost(string targetAddressOrSteamId);
        void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void PollEvents();
        void SyncPeers(IEnumerable<PeerInfo> peers);
    }

    /// <summary>
    /// Local TCP Loopback Transport (127.0.0.1:27099) for AI Agent automated dual-instance testing
    /// and local multi-window debugging without Steam Lobby overhead.
    /// </summary>
    public sealed class LocalTcpLoopbackTransport : INetworkTransport, ITransportDiagnostics
    {
        public const int DefaultPort = 27099;

        private TcpListener _listener;
        private readonly List<(TcpClient Client, NetworkStream Stream, long AcceptedAtTicks)> _pendingAccepts = new List<(TcpClient, NetworkStream, long)>();
        private readonly List<(byte PlayerId, TcpClient Client, NetworkStream Stream)> _clients = new List<(byte, TcpClient, NetworkStream)>();
        private TcpClient _clientToHost;
        private NetworkStream _streamToHost;
        private readonly List<PeerInfo> _peers = new List<PeerInfo>();
        private int _maxPlayers = CoopLimits.MaxPlayers;

        /// <summary>
        /// When true, this transport is used as a plain LAN TCP transport (bind 0.0.0.0 / join IP:port)
        /// instead of same-machine loopback testing. The wire protocol is identical.
        /// </summary>
        public bool IsLanMode { get; set; }
        public string BindAddress { get; set; } = "127.0.0.1";
        public int ListenPort { get; set; } = DefaultPort;
        public string JoinAddress { get; set; } = "127.0.0.1";
        public int JoinPort { get; set; } = DefaultPort;

        /// <summary>
        /// Optional callback returning current Host lobby phase byte for non-intrusive LobbyProbeAck replies.
        /// </summary>
        public static Func<byte> HostRoomPhaseProvider { get; set; }

        public bool IsActive { get; private set; }
        public bool IsHost { get; private set; }
        public byte LocalPlayerId { get; private set; } = 1;
        public IReadOnlyList<PeerInfo> ConnectedPeers => _peers;

        public bool IsDirectConnected => IsActive;
        public string ActivePathSummary => IsLanMode
            ? $"LAN TCP ({(IsHost ? $"{BindAddress}:{ListenPort}" : $"{JoinAddress}:{JoinPort}")})"
            : "Local Loopback (127.0.0.1)";
        public double ActiveDirectRttMs => 0.0;
        public string GetDebugStatsLine() => $"LocalTcpLoopback(Active={IsActive}, Peers={_peers.Count})";

        public event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        public event Action<PeerInfo> OnPeerConnected;
        public event Action<byte> OnPeerDisconnected;
        public event Action<string, string, string> OnVersionMismatch;

        public void StartHost(int maxPlayers = CoopLimits.MaxPlayers)
        {
            Dispose();
            IsHost = true;
            LocalPlayerId = 1;
            _maxPlayers = Math.Max(2, maxPlayers);
            _peers.Clear();
            _peers.Add(new PeerInfo { PlayerId = 1, PlayerName = "HostPlayer", IsReady = true });

            IPAddress bindAddr = IPAddress.Loopback;
            string bind = (BindAddress ?? string.Empty).Trim();
            if (bind.Length > 0)
            {
                if (bind == "0.0.0.0" || bind == "*")
                {
                    bindAddr = IPAddress.Any;
                }
                else if (!IPAddress.TryParse(bind, out bindAddr))
                {
                    bindAddr = IPAddress.Any;
                }
            }

            int port = (ListenPort > 0 && ListenPort <= 65535) ? ListenPort : DefaultPort;
            _listener = new TcpListener(bindAddr, port);
            _listener.Start();
            IsActive = true;
            ModLogger.Info($"[LocalTcpLoopbackTransport] Host listening on {bindAddr}:{port} (LanMode={IsLanMode})");
        }

        public void ConnectToHost(string targetAddressOrSteamId)
        {
            Dispose();
            IsHost = false;
            LocalPlayerId = 0; // Assigned by Host upon HandshakeAccept
            _peers.Clear();

            string host = string.IsNullOrWhiteSpace(JoinAddress) ? "127.0.0.1" : JoinAddress.Trim();
            int port = (JoinPort > 0 && JoinPort <= 65535) ? JoinPort : DefaultPort;
            string target = targetAddressOrSteamId?.Trim();
            if (!string.IsNullOrEmpty(target))
            {
                if (target.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = target.IndexOf(']');
                    if (close > 0)
                    {
                        host = target.Substring(1, close - 1);
                        if (close + 1 < target.Length && target[close + 1] == ':'
                            && int.TryParse(target.Substring(close + 2), out int ipv6Port))
                        {
                            port = ipv6Port;
                        }
                    }
                }
                else if (target.Contains(":"))
                {
                    int firstColon = target.IndexOf(':');
                    int lastColon = target.LastIndexOf(':');
                    if (firstColon == lastColon)
                    {
                        host = target.Substring(0, lastColon);
                        if (int.TryParse(target.Substring(lastColon + 1), out int parsedPort))
                            port = parsedPort;
                    }
                    else
                    {
                        // Bare IPv6 address without a port (e.g. "2001:db8::1").
                        host = target;
                    }
                }
                else
                {
                    host = target;
                }
            }

            JoinAddress = host;
            JoinPort = port;

            _clientToHost = new TcpClient { NoDelay = true };
            int timeoutMs = 8000;
            if (int.TryParse(Environment.GetEnvironmentVariable("TABCOOP_LAN_CONNECT_TIMEOUT_MS"), out int envTimeout) && envTimeout > 0)
                timeoutMs = envTimeout;

            try
            {
                var iar = _clientToHost.BeginConnect(host, port, null, null);
                if (!iar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    try { _clientToHost.Close(); } catch { }
                    throw new TimeoutException($"连接 {host}:{port} 超时（{timeoutMs}ms）。");
                }
                _clientToHost.EndConnect(iar);
            }
            catch
            {
                Dispose();
                throw;
            }

            _streamToHost = _clientToHost.GetStream();
            IsActive = true;

            ModLogger.Info($"[LocalTcpLoopbackTransport] Connected to Host at {host}:{port}, sending HandshakeRequest...");
            SendToPeer(1, SendChannel.ReliableCommand, PacketType.HandshakeRequest, w =>
            {
                w.Write(Environment.UserName ?? "ClientPlayer");
                w.Write(ModInfo.Version);
            });
        }

        public void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            byte[] frame = BuildFrame(LocalPlayerId, type, writePayload);

            if (IsHost)
            {
                foreach (var c in _clients)
                {
                    if (c.PlayerId == targetPlayerId)
                    {
                        TryWriteFrame(c.Stream, frame);
                        break;
                    }
                }
            }
            else if (_streamToHost != null)
            {
                TryWriteFrame(_streamToHost, frame);
            }
        }

        public void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            byte[] frame = BuildFrame(LocalPlayerId, type, writePayload);

            if (IsHost)
            {
                foreach (var c in _clients)
                {
                    TryWriteFrame(c.Stream, frame);
                }
            }
            else if (_streamToHost != null)
            {
                TryWriteFrame(_streamToHost, frame);
            }
        }

        public void PollEvents()
        {
            if (!IsActive) return;

            try
            {
                if (IsHost)
                {
                    while (_listener != null && _listener.Pending())
                    {
                        var tcp = _listener.AcceptTcpClient();
                        tcp.NoDelay = true;
                        _pendingAccepts.Add((tcp, tcp.GetStream(), DateTime.UtcNow.Ticks));
                    }

                    long nowTicks = DateTime.UtcNow.Ticks;
                    for (int i = _pendingAccepts.Count - 1; i >= 0; i--)
                    {
                        var pending = _pendingAccepts[i];
                        if (pending.Client == null || pending.Client.Client == null || !pending.Client.Connected)
                        {
                            ClosePending(i);
                            continue;
                        }

                        if (!pending.Stream.DataAvailable)
                        {
                            if (nowTicks - pending.AcceptedAtTicks > TimeSpan.FromSeconds(3).Ticks || IsSocketDisconnected(pending.Client))
                                ClosePending(i);
                            continue;
                        }

                        if (!TryReadSingleFrame(pending.Stream, out var pktType, out byte senderId, out byte[] payload))
                        {
                            ClosePending(i);
                            continue;
                        }

                        if (pktType == PacketType.LobbyProbe)
                        {
                            byte phase = HostRoomPhaseProvider != null ? HostRoomPhaseProvider() : (byte)1;
                            string hostName = _peers.Count > 0 ? (_peers[0].PlayerName ?? "HostPlayer") : "HostPlayer";
                            byte[] ack = BuildFrame(1, PacketType.LobbyProbeAck, w =>
                            {
                                w.Write((byte)_peers.Count);
                                w.Write((byte)_maxPlayers);
                                w.Write(phase);
                                w.Write(hostName);
                            });
                            TryWriteFrame(pending.Stream, ack);
                            ClosePending(i);
                            continue;
                        }

                        if (pktType == PacketType.HandshakeRequest)
                        {
                            byte assignedId = AllocateLowestFreePlayerId();
                            if (_peers.Count >= _maxPlayers || assignedId == 0)
                            {
                                ClosePending(i);
                                continue;
                            }

                            _pendingAccepts.RemoveAt(i);
                            _clients.Add((assignedId, pending.Client, pending.Stream));
                            ModLogger.Info($"[LocalTcpLoopbackTransport] Accepted client connection -> Assigned PlayerId={assignedId}");

                            using (var ms = new MemoryStream(payload, false))
                            using (var reader = new BinaryReader(ms))
                            {
                                HandleInternalOrDispatch(assignedId, pktType, reader);
                            }

                            if (!ReadAvailableFrames(pending.Stream, assignedId))
                            {
                                RemoveClientById(assignedId);
                            }
                            continue;
                        }

                        ClosePending(i);
                    }

                    for (int i = _clients.Count - 1; i >= 0; i--)
                    {
                        var entry = _clients[i];
                        if (!ReadAvailableFrames(entry.Stream, entry.PlayerId) || IsSocketDisconnected(entry.Client))
                        {
                            byte deadId = entry.PlayerId;
                            try { entry.Stream?.Close(); entry.Client?.Close(); } catch { }
                            _clients.RemoveAt(i);
                            _peers.RemoveAll(p => p.PlayerId == deadId);
                            OnPeerDisconnected?.Invoke(deadId);
                        }
                    }
                }
                else if (_streamToHost != null)
                {
                    if (!ReadAvailableFrames(_streamToHost, 1) || IsSocketDisconnected(_clientToHost))
                    {
                        ModLogger.Warn("[LocalTcpLoopbackTransport] Disconnected from Host.");
                        Dispose();
                        OnPeerDisconnected?.Invoke(1);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[LocalTcpLoopbackTransport] Exception in PollEvents", ex);
            }
        }

        private byte AllocateLowestFreePlayerId()
        {
            int limit = Math.Min(255, Math.Max(2, _maxPlayers));
            for (int candidate = 2; candidate <= limit; candidate++)
            {
                byte id = (byte)candidate;
                bool used = false;
                for (int i = 0; i < _clients.Count; i++)
                {
                    if (_clients[i].PlayerId == id) { used = true; break; }
                }
                if (!used)
                {
                    for (int i = 0; i < _peers.Count; i++)
                    {
                        if (_peers[i].PlayerId == id) { used = true; break; }
                    }
                }
                if (!used) return id;
            }
            return 0;
        }

        private void ClosePending(int index)
        {
            var p = _pendingAccepts[index];
            _pendingAccepts.RemoveAt(index);
            try { p.Stream?.Close(); p.Client?.Close(); } catch { }
        }

        private void RemoveClientById(byte playerId)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (_clients[i].PlayerId == playerId)
                {
                    var entry = _clients[i];
                    try { entry.Stream?.Close(); entry.Client?.Close(); } catch { }
                    _clients.RemoveAt(i);
                }
            }
            _peers.RemoveAll(p => p.PlayerId == playerId);
            OnPeerDisconnected?.Invoke(playerId);
        }

        private static bool IsSocketDisconnected(TcpClient client)
        {
            try
            {
                if (client == null || client.Client == null || !client.Connected) return true;
                return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0;
            }
            catch
            {
                return true;
            }
        }

        public static LocalProbeResult ProbeLocalHost(string host = "127.0.0.1", int port = DefaultPort, int timeoutMs = 250)
        {
            var result = new LocalProbeResult { IsOnline = false };
            TcpClient tcp = null;
            try
            {
                tcp = new TcpClient { NoDelay = true, ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
                var iar = tcp.BeginConnect(host, port, null, null);
                if (!iar.AsyncWaitHandle.WaitOne(timeoutMs))
                    return result;
                tcp.EndConnect(iar);

                using (var stream = tcp.GetStream())
                {
                    stream.ReadTimeout = timeoutMs;
                    stream.WriteTimeout = timeoutMs;
                    byte[] probe = BuildFrame(0, PacketType.LobbyProbe, null);
                    stream.Write(probe, 0, probe.Length);
                    stream.Flush();

                    if (TryReadSingleFrame(stream, out var pktType, out _, out byte[] payload) && pktType == PacketType.LobbyProbeAck)
                    {
                        using (var ms = new MemoryStream(payload, false))
                        using (var reader = new BinaryReader(ms))
                        {
                            result.IsOnline = true;
                            result.PlayerCount = reader.ReadByte();
                            result.MaxPlayers = reader.ReadByte();
                            result.RoomPhase = reader.ReadByte();
                            result.HostName = reader.ReadString();
                        }
                    }
                }
            }
            catch
            {
                // Host not listening or timed out; keep IsOnline = false quietly.
            }
            finally
            {
                try { tcp?.Close(); } catch { }
            }
            return result;
        }

        private static bool TryReadSingleFrame(NetworkStream stream, out PacketType pktType, out byte senderId, out byte[] payload)
        {
            pktType = 0;
            senderId = 0;
            payload = null;
            try
            {
                byte[] header = new byte[8];
                int read = 0;
                while (read < 8)
                {
                    int n = stream.Read(header, read, 8 - read);
                    if (n <= 0) return false;
                    read += n;
                }
                if (header[0] != 0x54 || header[1] != 0x42) return false;
                pktType = (PacketType)header[2];
                senderId = header[3];
                int payloadLen = BitConverter.ToInt32(header, 4);
                if (payloadLen < 0 || payloadLen > 4 * 1024 * 1024) return false;
                payload = new byte[payloadLen];
                int offset = 0;
                while (offset < payloadLen)
                {
                    int n = stream.Read(payload, offset, payloadLen - offset);
                    if (n <= 0) return false;
                    offset += n;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ReadAvailableFrames(NetworkStream stream, byte defaultSenderId)
        {
            try
            {
                while (stream.DataAvailable)
                {
                    byte[] header = new byte[8];
                    int read = 0;
                    while (read < 8)
                    {
                        int n = stream.Read(header, read, 8 - read);
                        if (n <= 0) return false;
                        read += n;
                    }

                    if (header[0] != 0x54 || header[1] != 0x42)
                    {
                        ModLogger.Warn("[LocalTcpLoopbackTransport] Invalid frame magic!");
                        return false;
                    }

                    var pktType = (PacketType)header[2];
                    byte senderId = header[3] != 0 ? header[3] : defaultSenderId;
                    int payloadLen = BitConverter.ToInt32(header, 4);

                    byte[] payload = new byte[payloadLen];
                    int offset = 0;
                    while (offset < payloadLen)
                    {
                        int n = stream.Read(payload, offset, payloadLen - offset);
                        if (n <= 0) return false;
                        offset += n;
                    }

                    using (var ms = new MemoryStream(payload, false))
                    using (var reader = new BinaryReader(ms))
                    {
                        HandleInternalOrDispatch(senderId, pktType, reader);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void HandleInternalOrDispatch(byte senderId, PacketType pktType, BinaryReader reader)
        {
            if (IsHost && pktType == PacketType.HandshakeRequest)
            {
                string clientName = reader.ReadString();
                string version = reader.ReadString();
                bool versionMatched = string.Equals(version, ModInfo.Version, StringComparison.Ordinal);
                if (!versionMatched)
                    ModLogger.Warn($"[LocalTcpLoopbackTransport] Version mismatch! Host={ModInfo.Version}, Client({clientName})={version}");
                else
                    ModLogger.Info($"[LocalTcpLoopbackTransport] Handshake from {clientName} (ver={version}).");
                var peer = new PeerInfo { PlayerId = senderId, PlayerName = $"{clientName}#{senderId}", IsReady = false };
                _peers.RemoveAll(p => p.PlayerId == senderId);
                _peers.Add(peer);

                SendToPeer(senderId, SendChannel.ReliableCommand, PacketType.HandshakeAccept, w =>
                {
                    w.Write(senderId);
                    w.Write((byte)_peers.Count);
                    foreach (var p in _peers)
                    {
                        w.Write(p.PlayerId);
                        w.Write(p.PlayerName);
                        w.Write(p.IsReady);
                    }
                    w.Write(ModInfo.Version);
                });

                OnPeerConnected?.Invoke(peer);
                if (!versionMatched)
                    OnVersionMismatch?.Invoke(clientName, ModInfo.Version, version);
                return;
            }

            if (!IsHost && pktType == PacketType.HandshakeAccept)
            {
                LocalPlayerId = reader.ReadByte();
                byte count = reader.ReadByte();
                _peers.Clear();
                for (int i = 0; i < count; i++)
                {
                    _peers.Add(new PeerInfo
                    {
                        PlayerId = reader.ReadByte(),
                        PlayerName = reader.ReadString(),
                        IsReady = reader.ReadBoolean()
                    });
                }
                string hostVersion = reader.BaseStream.Position < reader.BaseStream.Length
                    ? reader.ReadString()
                    : "<=0.1.2";
                ModLogger.Info($"[LocalTcpLoopbackTransport] HandshakeAccepted! LocalPlayerId={LocalPlayerId}, Peers={_peers.Count}, HostVer={hostVersion}");
                OnPeerConnected?.Invoke(_peers.Find(p => p.PlayerId == LocalPlayerId));
                if (!string.Equals(hostVersion, ModInfo.Version, StringComparison.Ordinal))
                {
                    string hostName = _peers.Find(p => p.PlayerId == 1)?.PlayerName ?? "Host";
                    ModLogger.Warn($"[LocalTcpLoopbackTransport] Version mismatch! Client={ModInfo.Version}, Host({hostName})={hostVersion}");
                    OnVersionMismatch?.Invoke(hostName, ModInfo.Version, hostVersion);
                }
                return;
            }

            OnPacketReceived?.Invoke(senderId, pktType, reader);
        }

        public void SyncPeers(IEnumerable<PeerInfo> peers)
        {
            _peers.Clear();
            if (peers == null) return;
            foreach (var p in peers)
            {
                if (p == null) continue;
                _peers.Add(new PeerInfo
                {
                    PlayerId = p.PlayerId,
                    PlayerName = p.PlayerName ?? string.Empty,
                    SteamId64 = p.SteamId64,
                    IsReady = p.IsReady
                });
            }
        }

        internal static byte[] BuildFrame(byte senderId, PacketType type, Action<BinaryWriter> writePayload)
        {
            using (var payloadMs = new MemoryStream())
            using (var payloadWriter = new BinaryWriter(payloadMs))
            {
                writePayload?.Invoke(payloadWriter);
                payloadWriter.Flush();
                byte[] payloadBytes = payloadMs.ToArray();

                using (var frameMs = new MemoryStream(8 + payloadBytes.Length))
                using (var frameWriter = new BinaryWriter(frameMs))
                {
                    frameWriter.Write((byte)0x54); // 'T'
                    frameWriter.Write((byte)0x42); // 'B'
                    frameWriter.Write((byte)type);
                    frameWriter.Write(senderId);
                    frameWriter.Write(payloadBytes.Length);
                    frameWriter.Write(payloadBytes);
                    frameWriter.Flush();
                    return frameMs.ToArray();
                }
            }
        }

        private static void TryWriteFrame(NetworkStream stream, byte[] frame)
        {
            try
            {
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            IsActive = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;

            foreach (var p in _pendingAccepts)
            {
                try { p.Stream?.Close(); p.Client?.Close(); } catch { }
            }
            _pendingAccepts.Clear();

            foreach (var c in _clients)
            {
                try { c.Stream?.Close(); c.Client?.Close(); } catch { }
            }
            _clients.Clear();

            try { _streamToHost?.Close(); _clientToHost?.Close(); } catch { }
            _streamToHost = null;
            _clientToHost = null;
        }
    }
}
