using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TABCoopMod.Core;

namespace TABCoopMod.Networking
{
    public enum DirectCandidateKind : byte
    {
        LanIPv4 = 1,
        VirtualLan = 2,
        PublicIPv6 = 3,
        PublicIPv4 = 4,
        UpnpIPv4 = 5,
        StunIPv4 = 6
    }

    public sealed class DirectEndpointCandidate
    {
        public DirectCandidateKind Kind;
        public IPAddress Address;
        public int Port;

        public override string ToString() => $"{Kind}:{Address}:{Port}";
    }

    /// <summary>
    /// Dual-stack (IPv4/IPv6) UDP Fast-Path upgrade layer orchestrated over Steam P2P signaling.
    /// Supports LAN, Virtual LAN (Tailscale/ZeroTier/Radmin/Pugongying), Public IPv6, UPnP IGD,
    /// and STUN Cone-NAT simultaneous open hole-punching with independent ReliableCommand (ch 2),
    /// ReliableBulk (ch 0), and UnreliableSequenced (ch 1) channels + lossless Steam fallback.
    /// </summary>
    public sealed class DirectFastPathManager : IDisposable
    {
        private static readonly byte[] Magic = { 0x54, 0x42, 0x44, 0x50 }; // 'TBDP'
        private static readonly byte[] StunMagicCookie = { 0x21, 0x12, 0xA4, 0x42 };

        private const byte KindBulk = 0x00;
        private const byte KindUnreliable = 0x01;
        private const byte KindCommand = 0x02;
        private const byte KindAckOnly = 0x10;
        private const byte KindProbePing = 0xFE;
        private const byte KindProbePong = 0xFF;

        private const byte FlagMsgStart = 0x01;
        private const byte FlagMsgEnd = 0x02;

        private const int ChunkPayloadBytes = 1080;
        private const int CommandWindowChunks = 48;
        private const int BulkWindowChunks = 48;
        private const double KeepaliveHeartbeatMs = 1000.0;
        private const double DiscoveryProbeIntervalMs = 120.0;
        private const double DirectTimeoutMs = 5500.0;
        private const double DirectDataStallFallbackMs = 3000.0;
        private const double DirectBulkStallFallbackMs = 6000.0;
        private const int SIO_UDP_CONNRESET = -1744830452;

        private static readonly string[] StunHosts =
        {
            "stun.qq.com:3478",
            "stun.miwifi.com:3478",
            "stun.cloudflare.com:3478"
        };

        private sealed class OutChunk
        {
            public uint Seq;
            public uint MsgId;
            public byte Flags;
            public byte PktType;
            public byte[] Data;
            public long LastSentTs;
            public int Retransmits;
        }

        private sealed class UnackedMessage
        {
            public uint MsgId;
            public uint EndSeq;
            public SendChannel Channel;
            public PacketType PktType;
            public byte[] Payload;
        }

        private sealed class ChannelTxState
        {
            public uint NextSeq = 1;
            public uint NextMsgId = 1;
            public uint AckBase;
            public readonly Dictionary<uint, OutChunk> InFlight = new Dictionary<uint, OutChunk>();
            public readonly Queue<OutChunk> PendingChunks = new Queue<OutChunk>();
            public readonly Queue<UnackedMessage> UnackedMessages = new Queue<UnackedMessage>();
        }

        private sealed class RxChunk
        {
            public byte Flags;
            public uint MsgId;
            public byte PktType;
            public byte[] Data;
        }

        private sealed class ChannelRxState
        {
            public uint ExpectedSeq = 1;
            public ulong SackMask;
            public bool AckDirty;
            public readonly SortedDictionary<uint, RxChunk> Buffered = new SortedDictionary<uint, RxChunk>();
            public readonly MemoryStream AssemblingMsg = new MemoryStream(4096);
            public byte AssemblingPktType;
            public uint AssemblingMsgId;
            public bool IsAssembling;
            public uint LastDeliveredMsgId;
            public readonly HashSet<uint> DeliveredFallbackIds = new HashSet<uint>();
        }

        private sealed class CandidateProbeState
        {
            public DirectEndpointCandidate Candidate;
            public IPEndPoint SocketTarget;
            public long LastPingSentTs;
            public long LastPongRecvTs;
            public double SmoothedRttMs = -1.0;
            public int PongCount;
            public int ProbeSeq;
        }

        private sealed class PeerDirectState
        {
            public byte PlayerId;
            public readonly object Sync = new object();
            public readonly List<CandidateProbeState> Candidates = new List<CandidateProbeState>();
            public CandidateProbeState ActiveCandidate;
            public long LastAnyPacketRecvTs;
            public long LastDataProgressTs;
            public long ActivatedTs;
            public long FallbackCooldownUntilTs;
            public int ConsecutiveDataStalls;
            public bool CircuitBroken;
            public bool VerifiedBidirectional;
            public bool TxEnabled;
            public bool CutoverSignaled;
            public bool CutoverAckedByPeer;
            public uint NextUnrelSeq = 1;
            public uint LastRecvUnrelSeq;
            public readonly ChannelTxState BulkTx = new ChannelTxState();
            public readonly ChannelTxState CmdTx = new ChannelTxState();
            public readonly ChannelRxState BulkRx = new ChannelRxState();
            public readonly ChannelRxState CmdRx = new ChannelRxState();
        }

        public struct DirectIncomingPacket
        {
            public byte SenderId;
            public PacketType Type;
            public byte[] Payload;
        }

        public struct FallbackMessage
        {
            public uint MsgId;
            public SendChannel Channel;
            public PacketType Type;
            public byte[] Payload;
        }

        private readonly bool _isHost;
        private readonly object _stateLock = new object();
        private readonly Dictionary<byte, PeerDirectState> _peers = new Dictionary<byte, PeerDirectState>();
        private readonly List<DirectEndpointCandidate> _localCandidates = new List<DirectEndpointCandidate>();
        private readonly ConcurrentQueue<DirectIncomingPacket> _incomingQueue = new ConcurrentQueue<DirectIncomingPacket>();
        private readonly Dictionary<long, byte[]> _stunTransactions = new Dictionary<long, byte[]>();

        private byte _localPlayerId = 1;
        private ulong _sessionToken;
        private Socket _udpSocket;
        private bool _isDualMode;
        private int _localUdpPort;
        private volatile bool _disposed;
        private volatile bool _discoveryCompleted;
        private Thread _recvThread;
        private Thread _pumpThread;
        private readonly AutoResetEvent _pumpWake = new AutoResetEvent(false);

        private string _upnpControlUrl;
        private string _upnpServiceType;
        private int _upnpMappedPort;

        public Action<IReadOnlyList<DirectEndpointCandidate>, ulong> LocalCandidatesUpdated;
        public Action<byte, bool> PeerDirectCutoverNeeded;
        public Action<byte, List<FallbackMessage>> PeerFallbackTriggered;

        private int _stunRefreshInFlight;
        private IPEndPoint[] _cachedStunEndpoints;

        public ulong SessionToken => _sessionToken;
        public int LocalUdpPort => _localUdpPort;

        public DirectFastPathManager(bool isHost)
        {
            _isHost = isHost;
            if (isHost)
            {
                var rng = new byte[8];
                using (var crng = System.Security.Cryptography.RandomNumberGenerator.Create())
                    crng.GetBytes(rng);
                _sessionToken = BitConverter.ToUInt64(rng, 0);
                if (_sessionToken == 0) _sessionToken = 0x544142434F4F5031UL;
            }

            StartSocketAndWorkers();
        }

        public void SetLocalPlayerId(byte localPlayerId)
        {
            _localPlayerId = localPlayerId;
        }

        public List<DirectEndpointCandidate> GetLocalCandidatesSnapshot()
        {
            lock (_stateLock)
                return new List<DirectEndpointCandidate>(_localCandidates);
        }

        public bool IsPeerDirectActive(byte playerId)
        {
            lock (_stateLock)
            {
                return _peers.TryGetValue(playerId, out var p) && p.TxEnabled && p.ActiveCandidate != null;
            }
        }

