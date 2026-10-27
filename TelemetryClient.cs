using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using GorillaNetworking;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.Networking;
using WebSocketSharp;

namespace NoLeaves
{
    public class TelemetryClient : MonoBehaviour
    {
        private sealed class Writer
        {
            private readonly List<byte> buffer = new List<byte>(32);
            public byte[] Bytes => buffer.ToArray();
            public void Write(byte b) => buffer.Add(b);
            public void AppendBytes(byte[] bytes) => buffer.AddRange(bytes);
            public void WriteVarint(int value)
            {
                uint num;
                for (num = (uint)value; num >= 128; num >>= 7)
                    buffer.Add((byte)(num | 0x80));
                buffer.Add((byte)num);
            }
        }

        private const string EndpointHost = "api-prod-iidk-de.corgi.st";
        public static string CurrentHost = EndpointHost;
        public static string WireEndpoint = "wss://" + EndpointHost + "/v2/ws";
        public static string ConfigEndpoint = "https://" + EndpointHost + "/v2/cfg";
        public const string MetadataApiUrl = "https://github.com/iimenu/manifest/raw/refs/heads/main/api.json";

        private const float FlushInterval = 0.25f;
        private const float RoomStateDelay = 3f;
        private const float RigWaitTimeout = 10f;
        private const float CosmeticWaitTimeout = 5f;
        private const int MaxQueued = 256;
        private const float FallbackPollMin = 60f;
        private const float FallbackPollMax = 180f;
        private const float CfgSafetyNet = 900f;
        private const int FrameMax = 4096;

        private static TelemetryClient instance;
        private static readonly Queue<byte[]> Pending = new Queue<byte[]>();
        private static bool inRoom;
        private static string currentRoom;
        private static string currentRegion;
        private static bool currentRoomVisible;
        private static readonly Dictionary<NetPlayer, ushort> Sids = new Dictionary<NetPlayer, ushort>();
        private static ushort nextSid;
        private static float nextFlushTime;

        private static readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        private static readonly ConcurrentQueue<byte[]> sendQueue = new ConcurrentQueue<byte[]>();

        private static Thread senderThread;
        private static volatile bool senderDraining;
        private static volatile WebSocket ws;

        private static bool wsHelloed;
        private static bool wsExpectingConfig;
        private static float wsExpectingConfigAt;
        private static float wsConnectingSince;
        private static float wsAckDeadline;
        private static float heartbeatInterval = 10f;
        private static float nextHeartbeatAt;
        private static float lastServerContact;
        private static float nextWsAttemptAt;
        private static float reconnectDelay;
        private static int connectFailures;
        private static bool evicted;
        private static int protocolKicks;
        private static bool killActive;
        private static bool primaryDown;
        private static bool bootstrapDone;
        private static bool shuttingDown;
        private static bool suppressReconnectOnce;
        private static float nextCfgPollAt;
        private static float nextFallbackPollAt;
        private static float nextSafetyNetAt;
        private static int heldRev;
        private static string selfUid;
        private static List<string> apiDomains = new List<string>();
        private static float nextApiRotateAt;
        private static int safetyNetTicks;
        private static byte[] installId;
        public static bool InstallIdFromMachine;

        private const string InstallIdSalt = "ii-Reborn-install-v2";
        private const uint RRF_RT_REG_SZ = 2u;
        private static readonly UIntPtr HKeyLocalMachine = (UIntPtr)2147483650u;
        private const string RoomAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-~";

        private static bool WsOpen => ws != null && ws.ReadyState == WebSocketState.Open;
        private static bool WsBusy => ws != null && (ws.ReadyState == WebSocketState.Connecting || ws.ReadyState == WebSocketState.Open);

        public static void Attach()
        {
            GameObject go = GameObject.Find("iiMenu_Telemetry") ?? new GameObject("iiMenu_Telemetry");
            DontDestroyOnLoad(go);
            if (go.GetComponent("TelemetryClient") == null)
                go.AddComponent<TelemetryClient>();
        }