        public bool TryGetPeerSummary(byte playerId, out string summary, out double rttMs, out bool isDirect)
        {
            lock (_stateLock)
            {
                if (_peers.TryGetValue(playerId, out var p) && p.TxEnabled && p.ActiveCandidate != null)
                {
                    var c = p.ActiveCandidate.Candidate;
                    summary = $"Direct {c.Kind}";
                    rttMs = p.ActiveCandidate.SmoothedRttMs > 0 ? p.ActiveCandidate.SmoothedRttMs : 0.0;
                    isDirect = true;
                    return true;
                }
            }
            summary = null;
            rttMs = -1.0;
            isDirect = false;
            return false;
        }

        public bool TryGetActiveSummary(out string summary, out double rttMs, out bool isDirect)
        {
            lock (_stateLock)
            {
                foreach (var kv in _peers)
                {
                    var p = kv.Value;
                    if (p.TxEnabled && p.ActiveCandidate != null)
                    {
                        var c = p.ActiveCandidate.Candidate;
                        summary = $"Direct {c.Kind}";
                        rttMs = p.ActiveCandidate.SmoothedRttMs > 0 ? p.ActiveCandidate.SmoothedRttMs : 0.0;
                        isDirect = true;
                        return true;
                    }
                }
            }
            summary = null;
            rttMs = 0.0;
            isDirect = false;
            return false;
        }

        public string GetDebugStatsLine()
        {
            lock (_stateLock)
            {
                if (_peers.Count == 0) return $"DirectUDP(port={_localUdpPort}, peers=0)";
                var sb = new StringBuilder();
                sb.Append($"DirectUDP(port={_localUdpPort}, cands={_localCandidates.Count}");
                foreach (var kv in _peers)
                {
                    var p = kv.Value;
                    string active = p.ActiveCandidate != null ? $"{p.ActiveCandidate.Candidate.Kind}:{p.ActiveCandidate.SmoothedRttMs:F1}ms" : "none";
                    sb.Append($", P{kv.Key}[tx={p.TxEnabled}, verified={p.VerifiedBidirectional}, active={active}, cmdFly={p.CmdTx.InFlight.Count}, bulkFly={p.BulkTx.InFlight.Count}]");
                }
                sb.Append(')');
                return sb.ToString();
            }
        }

        public bool WasMessageDeliveredViaDirect(byte senderId, SendChannel channel, uint msgId)
        {
            if (msgId == 0) return false;
            lock (_stateLock)
            {
                if (!_peers.TryGetValue(senderId, out var peer)) return false;
                var rx = channel == SendChannel.ReliableCommand ? peer.CmdRx : peer.BulkRx;
                lock (peer.Sync)
                {
                    if (msgId <= rx.LastDeliveredMsgId) return true;
                    return !rx.DeliveredFallbackIds.Add(msgId);
                }
            }
        }

        public void SetRemotePeerCandidates(byte peerPlayerId, ulong sessionToken, IList<DirectEndpointCandidate> candidates)
        {
            if (_disposed || peerPlayerId == 0 || candidates == null) return;

            if (!_isHost && sessionToken != 0 && _sessionToken == 0)
                _sessionToken = sessionToken;
            else if (_isHost && sessionToken != 0 && sessionToken != _sessionToken)
                return;

            lock (_stateLock)
            {
                if (!_peers.TryGetValue(peerPlayerId, out var peer))
                {
                    peer = new PeerDirectState { PlayerId = peerPlayerId };
                    _peers[peerPlayerId] = peer;
                }

                lock (peer.Sync)
                {
                    foreach (var cand in candidates)
                    {
                        if (cand?.Address == null || cand.Port <= 0 || cand.Port > 65535) continue;
                        if (cand.Address.AddressFamily == AddressFamily.InterNetworkV6 && !_isDualMode) continue;

                        bool exists = false;
                        for (int i = 0; i < peer.Candidates.Count; i++)
                        {
                            var ex = peer.Candidates[i].Candidate;
                            if (ex.Port == cand.Port && ex.Address.Equals(cand.Address))
                            {
                                if (GetPriorityScore(cand.Kind) < GetPriorityScore(ex.Kind))
                                    ex.Kind = cand.Kind;
                                exists = true;
                                break;
                            }
                        }

                        if (!exists)
                        {
                            peer.Candidates.Add(new CandidateProbeState
                            {
                                Candidate = cand,
                                SocketTarget = ToSocketEndPoint(cand.Address, cand.Port)
                            });
                        }
                    }
                    ModLogger.Info($"[DirectFastPath] Registered {peer.Candidates.Count} candidate(s) for P{peerPlayerId}: {string.Join(", ", candidates)}");
                }
            }

            _pumpWake.Set();
        }

        public void EnableDirectTxAfterBarrier(byte peerPlayerId, bool peerSide)
        {
            lock (_stateLock)
            {
                if (_peers.TryGetValue(peerPlayerId, out var peer))
                {
                    lock (peer.Sync)
                    {
                        if (peerSide)
                            peer.CutoverAckedByPeer = true;
                        if (peer.ActiveCandidate != null && peer.VerifiedBidirectional)
                        {
                            if (!peer.TxEnabled)
                            {
                                long nowTs = Stopwatch.GetTimestamp();
                                peer.TxEnabled = true;
                                peer.ActivatedTs = nowTs;
                                peer.LastAnyPacketRecvTs = nowTs;
                                peer.LastDataProgressTs = nowTs;
                                ModLogger.Info($"[DirectFastPath] Direct Fast-Path ACTIVATED for P{peerPlayerId} via {peer.ActiveCandidate.Candidate} (RTT={peer.ActiveCandidate.SmoothedRttMs:F1}ms)!");
                            }
                        }
                    }
                }
            }
            _pumpWake.Set();
        }

        public void GetTxNextSeqs(byte peerPlayerId, out uint cmdNextSeq, out uint bulkNextSeq)
        {
            cmdNextSeq = 1;
            bulkNextSeq = 1;
            PeerDirectState peer;
            lock (_stateLock)
            {
                if (!_peers.TryGetValue(peerPlayerId, out peer)) return;
            }
            lock (peer.Sync)
            {
                cmdNextSeq = peer.CmdTx.NextSeq;
                bulkNextSeq = peer.BulkTx.NextSeq;
            }
        }

        public void SyncRxExpectedSeq(byte peerPlayerId, uint cmdExpectedSeq, uint bulkExpectedSeq)
        {
            PeerDirectState peer;
            lock (_stateLock)
            {
                if (!_peers.TryGetValue(peerPlayerId, out peer)) return;
            }
            lock (peer.Sync)
            {
                AdvanceRxExpectedSeq(peer.CmdRx, cmdExpectedSeq);
                AdvanceRxExpectedSeq(peer.BulkRx, bulkExpectedSeq);
            }
            _pumpWake.Set();
        }

        private static void AdvanceRxExpectedSeq(ChannelRxState rx, uint targetExpectedSeq)
        {
            if (targetExpectedSeq <= rx.ExpectedSeq) return;
            rx.ExpectedSeq = targetExpectedSeq;
            rx.SackMask = 0;
            rx.AssemblingMsg.SetLength(0);
            rx.IsAssembling = false;
            if (rx.Buffered.Count > 0)
            {
                var stale = new List<uint>();
                foreach (var seq in rx.Buffered.Keys)
                {
                    if (seq < targetExpectedSeq) stale.Add(seq);
                    else break;
                }
                for (int i = 0; i < stale.Count; i++)
                    rx.Buffered.Remove(stale[i]);
            }
            rx.AckDirty = true;
        }

        public bool TrySendPacket(byte targetPlayerId, SendChannel channel, PacketType type, byte[] payload)
        {
            if (_disposed || _udpSocket == null || _sessionToken == 0) return false;
            if (payload == null) payload = Array.Empty<byte>();

            // Unreliable packets > 1150B are left to Steam to avoid UDP fragmentation loss.
            if (channel == SendChannel.UnreliableSequenced && payload.Length > 1150)
                return false;

            PeerDirectState peer;
            lock (_stateLock)
            {
                if (!_peers.TryGetValue(targetPlayerId, out peer)) return false;
            }

            IPEndPoint destEp = null;
            uint unrelSeq = 0;

            lock (peer.Sync)
            {
                if (!peer.TxEnabled || peer.ActiveCandidate == null) return false;

                if (channel == SendChannel.UnreliableSequenced)
                {
                    destEp = peer.ActiveCandidate.SocketTarget;
                    unrelSeq = peer.NextUnrelSeq++;
                }
                else
                {
                    if (peer.CmdTx.InFlight.Count == 0 && peer.CmdTx.PendingChunks.Count == 0
                        && peer.BulkTx.InFlight.Count == 0 && peer.BulkTx.PendingChunks.Count == 0)
                    {
                        peer.LastDataProgressTs = Stopwatch.GetTimestamp();
                    }
                    var tx = channel == SendChannel.ReliableCommand ? peer.CmdTx : peer.BulkTx;
                    uint msgId = tx.NextMsgId++;
                    int len = payload.Length;
                    int numChunks = Math.Max(1, (len + ChunkPayloadBytes - 1) / ChunkPayloadBytes);
                    uint endSeq = tx.NextSeq + (uint)numChunks - 1;

                    for (int i = 0; i < numChunks; i++)
                    {
                        int offset = i * ChunkPayloadBytes;
                        int sliceLen = Math.Min(ChunkPayloadBytes, len - offset);
                        var slice = new byte[sliceLen];
                        if (sliceLen > 0)
                            Buffer.BlockCopy(payload, offset, slice, 0, sliceLen);

                        byte flags = 0;
                        if (i == 0) flags |= FlagMsgStart;
                        if (i == numChunks - 1) flags |= FlagMsgEnd;

                        tx.PendingChunks.Enqueue(new OutChunk
                        {
                            Seq = tx.NextSeq++,
                            MsgId = msgId,
                            Flags = flags,
                            PktType = (byte)type,
                            Data = slice
                        });
                    }

                    tx.UnackedMessages.Enqueue(new UnackedMessage
                    {
                        MsgId = msgId,
                        EndSeq = endSeq,
                        Channel = channel,
                        PktType = type,
                        Payload = payload
                    });
                }
            }

            if (channel == SendChannel.UnreliableSequenced && destEp != null)
            {
                SendUnreliableDatagram(destEp, targetPlayerId, unrelSeq, (byte)type, payload);
                return true;
            }

            _pumpWake.Set();
            return true;
        }

        public bool TryDequeueIncoming(out DirectIncomingPacket packet)
        {
            return _incomingQueue.TryDequeue(out packet);
        }

        public void RemovePeer(byte playerId)
        {
            lock (_stateLock)
            {
                _peers.Remove(playerId);
            }
        }

        private void StartSocketAndWorkers()
        {
            try
            {
                try
                {
                    _udpSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    _udpSocket.DualMode = true;
                    _udpSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                    _isDualMode = true;
                }
                catch
                {
                    _udpSocket?.Close();
                    _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    _udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    _isDualMode = false;
                }

                try { _udpSocket.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
                try
                {
                    _udpSocket.ReceiveBufferSize = 1 << 20;
                    _udpSocket.SendBufferSize = 1 << 20;
                }
                catch { }

                _localUdpPort = ((IPEndPoint)_udpSocket.LocalEndPoint).Port;

                lock (_stateLock)
                {
                    CollectLocalNetworkCandidates();
                }

                _recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "TABCoop_DirectRecv" };
                _recvThread.Start();

                _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "TABCoop_DirectPump" };
                _pumpThread.Start();

                ThreadPool.QueueUserWorkItem(_ => RunExternalNatDiscoveryAsync());

                ModLogger.Info($"[DirectFastPath] Initialized DualMode={_isDualMode} UDP port={_localUdpPort}, localCandidates={_localCandidates.Count}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[DirectFastPath] Failed to start UDP fast-path socket (Steam fallback stays active): {ex.Message}");
            }
        }

        private void CollectLocalNetworkCandidates()
        {
            _localCandidates.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    string nameDesc = ((ni.Name ?? string.Empty) + " " + (ni.Description ?? string.Empty)).ToLowerInvariant();
                    if (nameDesc.Contains("clash") || nameDesc.Contains("mihomo") || nameDesc.Contains("sing-box")
                        || nameDesc.Contains("v2ray") || nameDesc.Contains("nekoray") || nameDesc.Contains("meta"))
                        continue;

                    bool isVirtualLanNic =
                        nameDesc.Contains("tailscale") || nameDesc.Contains("zerotier") || nameDesc.Contains("radmin")
                        || nameDesc.Contains("hamachi") || nameDesc.Contains("wireguard") || nameDesc.Contains("oray")
                        || nameDesc.Contains("pugongying") || nameDesc.Contains("pgl") || nameDesc.Contains("wintun");

                    var ipProps = ni.GetIPProperties();
                    if (ipProps == null) continue;

                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        IPAddress addr = ua?.Address;
                        if (addr == null || IPAddress.IsLoopback(addr)) continue;

                        if (addr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] b = addr.GetAddressBytes();
                            if (b[0] == 169 && b[1] == 254) continue; // APIPA
                            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) continue; // Proxy benchmark TUN range

                            bool isCgnatVpn = b[0] == 100 && b[1] >= 64 && b[1] <= 127;
                            bool isRfc1918 = b[0] == 10
                                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                                || (b[0] == 192 && b[1] == 168);

                            DirectCandidateKind kind = (isVirtualLanNic || isCgnatVpn)
                                ? DirectCandidateKind.VirtualLan
                                : (isRfc1918 ? DirectCandidateKind.LanIPv4 : DirectCandidateKind.PublicIPv4);