        public static byte[] GetInstallId()
        {
            if (installId != null) return installId;
            string text = ReadMachineGuid();
            if (!string.IsNullOrEmpty(text))
            {
                using HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(InstallIdSalt));
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
                installId = new byte[16];
                Buffer.BlockCopy(hash, 0, installId, 0, 16);
                InstallIdFromMachine = true;
                return installId;
            }
            string path = Path.Combine(Application.persistentDataPath, "InstallId.txt");
            string hex = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            if (string.IsNullOrEmpty(hex) || !Regex.IsMatch(hex, "^[0-9a-fA-F]{32}$"))
            {
                hex = Guid.NewGuid().ToString("N");
                try { File.WriteAllText(path, hex); } catch { }
            }
            installId = new byte[16];
            for (int i = 0; i < 16; i++)
                installId[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            InstallIdFromMachine = false;
            return installId;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegGetValueW(UIntPtr hive, string subKey, string value, uint flags, uint type, byte[] data, ref uint size);

        private static string ReadMachineGuid()
        {
            try
            {
                byte[] array = new byte[128];
                uint size = (uint)array.Length;
                int num = RegGetValueW(HKeyLocalMachine, "SOFTWARE\\Microsoft\\Cryptography", "MachineGuid", RRF_RT_REG_SZ, 0u, array, ref size);
                if (num != 0) return null;
                return Encoding.Unicode.GetString(array, 0, (int)size).TrimEnd('\0');
            }
            catch
            {
                return null;
            }
        }

        private void Awake()
        {
            instance = this;
            shuttingDown = false;
            senderDraining = false;
            if (senderThread == null || !senderThread.IsAlive)
            {
                senderThread = new Thread(SenderLoop) { IsBackground = true };
                senderThread.Start();
            }
            evicted = false;
            protocolKicks = 0;
            connectFailures = 0;
            reconnectDelay = 0f;
            wsHelloed = false;
            wsExpectingConfig = false;
            Application.quitting += OnApplicationQuitting;
            NetworkSystem.Instance.OnJoinedRoomEvent += new Action(OnJoinRoom);
            NetworkSystem.Instance.OnReturnedToSinglePlayer += new Action(OnLeaveRoom);
            NetworkSystem.Instance.OnPlayerJoined += new Action<NetPlayer>(OnPlayerJoin);
            NetworkSystem.Instance.OnPlayerLeft += new Action<NetPlayer>(OnPlayerLeave);
        }

        private void Start() => StartCoroutine(Bootstrap());

        private void OnApplicationQuitting() => Shutdown();

        private void OnDestroy()
        {
            Application.quitting -= OnApplicationQuitting;
            Shutdown();
            if (NetworkSystem.Instance != null)
            {
                NetworkSystem.Instance.OnJoinedRoomEvent -= new Action(OnJoinRoom);
                NetworkSystem.Instance.OnReturnedToSinglePlayer -= new Action(OnLeaveRoom);
                NetworkSystem.Instance.OnPlayerJoined -= new Action<NetPlayer>(OnPlayerJoin);
                NetworkSystem.Instance.OnPlayerLeft -= new Action<NetPlayer>(OnPlayerLeave);
            }
        }

        private void Update()
        {
            while (mainThread.TryDequeue(out Action action))
            {
                try { action(); } catch { }
            }
            float now = Time.unscaledTime;
            if (killActive && now > nextCfgPollAt)
            {
                nextCfgPollAt = now + 60f;
                StartCoroutine(FetchConfig());
            }
            else if (bootstrapDone && !killActive && !primaryDown && !WsOpen && now > nextCfgPollAt)
            {
                nextCfgPollAt = now + UnityEngine.Random.Range(FallbackPollMin, FallbackPollMax);
                StartCoroutine(FetchConfig());
            }
            if (primaryDown && now > nextFallbackPollAt)
            {
                nextFallbackPollAt = now + UnityEngine.Random.Range(FallbackPollMin, FallbackPollMax);
                StartCoroutine(FallbackPoll());
            }
            if (bootstrapDone && !killActive && !evicted && !shuttingDown && !primaryDown && !WsBusy && now > nextWsAttemptAt)
                ConnectWs();
            if (apiDomains.Count > 1 && primaryDown && now > nextApiRotateAt)
            {
                nextApiRotateAt = now + UnityEngine.Random.Range(FallbackPollMin, FallbackPollMax);
                int idx = apiDomains.IndexOf(CurrentHost);
                if (idx >= 0 && idx < apiDomains.Count - 1)
                    SetEndpointHost(apiDomains[idx + 1]);
            }
            if (ws != null && ws.ReadyState == WebSocketState.Connecting && now > wsConnectingSince + 15f)
            {
                try { ws.Close(); } catch { }
                ScheduleReconnect();
            }
            if (wsHelloed && now > nextHeartbeatAt)
            {
                nextHeartbeatAt = now + heartbeatInterval;
                TrySend(Frame(2, Array.Empty<byte>()));
            }
            if (ws != null && ws.ReadyState == WebSocketState.Open && !wsHelloed && wsAckDeadline > 0f && now > wsAckDeadline)
            {
                try { ws.Close(1000, "no ack"); } catch { }
            }
            if (wsHelloed && wsExpectingConfig && now > wsExpectingConfigAt)
            {
                wsExpectingConfig = false;
                StartCoroutine(FetchConfig(true));
            }
            if (wsHelloed && now - lastServerContact > heartbeatInterval * 2f + 2f)
            {
                try { ws?.Close(1000, "starved"); } catch { }
            }
            if (wsHelloed && now > nextSafetyNetAt)
            {
                nextSafetyNetAt = now + CfgSafetyNet;
                safetyNetTicks++;
                StartCoroutine(FetchConfig(false));
                if ((safetyNetTicks & 3) == 3)
                    StartCoroutine(FallbackPoll());
            }
            if (Pending.Count > 0 && now >= nextFlushTime)
            {
                nextFlushTime = now + FlushInterval;
                Flush();
            }
        }

        public static void Shutdown()
        {
            if (shuttingDown) return;
            shuttingDown = true;
            if (inRoom)
            {
                Enqueue(Frame(17, new byte[1] { 3 }));
                inRoom = false;
                Sids.Clear();
            }
            Flush();
            senderDraining = true;
            try { senderThread?.Join(500); } catch { }
            try { ws?.Close(1001, "quit"); } catch { }
        }

        private static IEnumerator Bootstrap()
        {
            yield return FetchConfig();
            bootstrapDone = true;
            nextCfgPollAt = Time.unscaledTime + UnityEngine.Random.Range(FallbackPollMin, FallbackPollMax);
            if (primaryDown)
                nextWsAttemptAt = Time.unscaledTime + UnityEngine.Random.Range(FallbackPollMin, FallbackPollMax);
            else if (!killActive)
                ConnectWs();
        }

        private static IEnumerator FetchConfig(bool full = false)
        {
            string url = (!full && heldRev > 0) ? $"{ConfigEndpoint}?rev={heldRev}" : (ConfigEndpoint + "?rev=0");
            using UnityWebRequest request = UnityWebRequest.Get(url);
            request.timeout = 10;
            yield return request.SendWebRequest();
            bool reachable = request.result == UnityWebRequest.Result.Success && (request.responseCode == 200 || request.responseCode == 204);
            if (!reachable)
            {
                if (!WsOpen) primaryDown = true;
                yield break;
            }
            primaryDown = false;
            if (request.responseCode == 204) yield break;
            string body = request.downloadHandler.text ?? "";
            Match revMatch = Regex.Match(body, "\"rev\"\\s*:\\s*(\\d+)");
            if (revMatch.Success && int.TryParse(revMatch.Groups[1].Value, out int r) && r > heldRev)
                heldRev = r;
        }

        private static IEnumerator FallbackPoll()
        {
            using UnityWebRequest apiRequest = UnityWebRequest.Get(MetadataApiUrl);
            apiRequest.timeout = 10;
            yield return apiRequest.SendWebRequest();
            if (apiRequest.result != UnityWebRequest.Result.Success) yield break;
            try
            {
                string json = apiRequest.downloadHandler.text ?? "";
                Match domainsMatch = Regex.Match(json, "\"domains\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                if (domainsMatch.Success)
                {
                    List<string> domains = new List<string>();
                    foreach (Match m in Regex.Matches(domainsMatch.Groups[1].Value, "\"([^\"]+)\""))
                    {
                        if (domains.Count >= 8) break;
                        string d = m.Groups[1].Value;
                        if (!string.IsNullOrEmpty(d) && Regex.IsMatch(d, "^[A-Za-z0-9.-]{1,253}$"))
                            domains.Add(d);
                    }
                    if (domains.Count > 0)
                    {
                        apiDomains = domains;
                        SetEndpointHost(apiDomains[0]);
                        nextApiRotateAt = 0f;
                    }
                }
            }
            catch { }
        }

        private static void SetEndpointHost(string host)
        {
            if (host == CurrentHost) return;
            CurrentHost = host;
            WireEndpoint = "wss://" + host + "/v2/ws";
            ConfigEndpoint = "https://" + host + "/v2/cfg";
            heldRev = 0;
            suppressReconnectOnce = true;
            try { ws?.Close(1000, "hostchange"); } catch { }
        }

        private static void ConnectWs()
        {
            if (shuttingDown || evicted || WsBusy) return;
            while (sendQueue.TryDequeue(out _)) { }
            WebSocket socket = new WebSocket(WireEndpoint);
            ws = socket;
            wsConnectingSince = Time.unscaledTime;
            wsAckDeadline = 0f;
            wsHelloed = false;
            wsExpectingConfig = false;
            socket.OnOpen += (sender, args) => mainThread.Enqueue(() =>
            {
                connectFailures = 0;
                reconnectDelay = 0f;
                primaryDown = false;
                wsAckDeadline = Time.unscaledTime + 10f;
                TrySend(BuildHello());
            });
            socket.OnMessage += (sender, args) => mainThread.Enqueue(() => HandleServerData(args.RawData));
            socket.OnClose += (sender, args) => mainThread.Enqueue(() => HandleWsClosed(args.Code));
            socket.OnError += (sender, args) => mainThread.Enqueue(() =>
            {
                if (ws == socket && socket.ReadyState == WebSocketState.Closed)
                    ScheduleReconnect();
            });
            try
            {
                socket.ConnectAsync();
            }
            catch
            {
                connectFailures++;
                ScheduleReconnect();
            }
        }

        private static bool TrySend(byte[] frame)
        {
            if (!WsOpen) return false;
            sendQueue.Enqueue(frame);
            return true;
        }

        private static void SenderLoop()
        {
            while (true)
            {
                if (!sendQueue.TryDequeue(out byte[] result))
                {
                    if (senderDraining) break;
                    Thread.Sleep(25);
                    continue;
                }
                try
                {
                    WebSocket webSocket = ws;
                    if (webSocket != null && webSocket.ReadyState == WebSocketState.Open)
                        webSocket.Send(result);
                }
                catch { }
            }
        }

        private static void HandleWsClosed(ushort code)
        {
            bool wasHelloed = wsHelloed;
            wsHelloed = false;
            wsExpectingConfig = false;
            if (shuttingDown) return;
            if (suppressReconnectOnce)
            {
                suppressReconnectOnce = false;
                return;
            }
            if (!wasHelloed) connectFailures++;
            ScheduleReconnect();
            if (!killActive && wasHelloed)
                instance.StartCoroutine(FetchConfig());
        }

        private static void ScheduleReconnect()
        {
            float delay;
            if (connectFailures <= 2)
            {
                reconnectDelay = (reconnectDelay <= 0f) ? 1f : Mathf.Min(reconnectDelay * 2f, 60f);
                delay = reconnectDelay;
            }
            else
            {
                delay = UnityEngine.Random.Range(FallbackPollMin, FallbackPollMax);
                reconnectDelay = 60f;
            }
            nextWsAttemptAt = Time.unscaledTime + delay * UnityEngine.Random.Range(0.7f, 1.3f);
        }

        private static void HandleServerData(byte[] data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                byte type = data[offset++];
                if (!TryReadVarint(data, ref offset, out int value) || offset + value > data.Length)
                    break;
                byte[] array = new byte[value];
                Buffer.BlockCopy(data, offset, array, 0, value);
                offset += value;
                HandleServerFrame(type, array);
            }
        }

        private static void HandleServerFrame(byte type, byte[] payload)
        {
            switch (type)
            {
                case 129:
                    if (payload.Length >= 14)
                    {
                        protocolKicks = 0;
                        connectFailures = 0;
                        wsHelloed = true;
                        lastServerContact = Time.unscaledTime;
                        heartbeatInterval = Mathf.Clamp(payload[12] | (payload[13] << 8), 5, 60);
                        nextHeartbeatAt = Time.unscaledTime + heartbeatInterval;
                        nextSafetyNetAt = Time.unscaledTime + CfgSafetyNet;
                        wsExpectingConfig = true;
                        wsExpectingConfigAt = Time.unscaledTime + 2f;
                        if (inRoom && PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null)
                        {
                            currentRoom = PhotonNetwork.CurrentRoom.Name;
                            currentRegion = PhotonNetwork.CloudRegion;
                            currentRoomVisible = PhotonNetwork.CurrentRoom.IsVisible;
                            Enqueue(Frame(16, EncodeRoomEnter()));
                            instance.StartCoroutine(RoomStateAfterRigsSettle());
                        }
                    }
                    break;
                case 130:
                    lastServerContact = Time.unscaledTime;
                    break;
                case 131:
                    wsExpectingConfig = false;
                    break;
                case 132:
                    if (payload.Length >= 2)
                        HandleKick(payload[0], payload[1]);
                    break;
            }
        }

        private static void HandleKick(byte reason, byte backoff)
        {
            suppressReconnectOnce = true;
            switch (reason)
            {
                case 0:
                    killActive = true;
                    nextCfgPollAt = Time.unscaledTime + 60f;
                    break;
                case 1:
                    evicted = true;
                    break;
                case 2:
                    nextWsAttemptAt = Time.unscaledTime + Mathf.Max(1f, (int)backoff);
                    break;
                case 3:
                    protocolKicks++;
                    if (protocolKicks >= 3) evicted = true;
                    else nextWsAttemptAt = Time.unscaledTime + Mathf.Max(1f, (int)backoff);
                    break;
                case 4:
                    nextWsAttemptAt = Time.unscaledTime + Mathf.Max(30f, (int)backoff);
                    break;
            }
        }

        private static byte[] BuildHello()
        {
            Writer writer = new Writer();
            writer.Write(3);
            writer.AppendBytes(GetInstallId());
            writer.Write((byte)(InstallIdFromMachine ? 2 : 0));
            WriteString(writer, Cap("1.2.1", 32));
            WriteString(writer, SelfUid());
            return Frame(1, writer.Bytes);
        }

        private static bool TryReadVarint(byte[] buffer, ref int offset, out int value)
        {
            long num = 0L;
            int shift = 0;
            while (offset < buffer.Length)
            {
                byte b = buffer[offset++];
                num += (b & 0x7F) * (1L << shift);
                if ((b & 0x80) == 0)
                {
                    if (num > int.MaxValue) { value = 0; return false; }
                    value = (int)num;
                    return true;
                }
                shift += 7;
                if (shift > 35) break;
            }
            value = 0;
            return false;
        }

        private static void OnJoinRoom()
        {
            if (PhotonNetwork.CurrentRoom == null) return;
            inRoom = true;
            currentRoom = PhotonNetwork.CurrentRoom.Name;
            currentRegion = PhotonNetwork.CloudRegion;
            currentRoomVisible = PhotonNetwork.CurrentRoom.IsVisible;
            Sids.Clear();
            nextSid = 0;
            Enqueue(Frame(16, EncodeRoomEnter()));
            instance.StartCoroutine(RoomStateAfterRigsSettle());
        }

        private static void OnLeaveRoom()
        {
            if (!inRoom) return;
            inRoom = false;
            Sids.Clear();
            Enqueue(Frame(17, new byte[1]));
        }

        private static void OnPlayerJoin(NetPlayer player)
        {
            if (!inRoom || player == null) return;
            ushort sid = AssignSid(player);
            instance.StartCoroutine(PlayerJoinWhenRigResolves(player, sid));
        }

        private static void OnPlayerLeave(NetPlayer player)
        {
            if (!inRoom || player == null || !Sids.TryGetValue(player, out ushort sid)) return;
            Sids.Remove(player);
            Enqueue(Frame(19, EncodeU16(sid)));
        }

        private static IEnumerator RoomStateAfterRigsSettle()
        {
            yield return new WaitForSecondsRealtime(RoomStateDelay);
            if (!inRoom || PhotonNetwork.CurrentRoom == null) yield break;
            currentRoom = PhotonNetwork.CurrentRoom.Name;
            currentRegion = PhotonNetwork.CloudRegion;
            currentRoomVisible = PhotonNetwork.CurrentRoom.IsVisible;
            Enqueue(Frame(20, EncodeRoomState()));
        }

        private static IEnumerator PlayerJoinWhenRigResolves(NetPlayer player, ushort sid)
        {
            VRRig rig = null;
            float deadline = Time.unscaledTime + RigWaitTimeout;
            while (Time.unscaledTime < deadline)
            {
                rig = GetVRRigFromPlayer(player);
                if (rig != null) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (rig != null)
            {
                float cosmeticDeadline = Time.unscaledTime + CosmeticWaitTimeout;
                while (Time.unscaledTime < cosmeticDeadline)
                {
                    var owned = GetOwnedCosmetics(rig);
                    if (owned != null && owned.Count > 0) break;
                    yield return new WaitForSecondsRealtime(0.25f);
                }
            }
            if (inRoom && Sids.TryGetValue(player, out ushort assigned) && assigned == sid)
                Enqueue(Frame(18, EncodePlayerEntry(sid, player.NickName, rig, player.UserId)));
        }

        private static ushort AssignSid(NetPlayer player)
        {
            if (Sids.TryGetValue(player, out ushort value)) return value;
            Sids[player] = nextSid;
            return nextSid++;
        }

        private static void Enqueue(byte[] frame)
        {
            if (frame.Length > FrameMax) return;
            while (Pending.Count >= MaxQueued) Pending.Dequeue();
            Pending.Enqueue(frame);
        }

        private static void Flush()
        {
            if (!wsHelloed || !WsOpen) return;
            int limit = shuttingDown ? int.MaxValue : 32;
            int sent = 0;
            while (Pending.Count > 0 && sent < limit)
            {
                byte[] frame = Pending.Peek();
                if (!TrySend(frame)) break;
                Pending.Dequeue();
                sent++;
            }
        }

        private static byte[] Frame(byte type, byte[] payload)
        {
            Writer writer = new Writer();
            writer.Write(type);
            writer.WriteVarint(payload.Length);
            writer.AppendBytes(payload);
            return writer.Bytes;
        }

        private static string Cap(string value, int max)
        {
            return (value != null && value.Length > max) ? value.Substring(0, max) : value;
        }

        private static string CleanUid(string value)
        {
            if (value == null || !Regex.IsMatch(value, "^[0-9A-Fa-f]{8,40}$"))
                return string.Empty;
            return value.ToUpperInvariant();
        }

        private static string SelfUid()
        {
            if (selfUid == null)
            {
                try
                {
                    selfUid = CleanUid(PlayFabAuthenticator.instance.GetPlayFabPlayerId());
                }
                catch
                {
                    selfUid = string.Empty;
                }
            }
            return selfUid;
        }

        private static byte[] EncodeRoomEnter()
        {
            Writer writer = new Writer();
            WriteRoomRef(writer, currentRoom);
            WriteString(writer, Cap(currentRegion, 16));
            writer.Write((byte)(currentRoomVisible ? 1u : 0u));
            WriteString(writer, Cap(NetworkSystem.Instance.GameModeString, 128));
            writer.WriteVarint(PhotonNetwork.PlayerList.Length);
            WriteString(writer, Cap(PhotonNetwork.ServerAddress, 64));
            return writer.Bytes;
        }

        private static readonly FieldInfo OwnedCosmeticsField =
            typeof(VRRig).GetField("_playerOwnedCosmetics", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static ICollection<string> GetOwnedCosmetics(VRRig rig)
        {
            if (rig == null || OwnedCosmeticsField == null) return null;
            try
            {
                return OwnedCosmeticsField.GetValue(rig) as ICollection<string>;
            }
            catch
            {
                return null;
            }
        }

        private static VRRig GetVRRigFromPlayer(NetPlayer p)
        {
            if (p == null) return null;
            try
            {
                if (GorillaGameManager.instance != null)
                {
                    VRRig found = GorillaGameManager.StaticFindRigForPlayer(p);
                    if (found != null) return found;
                }
            }
            catch { }
            try
            {
                foreach (VRRig rig in GameObject.FindObjectsByType<VRRig>(FindObjectsSortMode.None))
                {
                    if (rig != null && rig.Creator != null && rig.Creator.ActorNumber == p.ActorNumber)
                        return rig;
                }
            }
            catch { }
            return null;
        }

        private static bool IsSteamPlayer(VRRig rig)
        {
            try
            {
                var owned = GetOwnedCosmetics(rig);
                string text = owned != null ? string.Join("", owned) : string.Empty;
                int propCount = rig.Creator?.GetPlayerRef()?.CustomProperties?.Count ?? 0;
                return text.Contains("S. FIRST LOGIN") || text.Contains("FIRST LOGIN") || propCount >= 2;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] EncodePlayerEntry(ushort sid, string nickname, VRRig rig, string uid)
        {
            Writer writer = new Writer();
            writer.Write((byte)(sid >> 8));
            writer.Write((byte)(sid & 0xFF));
            if (nickname != null && nickname.Length > 12)
                nickname = nickname.Substring(0, 12);
            WriteString(writer, nickname);
            byte b = 2;
            Color color = Color.black;
            if (rig != null)
            {
                b = !IsSteamPlayer(rig) ? (byte)1 : (byte)0;
                color = rig.playerColor;
            }
            writer.Write(b);
            writer.Write((byte)Math.Round(color.r * 255f));
            writer.Write((byte)Math.Round(color.g * 255f));
            writer.Write((byte)Math.Round(color.b * 255f));
            var owned = GetOwnedCosmetics(rig);
            if (owned == null)
            {
                writer.WriteVarint(0);
            }
            else
            {
                List<string> list = new List<string>(10);
                foreach (string item in owned)
                {
                    if (list.Count >= 10) break;
                    list.Add(item);
                }
                writer.WriteVarint(list.Count);
                for (int i = 0; i < list.Count; i++)
                    WriteString(writer, Cap(list[i], 32));
            }
            WriteString(writer, CleanUid(uid));
            return writer.Bytes;
        }

        private static byte[] EncodeRoomState()
        {
            Writer writer = new Writer();
            WriteRoomRef(writer, currentRoom);
            WriteString(writer, currentRegion);
            writer.Write((byte)(currentRoomVisible ? 1u : 0u));
            Player[] playerList = PhotonNetwork.PlayerList;
            int num = Math.Min(playerList.Length, 32);
            writer.WriteVarint(num);
            for (int i = 0; i < num; i++)
            {
                Player player = playerList[i];
                writer.AppendBytes(EncodePlayerEntry(AssignSid(player), player.NickName, GetVRRigFromPlayer(player), player.UserId));
            }
            return writer.Bytes;
        }

        private static void WriteRoomRef(Writer w, string room)
        {
            room = (room ?? string.Empty).ToUpperInvariant();
            bool valid6 = room.Length == 6;
            if (valid6)
            {
                for (int i = 0; i < 6; i++)
                {
                    if (RoomAlphabet.IndexOf(room[i]) < 0)
                    {
                        valid6 = false;
                        break;
                    }
                }
            }
            if (valid6)
            {
                uint num = 0u;
                foreach (char value in room)
                    num = num * 40 + (uint)RoomAlphabet.IndexOf(value);
                w.Write(0);
                w.Write((byte)(num & 0xFF));
                w.Write((byte)((num >> 8) & 0xFF));
                w.Write((byte)((num >> 16) & 0xFF));
                w.Write((byte)((num >> 24) & 0xFF));
            }
            else
            {
                w.Write(1);
                WriteString(w, room);
            }
        }

        private static void WriteString(Writer w, string value)
        {
            if (value == null) value = string.Empty;
            w.WriteVarint(value.Length);
            foreach (char c in value)
                w.Write((byte)c);
        }

        private static byte[] EncodeU16(ushort value)
        {
            return new byte[2]
            {
                (byte)(value >> 8),
                (byte)(value & 0xFF)
            };
        }
    }
}