                            if (seen.Add(addr.ToString()))
                                _localCandidates.Add(new DirectEndpointCandidate { Kind = kind, Address = addr, Port = _localUdpPort });
                        }
                        else if (addr.AddressFamily == AddressFamily.InterNetworkV6 && _isDualMode)
                        {
                            if (addr.IsIPv6LinkLocal || addr.IsIPv6Multicast || addr.IsIPv6SiteLocal) continue;
                            byte[] b = addr.GetAddressBytes();
                            // Skip Teredo (2001:0000::/32)
                            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) continue;

                            bool isGlobalV6 = (b[0] & 0xE0) == 0x20; // 2000::/3
                            bool isUla = (b[0] & 0xFE) == 0xFC;      // fc00::/7
                            if (!isGlobalV6 && !(isUla && isVirtualLanNic)) continue;

                            if (seen.Add(addr.ToString()))
                            {
                                _localCandidates.Add(new DirectEndpointCandidate
                                {
                                    Kind = isGlobalV6 ? DirectCandidateKind.PublicIPv6 : DirectCandidateKind.VirtualLan,
                                    Address = addr,
                                    Port = _localUdpPort
                                });
                            }
                        }
                    }
                }

                _localCandidates.Sort((a, b) => GetPriorityScore(a.Kind).CompareTo(GetPriorityScore(b.Kind)));
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[DirectFastPath] NIC enumeration warning: {ex.Message}");
            }
        }

        private void RunExternalNatDiscoveryAsync()
        {
            if (_disposed || _udpSocket == null) return;

            bool addedAny = false;
            try
            {
                // 1. Fire STUN requests in parallel from our exact bound UDP socket
                SendStunProbesToServers();

                // 2. Best-effort UPnP IGD discovery & port mapping (400ms timeout)
                if (TryUpnpMapUdpPort(_localUdpPort, out IPAddress extIp) && extIp != null)
                {
                    addedAny |= AddExternalCandidate(DirectCandidateKind.UpnpIPv4, extIp, _localUdpPort);
                }

                // Allow STUN responses up to 420ms to land cleanly before advertising update
                Thread.Sleep(420);
            }
            catch { }
            finally
            {
                _discoveryCompleted = true;
            }

            if (addedAny)
                NotifyLocalCandidatesUpdated();
        }

        private long _lastStunProbeTs;
        private IPAddress _lastStunIp;
        private int _lastStunPort;
        private long _lastStunPortChangeTs;
        private int _stunPortChurnCount;
        private volatile bool _symmetricNatDetected;
        private ulong _lastCandidateBroadcastHash;
        private long _lastCandidateBroadcastTs;
        private int _candidateBroadcastCount;

        private void SendStunProbesToServers(bool respectCooldown = false)
        {
            long nowTs = Stopwatch.GetTimestamp();
            if (respectCooldown && _lastStunProbeTs > 0)
            {
                double elapsedMs = (nowTs - _lastStunProbeTs) * 1000.0 / Stopwatch.Frequency;
                double minIntervalMs = _symmetricNatDetected ? 30000.0 : 8000.0;
                if (elapsedMs < minIntervalMs)
                    return;
            }
            _lastStunProbeTs = nowTs;

            var stunEndpoints = _cachedStunEndpoints;
            if (stunEndpoints == null || stunEndpoints.Length == 0)
            {
                var list = new List<IPEndPoint>();
                foreach (string hostPort in StunHosts)
                {
                    if (_disposed) return;
                    try
                    {
                        var parts = hostPort.Split(':');
                        int port = int.Parse(parts[1]);
                        IPAddress[] ips = Dns.GetHostAddresses(parts[0]);
                        foreach (var ip in ips)
                        {
                            if (ip.AddressFamily == AddressFamily.InterNetwork)
                            {
                                list.Add(new IPEndPoint(ip, port));
                                break;
                            }
                        }
                    }
                    catch { }
                }
                stunEndpoints = list.ToArray();
                _cachedStunEndpoints = stunEndpoints;
            }

            for (int i = 0; i < stunEndpoints.Length; i++)
            {
                SendStunBindingRequest(stunEndpoints[i]);
            }
        }

        /// <summary>
        /// Refreshes external STUN NAT bindings (useful when idle in CGNAT lobby or on new peer handshake).
        /// Rate-limited and guarded against Symmetric NAT multi-STUN port churn.
        /// </summary>
        public void RefreshExternalCandidatesAsync()
        {
            if (_disposed || _udpSocket == null) return;
            if (Interlocked.CompareExchange(ref _stunRefreshInFlight, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    SendStunProbesToServers(respectCooldown: true);
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _stunRefreshInFlight, 0);
                }
            });
        }

        public bool HasUnverifiedPeers()
        {
            lock (_stateLock)
            {
                foreach (var p in _peers.Values)
                {
                    if (!p.TxEnabled || !p.VerifiedBidirectional)
                        return true;
                }
                return false;
            }
        }

        private bool AddExternalCandidate(DirectCandidateKind kind, IPAddress ip, int port)
        {
            if (ip == null || IPAddress.IsLoopback(ip) || port <= 0 || port > 65535) return false;

            bool changed = false;
            bool logSymNat = false;
            int prevPort = 0;
            lock (_stateLock)
            {
                if (kind == DirectCandidateKind.StunIPv4)
                {
                    long nowTs = Stopwatch.GetTimestamp();
                    if (_lastStunIp != null && _lastStunIp.Equals(ip) && _lastStunPort != port)
                    {
                        double secSince = _lastStunPortChangeTs > 0
                            ? (nowTs - _lastStunPortChangeTs) / (double)Stopwatch.Frequency
                            : 0.0;
                        if (secSince <= 30.0)
                        {
                            _stunPortChurnCount++;
                            if (_stunPortChurnCount >= 2 && !_symmetricNatDetected)
                            {
                                _symmetricNatDetected = true;
                                logSymNat = true;
                                prevPort = _lastStunPort;
                            }
                        }
                        else
                        {
                            _stunPortChurnCount = 1;
                        }
                        _lastStunPortChangeTs = nowTs;
                    }
                    else if (_lastStunIp == null)
                    {
                        _lastStunIp = ip;
                        _lastStunPort = port;
                        _lastStunPortChangeTs = nowTs;
                    }

                    // If Symmetric NAT assigns a distinct external port per destination, replacing the prior
                    // StunIPv4 entry on every reply causes candidate storms across Steam. Keep stable entries.
                    if (_symmetricNatDetected)
                    {
                        bool alreadyHave = false;
                        for (int i = 0; i < _localCandidates.Count; i++)
                        {
                            if (_localCandidates[i].Kind == DirectCandidateKind.StunIPv4 && _localCandidates[i].Address.Equals(ip))
                            {
                                alreadyHave = true;
                                break;
                            }
                        }
                        if (alreadyHave)
                        {
                            if (logSymNat)
                                ModLogger.Info($"[DirectFastPath] Symmetric NAT port variance detected (IP={ip}, ports={prevPort}/{port}); suppressing STUN candidate churn.");
                            return false;
                        }
                    }

                    _lastStunIp = ip;
                    _lastStunPort = port;

                    // Replace stale StunIPv4 entry for same external IP if CGNAT reassigned external port.
                    for (int i = _localCandidates.Count - 1; i >= 0; i--)
                    {
                        var ex = _localCandidates[i];
                        if (ex.Kind == DirectCandidateKind.StunIPv4 && ex.Address.Equals(ip))
                        {
                            if (ex.Port == port) return false;
                            _localCandidates.RemoveAt(i);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < _localCandidates.Count; i++)
                    {
                        if (_localCandidates[i].Address.Equals(ip) && _localCandidates[i].Port == port)
                            return false;
                    }
                }
                _localCandidates.Add(new DirectEndpointCandidate { Kind = kind, Address = ip, Port = port });
                _localCandidates.Sort((a, b) => GetPriorityScore(a.Kind).CompareTo(GetPriorityScore(b.Kind)));
                changed = true;
            }
            return changed;
        }

        private void NotifyLocalCandidatesUpdated()
        {
            var snap = GetLocalCandidatesSnapshot();
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < snap.Count; i++)
            {
                var c = snap[i];
                hash ^= (ulong)c.Kind; hash *= 1099511628211UL;
                byte[] addr = c.Address.GetAddressBytes();
                for (int j = 0; j < addr.Length; j++) { hash ^= addr[j]; hash *= 1099511628211UL; }
                hash ^= (ulong)(uint)c.Port; hash *= 1099511628211UL;
            }

            long nowTs = Stopwatch.GetTimestamp();
            double sinceLastMs = _lastCandidateBroadcastTs > 0
                ? (nowTs - _lastCandidateBroadcastTs) * 1000.0 / Stopwatch.Frequency
                : 10000.0;
            if (hash == _lastCandidateBroadcastHash && sinceLastMs < 15000.0)
                return;
            if (_candidateBroadcastCount >= 2 && sinceLastMs < 1500.0)
                return;

            _lastCandidateBroadcastHash = hash;
            _lastCandidateBroadcastTs = nowTs;
            _candidateBroadcastCount++;
            ModLogger.Info($"[DirectFastPath] Updated external candidates ({snap.Count}): {string.Join(", ", snap)}");
            LocalCandidatesUpdated?.Invoke(snap, _sessionToken);
        }

        private void SendStunBindingRequest(IPEndPoint stunEp)
        {
            try
            {
                byte[] req = new byte[20];
                req[0] = 0x00; req[1] = 0x01; // Binding Request
                req[2] = 0x00; req[3] = 0x00;
                Buffer.BlockCopy(StunMagicCookie, 0, req, 4, 4);
                var txId = new byte[12];
                long key = Stopwatch.GetTimestamp() ^ stunEp.GetHashCode();
                byte[] keyBytes = BitConverter.GetBytes(key);
                Buffer.BlockCopy(keyBytes, 0, txId, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(stunEp.Address.GetHashCode()), 0, txId, 8, 4);
                Buffer.BlockCopy(txId, 0, req, 8, 12);

                lock (_stateLock)
                    _stunTransactions[key] = txId;

                var dest = ToSocketEndPoint(stunEp.Address, stunEp.Port);
                _udpSocket?.SendTo(req, dest);
            }
            catch { }
        }

        private void TryHandleStunResponse(byte[] buf, int len)
        {
            if (len < 20) return;
            // Binding Success Response (0x0101)
            if (buf[0] != 0x01 || buf[1] != 0x01) return;
            for (int i = 0; i < 4; i++)
                if (buf[4 + i] != StunMagicCookie[i]) return;

            int msgLen = (buf[2] << 8) | buf[3];
            if (20 + msgLen > len) return;

            int pos = 20;
            while (pos + 4 <= 20 + msgLen)
            {
                ushort attrType = (ushort)((buf[pos] << 8) | buf[pos + 1]);
                ushort attrLen = (ushort)((buf[pos + 2] << 8) | buf[pos + 3]);
                pos += 4;
                if (pos + attrLen > 20 + msgLen) break;

                if ((attrType == 0x0020 || attrType == 0x0001) && attrLen >= 8 && buf[pos + 1] == 0x01)
                {
                    int port;
                    byte[] ipBytes = new byte[4];
                    if (attrType == 0x0020) // XOR-MAPPED-ADDRESS
                    {
                        port = ((buf[pos + 2] << 8) | buf[pos + 3]) ^ 0x2112;
                        for (int j = 0; j < 4; j++)
                            ipBytes[j] = (byte)(buf[pos + 4 + j] ^ StunMagicCookie[j]);
                    }
                    else // MAPPED-ADDRESS
                    {
                        port = (buf[pos + 2] << 8) | buf[pos + 3];
                        Buffer.BlockCopy(buf, pos + 4, ipBytes, 0, 4);
                    }

                    var pubIp = new IPAddress(ipBytes);
                    if (AddExternalCandidate(DirectCandidateKind.StunIPv4, pubIp, port) && _discoveryCompleted)
                        NotifyLocalCandidatesUpdated();
                    return;
                }

                pos += (attrLen + 3) & ~3;
            }
        }

        private bool TryUpnpMapUdpPort(int port, out IPAddress externalIp)
        {
            externalIp = null;
            Socket ssdp = null;
            try
            {
                IPAddress lanIp = GetPrimaryLanIPv4();
                if (lanIp == null) return false;

                ssdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                ssdp.Bind(new IPEndPoint(lanIp, 0));
                ssdp.ReceiveTimeout = 350;

                string search =
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 1\r\n" +
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
                byte[] reqBytes = Encoding.ASCII.GetBytes(search);
                ssdp.SendTo(reqBytes, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

                byte[] respBuf = new byte[4096];
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n = ssdp.ReceiveFrom(respBuf, ref remote);
                string resp = Encoding.ASCII.GetString(respBuf, 0, n);

                string location = null;
                foreach (string line in resp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    {
                        location = line.Substring(9).Trim();
                        break;
                    }
                }
                if (string.IsNullOrEmpty(location)) return false;

                var locUri = new Uri(location);
                string descXml = HttpGetString(locUri, 500);
                if (string.IsNullOrEmpty(descXml)) return false;

                string serviceType = "urn:schemas-upnp-org:service:WANIPConnection:1";
                int stIdx = descXml.IndexOf("urn:schemas-upnp-org:service:WANIPConnection:", StringComparison.OrdinalIgnoreCase);
                if (stIdx < 0)
                {
                    stIdx = descXml.IndexOf("urn:schemas-upnp-org:service:WANPPPConnection:", StringComparison.OrdinalIgnoreCase);
                    if (stIdx >= 0) serviceType = "urn:schemas-upnp-org:service:WANPPPConnection:1";
                }
                if (stIdx < 0) return false;

                int endSt = descXml.IndexOf('<', stIdx);
                if (endSt > stIdx) serviceType = descXml.Substring(stIdx, endSt - stIdx).Trim();

                int ctrlStart = descXml.IndexOf("<controlURL>", stIdx, StringComparison.OrdinalIgnoreCase);
                if (ctrlStart < 0) return false;
                ctrlStart += "<controlURL>".Length;
                int ctrlEnd = descXml.IndexOf("</controlURL>", ctrlStart, StringComparison.OrdinalIgnoreCase);
                if (ctrlEnd <= ctrlStart) return false;

                string controlPath = descXml.Substring(ctrlStart, ctrlEnd - ctrlStart).Trim();
                var controlUri = new Uri(locUri, controlPath);

                string addBody =
                    "<?xml version=\"1.0\"?>" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    "<s:Body><u:AddPortMapping xmlns:u=\"" + serviceType + "\">" +
                    "<NewRemoteHost></NewRemoteHost>" +
                    "<NewExternalPort>" + port + "</NewExternalPort>" +
                    "<NewProtocol>UDP</NewProtocol>" +
                    "<NewInternalPort>" + port + "</NewInternalPort>" +
                    "<NewInternalClient>" + lanIp + "</NewInternalClient>" +
                    "<NewEnabled>1</NewEnabled>" +
                    "<NewPortMappingDescription>TABCoopMod</NewPortMappingDescription>" +
                    "<NewLeaseDuration>7200</NewLeaseDuration>" +
                    "</u:AddPortMapping></s:Body></s:Envelope>";

                if (!HttpSoapPost(controlUri, serviceType + "#AddPortMapping", addBody, 500, out _))
                    return false;

                string getIpBody =
                    "<?xml version=\"1.0\"?>" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    "<s:Body><u:GetExternalIPAddress xmlns:u=\"" + serviceType + "\"></u:GetExternalIPAddress></s:Body></s:Envelope>";

                if (!HttpSoapPost(controlUri, serviceType + "#GetExternalIPAddress", getIpBody, 500, out string ipResp))
                    return false;

                int ipStart = ipResp.IndexOf("<NewExternalIPAddress>", StringComparison.OrdinalIgnoreCase);
                if (ipStart < 0) return false;
                ipStart += "<NewExternalIPAddress>".Length;
                int ipEnd = ipResp.IndexOf("</NewExternalIPAddress>", ipStart, StringComparison.OrdinalIgnoreCase);
                if (ipEnd <= ipStart) return false;

                string ipStr = ipResp.Substring(ipStart, ipEnd - ipStart).Trim();
                if (!IPAddress.TryParse(ipStr, out externalIp)) return false;

                _upnpControlUrl = controlUri.ToString();
                _upnpServiceType = serviceType;
                _upnpMappedPort = port;
                ModLogger.Info($"[DirectFastPath] UPnP mapped UDP port {port} -> {externalIp}");
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { ssdp?.Close(); } catch { }
            }
        }

        private IPAddress GetPrimaryLanIPv4()
        {
            lock (_stateLock)
            {
                for (int i = 0; i < _localCandidates.Count; i++)
                {
                    if (_localCandidates[i].Kind == DirectCandidateKind.LanIPv4)
                        return _localCandidates[i].Address;
                }
            }
            return null;
        }

        private static string HttpGetString(Uri uri, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static bool HttpSoapPost(Uri uri, string soapAction, string body, int timeoutMs, out string responseText)
        {
            responseText = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(uri);
                req.Method = "POST";
                req.ContentType = "text/xml; charset=\"utf-8\"";
                req.Headers["SOAPACTION"] = "\"" + soapAction + "\"";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream())
                    s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    responseText = sr.ReadToEnd();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RecvLoop()
        {
            byte[] buf = new byte[4096];
            EndPoint remoteEp = _isDualMode
                ? (EndPoint)new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0);

            while (!_disposed && _udpSocket != null)
            {
                try
                {
                    int n = _udpSocket.ReceiveFrom(buf, ref remoteEp);
                    if (n < 15) continue;

                    // STUN responses have top two bits 00 and STUN magic cookie at [4..7]
                    if ((buf[0] & 0xC0) == 0 && n >= 20 && buf[4] == StunMagicCookie[0] && buf[5] == StunMagicCookie[1])
                    {
                        TryHandleStunResponse(buf, n);
                        continue;
                    }

                    if (buf[0] != Magic[0] || buf[1] != Magic[1] || buf[2] != Magic[2] || buf[3] != Magic[3])
                        continue;

                    ulong token = BitConverter.ToUInt64(buf, 4);
                    if (_sessionToken == 0 || token != _sessionToken) continue;

                    byte kind = buf[12];
                    byte senderId = buf[13];
                    byte targetId = buf[14];
                    if (targetId != 0 && _localPlayerId != 0 && targetId != _localPlayerId) continue;

                    var ipEp = remoteEp as IPEndPoint;
                    if (ipEp == null) continue;

                    HandleIncomingDatagram(ipEp, senderId, kind, buf, n);
                }
                catch (SocketException)
                {
                    if (_disposed) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                        ModLogger.Warn($"[DirectFastPath] RecvLoop error: {ex.Message}");
                }
            }
        }

        private void HandleIncomingDatagram(IPEndPoint remoteEp, byte senderId, byte kind, byte[] buf, int len)
        {
            long nowTs = Stopwatch.GetTimestamp();
            PeerDirectState peer;
            lock (_stateLock)
            {
                if (!_peers.TryGetValue(senderId, out peer))
                {
                    peer = new PeerDirectState { PlayerId = senderId };
                    _peers[senderId] = peer;
                }
            }

            if (kind == KindProbePing)
            {
                if (len < 28) return;
                // Reflect ProbePong immediately with identical payload tag
                byte[] pong = new byte[28];
                Buffer.BlockCopy(buf, 0, pong, 0, 28);
                pong[12] = KindProbePong;
                pong[13] = _localPlayerId;
                pong[14] = senderId;
                SendRaw(remoteEp, pong, 28);

                // Also ensure senderEp is tracked so simultaneous-open hole-punch learns peer-reflexive NAT ports!
                RegisterDynamicOrHeartbeatCandidate(peer, remoteEp, (DirectCandidateKind)buf[15], nowTs, isPong: false, echoTs: 0);
                return;
            }

            if (kind == KindProbePong)
            {
                if (len < 28) return;
                var ckind = (DirectCandidateKind)buf[15];
                long echoTs = BitConverter.ToInt64(buf, 16);
                RegisterDynamicOrHeartbeatCandidate(peer, remoteEp, ckind, nowTs, isPong: true, echoTs: echoTs);
                return;
            }

            bool needWake = false;
            lock (peer.Sync)
            {
                peer.LastAnyPacketRecvTs = nowTs;

                if (kind == KindUnreliable)
                {
                    if (len < 22) return;
                    uint seq = BitConverter.ToUInt32(buf, 15);
                    byte pktType = buf[19];
                    ushort plen = BitConverter.ToUInt16(buf, 20);
                    if (22 + plen > len) return;
                    if (seq <= peer.LastRecvUnrelSeq && (peer.LastRecvUnrelSeq - seq) < 100000U) return;
                    peer.LastRecvUnrelSeq = seq;

                    var payload = new byte[plen];
                    if (plen > 0) Buffer.BlockCopy(buf, 22, payload, 0, plen);
                    _incomingQueue.Enqueue(new DirectIncomingPacket
                    {
                        SenderId = senderId,
                        Type = (PacketType)pktType,
                        Payload = payload
                    });
                    return;
                }

                if (kind == KindAckOnly)
                {
                    if (len < 39) return;
                    uint bulkAck = BitConverter.ToUInt32(buf, 15);
                    ulong bulkSack = BitConverter.ToUInt64(buf, 19);
                    uint cmdAck = BitConverter.ToUInt32(buf, 27);
                    ulong cmdSack = BitConverter.ToUInt64(buf, 31);
                    bool progBulk = ApplyAckToTx(peer.BulkTx, bulkAck, bulkSack);
                    bool progCmd = ApplyAckToTx(peer.CmdTx, cmdAck, cmdSack);
                    if (progBulk || progCmd)
                    {
                        peer.LastDataProgressTs = nowTs;
                        if (peer.ConsecutiveDataStalls > 0 && peer.ActivatedTs > 0
                            && (nowTs - peer.ActivatedTs) * 1000.0 / Stopwatch.Frequency >= 20000.0)
                        {
                            peer.ConsecutiveDataStalls = 0;
                        }
                    }
                    needWake = true;
                }
                else if (kind == KindCommand || kind == KindBulk)
                {
                    // Reliable chunk header is 39 bytes + plen payload; allow 0..2 byte payloads (e.g. ClientReadyState=1B).
                    if (len < 39) return;
                    uint streamSeq = BitConverter.ToUInt32(buf, 15);
                    byte flags = buf[19];
                    uint msgId = BitConverter.ToUInt32(buf, 20);
                    byte pktType = buf[24];
                    uint peerAckBase = BitConverter.ToUInt32(buf, 25);
                    ulong peerSackMask = BitConverter.ToUInt64(buf, 29);
                    ushort plen = BitConverter.ToUInt16(buf, 37);
                    if (39 + plen > len) return;

                    // Process piggybacked ACK for our outbound stream on the matching channel
                    bool prog = kind == KindCommand
                        ? ApplyAckToTx(peer.CmdTx, peerAckBase, peerSackMask)
                        : ApplyAckToTx(peer.BulkTx, peerAckBase, peerSackMask);
                    if (prog)
                    {
                        peer.LastDataProgressTs = nowTs;
                        if (peer.ConsecutiveDataStalls > 0 && peer.ActivatedTs > 0
                            && (nowTs - peer.ActivatedTs) * 1000.0 / Stopwatch.Frequency >= 20000.0)
                        {
                            peer.ConsecutiveDataStalls = 0;
                        }
                    }

                    var rx = kind == KindCommand ? peer.CmdRx : peer.BulkRx;
                    var chunkBytes = new byte[plen];
                    if (plen > 0) Buffer.BlockCopy(buf, 39, chunkBytes, 0, plen);

                    ProcessRxChunk(senderId, rx, streamSeq, flags, msgId, pktType, chunkBytes, nowTs);
                    needWake = true;
                }
            }

            if (needWake) _pumpWake.Set();
        }

        private void RegisterDynamicOrHeartbeatCandidate(
            PeerDirectState peer, IPEndPoint remoteEp, DirectCandidateKind candKind, long nowTs, bool isPong, long echoTs)
        {
            IPAddress normAddr = remoteEp.Address;
            if (normAddr.IsIPv4MappedToIPv6) normAddr = normAddr.MapToIPv4();

            bool triggerCutover = false;
            lock (peer.Sync)
            {
                peer.LastAnyPacketRecvTs = nowTs;
                CandidateProbeState match = null;
                for (int i = 0; i < peer.Candidates.Count; i++)
                {
                    var cs = peer.Candidates[i];
                    if (cs.Candidate.Port == remoteEp.Port && cs.Candidate.Address.Equals(normAddr))
                    {
                        match = cs;
                        break;
                    }
                }

                if (match == null)
                {
                    var inferredKind = normAddr.AddressFamily == AddressFamily.InterNetworkV6
                        ? DirectCandidateKind.PublicIPv6
                        : candKind;
                    match = new CandidateProbeState
                    {
                        Candidate = new DirectEndpointCandidate { Kind = inferredKind, Address = normAddr, Port = remoteEp.Port },
                        SocketTarget = ToSocketEndPoint(normAddr, remoteEp.Port)
                    };
                    peer.Candidates.Add(match);
                }

                if (isPong)
                {
                    double rttMs = (nowTs - echoTs) * 1000.0 / Stopwatch.Frequency;
                    if (rttMs >= 0.0 && rttMs < 5000.0)
                    {
                        match.SmoothedRttMs = match.SmoothedRttMs < 0.0 ? rttMs : (match.SmoothedRttMs * 0.7 + rttMs * 0.3);
                    }
                    match.LastPongRecvTs = nowTs;
                    match.PongCount++;
                    peer.VerifiedBidirectional = true;

                    // Pick highest-priority / lowest-RTT active candidate
                    CandidateProbeState best = SelectBestCandidate(peer, nowTs);
                    if (best != null)
                        peer.ActiveCandidate = best;

                    if (!peer.TxEnabled && !peer.CutoverSignaled && !peer.CircuitBroken
                        && nowTs >= peer.FallbackCooldownUntilTs
                        && peer.ActiveCandidate != null && match.PongCount >= 1)
                    {
                        peer.CutoverSignaled = true;
                        triggerCutover = true;
                    }
                }
            }

            if (triggerCutover)
            {
                ModLogger.Info($"[DirectFastPath] Candidate verified for P{peer.PlayerId}: {peer.ActiveCandidate?.Candidate} (RTT={peer.ActiveCandidate?.SmoothedRttMs:F1}ms). Signaling Steam barrier cutover...");
                PeerDirectCutoverNeeded?.Invoke(peer.PlayerId, _isHost);
            }
        }

        private static CandidateProbeState SelectBestCandidate(PeerDirectState peer, long nowTs)
        {
            CandidateProbeState best = null;
            double bestScore = double.MaxValue;
            for (int i = 0; i < peer.Candidates.Count; i++)
            {
                var c = peer.Candidates[i];
                if (c.PongCount <= 0) continue;
                double ageMs = (nowTs - c.LastPongRecvTs) * 1000.0 / Stopwatch.Frequency;
                if (ageMs > DirectTimeoutMs) continue;

                double rtt = c.SmoothedRttMs >= 0 ? c.SmoothedRttMs : 50.0;
                double score = rtt + GetPriorityScore(c.Candidate.Kind) * 6.0;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best;
        }

        private void ProcessRxChunk(
            byte senderId, ChannelRxState rx, uint streamSeq, byte flags, uint msgId, byte pktType, byte[] data, long nowTs)
        {
            if (streamSeq < rx.ExpectedSeq)
            {
                rx.AckDirty = true;
                return;
            }

            if (streamSeq == rx.ExpectedSeq)
            {
                DeliverContiguousChunk(senderId, rx, flags, msgId, pktType, data);
                rx.ExpectedSeq++;
                rx.SackMask >>= 1;

                while (rx.Buffered.TryGetValue(rx.ExpectedSeq, out var queued))
                {
                    rx.Buffered.Remove(rx.ExpectedSeq);
                    DeliverContiguousChunk(senderId, rx, queued.Flags, queued.MsgId, queued.PktType, queued.Data);
                    rx.ExpectedSeq++;
                    rx.SackMask >>= 1;
                }
            }
            else
            {
                uint diff = streamSeq - rx.ExpectedSeq;
                if (diff < 64 && !rx.Buffered.ContainsKey(streamSeq))
                {
                    rx.Buffered[streamSeq] = new RxChunk
                    {
                        Flags = flags,
                        MsgId = msgId,
                        PktType = pktType,
                        Data = data
                    };
                    rx.SackMask |= (1UL << (int)(diff - 1));
                }
            }

            rx.AckDirty = true;
        }

        private void DeliverContiguousChunk(
            byte senderId, ChannelRxState rx, byte flags, uint msgId, byte pktType, byte[] data)
        {
            if ((flags & FlagMsgStart) != 0)
            {
                rx.AssemblingMsg.SetLength(0);
                rx.AssemblingPktType = pktType;
                rx.AssemblingMsgId = msgId;
                rx.IsAssembling = true;
            }

            if (rx.IsAssembling && data != null && data.Length > 0)
                rx.AssemblingMsg.Write(data, 0, data.Length);

            if ((flags & FlagMsgEnd) != 0 && rx.IsAssembling)
            {
                byte[] fullPayload = rx.AssemblingMsg.ToArray();
                rx.AssemblingMsg.SetLength(0);
                rx.IsAssembling = false;
                if (msgId > rx.LastDeliveredMsgId)
                    rx.LastDeliveredMsgId = msgId;

                if (!rx.DeliveredFallbackIds.Contains(msgId))
                {
                    _incomingQueue.Enqueue(new DirectIncomingPacket
                    {
                        SenderId = senderId,
                        Type = (PacketType)rx.AssemblingPktType,
                        Payload = fullPayload
                    });
                }
            }
        }

        private static bool ApplyAckToTx(ChannelTxState tx, uint ackBase, ulong sackMask)
        {
            bool progressed = false;
            if (ackBase > tx.AckBase)
            {
                tx.AckBase = ackBase;
                progressed = true;
            }

            if (tx.InFlight.Count > 0)
            {
                var acked = new List<uint>();
                foreach (var seq in tx.InFlight.Keys)
                {
                    if (seq <= tx.AckBase)
                    {
                        acked.Add(seq);
                    }
                    else
                    {
                        uint diff = seq - tx.AckBase;
                        if (diff <= 64 && (sackMask & (1UL << (int)(diff - 1))) != 0)
                            acked.Add(seq);
                    }
                }
                for (int i = 0; i < acked.Count; i++)
                    tx.InFlight.Remove(acked[i]);
                if (acked.Count > 0)
                    progressed = true;
            }

            while (tx.UnackedMessages.Count > 0 && tx.UnackedMessages.Peek().EndSeq <= tx.AckBase)
                tx.UnackedMessages.Dequeue();

            return progressed;
        }

        private void PumpLoop()
        {
            double swToMs = 1000.0 / Stopwatch.Frequency;
            var snapshotPeers = new List<PeerDirectState>(4);

            while (!_disposed)
            {
                _pumpWake.WaitOne(10);
                if (_disposed) break;

                long nowTs = Stopwatch.GetTimestamp();
                snapshotPeers.Clear();
                lock (_stateLock)
                {
                    foreach (var p in _peers.Values) snapshotPeers.Add(p);
                }

                for (int i = 0; i < snapshotPeers.Count; i++)
                {
                    var peer = snapshotPeers[i];
                    List<FallbackMessage> fallbackMsgs = null;
                    byte fallbackPeerId = 0;

                    lock (peer.Sync)
                    {
                        // 1. Send discovery/keepalive probes
                        if (_sessionToken != 0 && !peer.CircuitBroken && peer.Candidates.Count > 0)
                        {
                            double interval = peer.TxEnabled ? KeepaliveHeartbeatMs : DiscoveryProbeIntervalMs;
                            for (int c = 0; c < peer.Candidates.Count; c++)
                            {
                                var cs = peer.Candidates[c];
                                // Once direct is up, only heartbeat verified/active candidates every 1s
                                if (peer.TxEnabled && cs != peer.ActiveCandidate && cs.PongCount == 0)
                                    continue;

                                double sincePing = cs.LastPingSentTs > 0 ? (nowTs - cs.LastPingSentTs) * swToMs : interval + 1.0;
                                if (sincePing >= interval)
                                {
                                    cs.LastPingSentTs = nowTs;
                                    SendProbePing(cs, peer.PlayerId, nowTs);
                                }
                            }
                        }

                        // 2. Check liveness & reliable data-channel stall timeout -> seamless lossless Steam fallback
                        if (peer.TxEnabled && peer.LastAnyPacketRecvTs > 0)
                        {
                            bool hasInFlightData = peer.CmdTx.InFlight.Count > 0 || peer.BulkTx.InFlight.Count > 0;
                            if (!hasInFlightData)
                                peer.LastDataProgressTs = nowTs;

                            double silentMs = (nowTs - peer.LastAnyPacketRecvTs) * swToMs;
                            double dataStallMs = (hasInFlightData && peer.LastDataProgressTs > 0)
                                ? (nowTs - peer.LastDataProgressTs) * swToMs
                                : 0.0;
                            bool onlyBulkStalled = hasInFlightData && peer.CmdTx.InFlight.Count == 0 && silentMs < 2500.0;
                            double effectiveDataStallMs = onlyBulkStalled ? DirectBulkStallFallbackMs : DirectDataStallFallbackMs;

                            if (silentMs >= DirectTimeoutMs || dataStallMs >= effectiveDataStallMs)
                            {
                                string reason = silentMs >= DirectTimeoutMs
                                    ? $"path timeout ({silentMs:F0}ms silent)"
                                    : $"data channel stalled ({dataStallMs:F0}ms unacked, cmdFly={peer.CmdTx.InFlight.Count}, bulkFly={peer.BulkTx.InFlight.Count})";
                                peer.ConsecutiveDataStalls++;
                                if (peer.ConsecutiveDataStalls >= 3)
                                {
                                    peer.CircuitBroken = true;
                                    ModLogger.Warn($"[DirectFastPath] Direct {reason} for P{peer.PlayerId} (#{peer.ConsecutiveDataStalls}); tripping circuit-breaker for this session (staying on Steam P2P).");
                                }
                                else
                                {
                                    double cooldownSec = peer.ConsecutiveDataStalls == 1 ? 10.0 : 30.0;
                                    peer.FallbackCooldownUntilTs = nowTs + (long)(cooldownSec * Stopwatch.Frequency);
                                    ModLogger.Warn($"[DirectFastPath] Direct {reason} for P{peer.PlayerId} (#{peer.ConsecutiveDataStalls}); falling back cleanly to Steam P2P (cooldown {cooldownSec:F0}s).");
                                }
                                peer.TxEnabled = false;
                                peer.CutoverSignaled = false;
                                peer.CutoverAckedByPeer = false;
                                peer.VerifiedBidirectional = false;
                                peer.ActiveCandidate = null;

                                fallbackMsgs = DrainUnackedMessagesForFallback(peer);
                                fallbackPeerId = peer.PlayerId;
                            }
                        }

                        // 3. Transmit/retransmit ReliableCommand first, then ReliableBulk, then ACKs
                        if (peer.TxEnabled && peer.ActiveCandidate != null)
                        {
                            var dest = peer.ActiveCandidate.SocketTarget;
                            double rtt = peer.ActiveCandidate.SmoothedRttMs > 0 ? peer.ActiveCandidate.SmoothedRttMs : 40.0;
                            double rtoMs = Math.Max(45.0, Math.Min(450.0, rtt * 1.6 + 15.0));

                            PumpReliableChannel(peer, peer.CmdTx, KindCommand, dest, CommandWindowChunks, nowTs, swToMs, rtoMs);
                            PumpReliableChannel(peer, peer.BulkTx, KindBulk, dest, BulkWindowChunks, nowTs, swToMs, rtoMs);

                            if (peer.CmdRx.AckDirty || peer.BulkRx.AckDirty)
                            {
                                SendAckOnlyDatagram(dest, peer);
                                peer.CmdRx.AckDirty = false;
                                peer.BulkRx.AckDirty = false;
                            }
                        }
                    }

                    if (fallbackMsgs != null && fallbackMsgs.Count > 0)
                        PeerFallbackTriggered?.Invoke(fallbackPeerId, fallbackMsgs);
                }
            }
        }

        private List<FallbackMessage> DrainUnackedMessagesForFallback(PeerDirectState peer)
        {
            var list = new List<FallbackMessage>();
            while (peer.CmdTx.UnackedMessages.Count > 0)
            {
                var m = peer.CmdTx.UnackedMessages.Dequeue();
                list.Add(new FallbackMessage { MsgId = m.MsgId, Channel = m.Channel, Type = m.PktType, Payload = m.Payload });
            }
            while (peer.BulkTx.UnackedMessages.Count > 0)
            {
                var m = peer.BulkTx.UnackedMessages.Dequeue();
                list.Add(new FallbackMessage { MsgId = m.MsgId, Channel = m.Channel, Type = m.PktType, Payload = m.Payload });
            }
            peer.CmdTx.InFlight.Clear();
            peer.CmdTx.PendingChunks.Clear();
            peer.CmdTx.AckBase = peer.CmdTx.NextSeq > 0 ? peer.CmdTx.NextSeq - 1 : 0;
            peer.BulkTx.InFlight.Clear();
            peer.BulkTx.PendingChunks.Clear();
            peer.BulkTx.AckBase = peer.BulkTx.NextSeq > 0 ? peer.BulkTx.NextSeq - 1 : 0;
            return list;
        }

        private void PumpReliableChannel(
            PeerDirectState peer, ChannelTxState tx, byte kind, IPEndPoint dest, int maxWindow,
            long nowTs, double swToMs, double rtoMs)
        {
            // Retransmit timed-out chunks first
            if (tx.InFlight.Count > 0)
            {
                foreach (var ch in tx.InFlight.Values)
                {
                    double ageMs = (nowTs - ch.LastSentTs) * swToMs;
                    double chunkRto = Math.Min(800.0, rtoMs * Math.Pow(1.4, Math.Min(4, ch.Retransmits)));
                    if (ageMs >= chunkRto)
                    {
                        ch.LastSentTs = nowTs;
                        ch.Retransmits++;
                        SendReliableChunkDatagram(dest, peer, kind, ch);
                    }
                }
            }

            // Send fresh chunks up to window limit
            while (tx.PendingChunks.Count > 0 && tx.InFlight.Count < maxWindow)
            {
                var ch = tx.PendingChunks.Dequeue();
                ch.LastSentTs = nowTs;
                tx.InFlight[ch.Seq] = ch;
                SendReliableChunkDatagram(dest, peer, kind, ch);
            }
        }

        private void SendProbePing(CandidateProbeState cs, byte targetPlayerId, long nowTs)
        {
            byte[] pkt = new byte[28];
            WriteCommonHeader(pkt, KindProbePing, targetPlayerId);
            pkt[15] = (byte)cs.Candidate.Kind;
            Buffer.BlockCopy(BitConverter.GetBytes(nowTs), 0, pkt, 16, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(++cs.ProbeSeq), 0, pkt, 24, 4);
            SendRaw(cs.SocketTarget, pkt, 28);
        }

        private void SendUnreliableDatagram(IPEndPoint dest, byte targetPlayerId, uint seq, byte pktType, byte[] payload)
        {
            int plen = payload?.Length ?? 0;
            byte[] pkt = new byte[22 + plen];
            WriteCommonHeader(pkt, KindUnreliable, targetPlayerId);
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, pkt, 15, 4);
            pkt[19] = pktType;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)plen), 0, pkt, 20, 2);
            if (plen > 0) Buffer.BlockCopy(payload, 0, pkt, 22, plen);
            SendRaw(dest, pkt, pkt.Length);
        }

        private void SendAckOnlyDatagram(IPEndPoint dest, PeerDirectState peer)
        {
            byte[] pkt = new byte[39];
            WriteCommonHeader(pkt, KindAckOnly, peer.PlayerId);
            Buffer.BlockCopy(BitConverter.GetBytes(peer.BulkRx.ExpectedSeq - 1), 0, pkt, 15, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(peer.BulkRx.SackMask), 0, pkt, 19, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(peer.CmdRx.ExpectedSeq - 1), 0, pkt, 27, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(peer.CmdRx.SackMask), 0, pkt, 31, 8);
            SendRaw(dest, pkt, 39);
        }

        private void SendReliableChunkDatagram(IPEndPoint dest, PeerDirectState peer, byte kind, OutChunk ch)
        {
            var rx = kind == KindCommand ? peer.CmdRx : peer.BulkRx;
            rx.AckDirty = false;
            int plen = ch.Data?.Length ?? 0;
            byte[] pkt = new byte[39 + plen];
            WriteCommonHeader(pkt, kind, peer.PlayerId);
            Buffer.BlockCopy(BitConverter.GetBytes(ch.Seq), 0, pkt, 15, 4);
            pkt[19] = ch.Flags;
            Buffer.BlockCopy(BitConverter.GetBytes(ch.MsgId), 0, pkt, 20, 4);
            pkt[24] = ch.PktType;
            Buffer.BlockCopy(BitConverter.GetBytes(rx.ExpectedSeq - 1), 0, pkt, 25, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(rx.SackMask), 0, pkt, 29, 8);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)plen), 0, pkt, 37, 2);
            if (plen > 0) Buffer.BlockCopy(ch.Data, 0, pkt, 39, plen);
            SendRaw(dest, pkt, pkt.Length);
        }

        private void WriteCommonHeader(byte[] pkt, byte kind, byte targetPlayerId)
        {
            pkt[0] = Magic[0]; pkt[1] = Magic[1]; pkt[2] = Magic[2]; pkt[3] = Magic[3];
            Buffer.BlockCopy(BitConverter.GetBytes(_sessionToken), 0, pkt, 4, 8);
            pkt[12] = kind;
            pkt[13] = _localPlayerId;
            pkt[14] = targetPlayerId;
        }

        private void SendRaw(IPEndPoint dest, byte[] data, int length)
        {
            try
            {
                _udpSocket?.SendTo(data, 0, length, SocketFlags.None, dest);
            }
            catch { }
        }

        private IPEndPoint ToSocketEndPoint(IPAddress ip, int port)
        {
            if (_isDualMode && ip.AddressFamily == AddressFamily.InterNetwork)
                return new IPEndPoint(ip.MapToIPv6(), port);
            return new IPEndPoint(ip, port);
        }

        private static int GetPriorityScore(DirectCandidateKind kind)
        {
            switch (kind)
            {
                case DirectCandidateKind.LanIPv4: return 0;
                case DirectCandidateKind.VirtualLan: return 1;
                case DirectCandidateKind.PublicIPv6: return 2;
                case DirectCandidateKind.PublicIPv4: return 3;
                case DirectCandidateKind.UpnpIPv4: return 4;
                case DirectCandidateKind.StunIPv4: return 5;
                default: return 10;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _pumpWake.Set(); } catch { }

            if (!string.IsNullOrEmpty(_upnpControlUrl) && _upnpMappedPort > 0)
            {
                int port = _upnpMappedPort;
                string url = _upnpControlUrl;
                string svc = _upnpServiceType;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        string delBody =
                            "<?xml version=\"1.0\"?>" +
                            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                            "<s:Body><u:DeletePortMapping xmlns:u=\"" + svc + "\">" +
                            "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>UDP</NewProtocol>" +
                            "</u:DeletePortMapping></s:Body></s:Envelope>";
                        HttpSoapPost(new Uri(url), svc + "#DeletePortMapping", delBody, 300, out string _);
                    }
                    catch { }
                });
            }

            try { _udpSocket?.Close(); } catch { }
            _udpSocket = null;
            try { _pumpWake.Dispose(); } catch { }
        }
    }
}
