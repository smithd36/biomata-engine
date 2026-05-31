// Biomata.SDK — WebSocketTransport.cs
//
// JSON-over-WebSocket transport, using System.Net.WebSockets.ClientWebSocket
// (built into Unity 6's .NET Standard 2.1 BCL — no precompiled DLL needed).
//
// Protocol v1 — see docs/websocket-protocol.md for the full spec.
// Four frame shapes (all JSON text, UTF-8):
//   {"type":"hlo", "v":1, "server":"biomata-engine", "server_version":"...",
//    "session_id":"...", "capabilities":[...]}                     server→client on connect
//   {"type":"req", "v":1, "id":"<uuid>", "method":"<name>", "params":{}}   client→server
//   {"type":"res", "v":1, "id":"<uuid>", "ok":true,  "result":{}}          server→client
//   {"type":"res", "v":1, "id":"<uuid>", "ok":false,
//    "error":{"code":-32601,"name":"METHOD_NOT_FOUND","message":"..."}}     server→client
//   {"type":"evt", "v":1, "session_id":"...", "seq":N, "event_type":"...",
//    "tick":N, "agent_id":"...", "ts":"...Z", "data":{}}                    server→client
//
// One receive loop demultiplexes hlo/res/evt frames.
// Pending requests are awaited via TaskCompletionSource keyed on id.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Biomata.SDK.Transport
{
    /// <summary>
    /// WebSocket-based ITransport implementation. Default transport for Unity 6.
    ///
    /// Lifecycle:
    ///   1. ConnectAsync opens the ClientWebSocket and starts the receive loop.
    ///   2. RPC methods send a "req" frame, await its correlated "res" frame.
    ///   3. SubscribeEventsAsync sends a subscribe request; thereafter "evt"
    ///      frames are routed to OnEvent until UnsubscribeEventsAsync or
    ///      DisconnectAsync.
    ///   4. DisconnectAsync closes the socket cleanly and cancels pending RPCs.
    /// </summary>
    internal sealed class WebSocketTransport : ITransport
    {
        // ── Wire protocol constants ───────────────────────────────────────────
        // Must match src/transport/websocket/protocol.py exactly.

        private const int    PROTOCOL_VERSION = 1;
        private const string T_HELLO    = "hlo";
        private const string T_REQUEST  = "req";
        private const string T_RESPONSE = "res";
        private const string T_EVENT    = "evt";

        private const string M_HEALTH        = "health_check";
        private const string M_REGISTER      = "register_agent";
        private const string M_REMOVE        = "remove_agent";
        private const string M_OBSERVATION   = "send_observation";
        private const string M_TICK          = "tick";
        private const string M_PAUSE         = "pause";
        private const string M_RESUME        = "resume";
        private const string M_SNAPSHOT      = "snapshot";
        private const string M_RESTORE       = "restore";
        private const string M_SUBSCRIBE     = "subscribe_events";
        private const string M_UNSUBSCRIBE   = "unsubscribe_events";
        private const string M_ROLES_LIST    = "roles.list";

        private readonly BiomataConfig _config;
        private readonly string        _url;

        private ClientWebSocket _ws;
        private CancellationTokenSource _recvCts;
        private Task _recvLoop;

        // Pending requests keyed by request id. The receive loop completes the
        // TCS when a matching "res" frame arrives.
        private readonly Dictionary<string, TaskCompletionSource<JObject>> _pending = new();
        private readonly object _pendingLock = new();

        // Serialize sends — ClientWebSocket.SendAsync isn't safe to call from
        // multiple tasks concurrently on the same socket.
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // ── Server info (populated from hlo frame) ────────────────────────────

        /// <summary>Server software version string received in the hello frame.</summary>
        public string ServerVersion { get; private set; } = string.Empty;

        /// <summary>Capability strings the server declared in the hello frame.</summary>
        public IReadOnlyList<string> ServerCapabilities { get; private set; } = Array.Empty<string>();

        /// <summary>Tick mode declared by the server in the hello frame ("host_driven" or "autonomous").</summary>
        public string ServerTickMode { get; private set; } = "host_driven";

        // ── ITransport state ──────────────────────────────────────────────────

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public bool            IsConnected => State == ConnectionState.Connected;
        public event Action<ConnectionState> OnStateChanged;
        public event Action<SimulationEvent> OnEvent;
        public event Action<Exception>       OnEventStreamDisconnected;

        // ── Construction ──────────────────────────────────────────────────────

        public WebSocketTransport(BiomataConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            var scheme = config.UseTls ? "wss" : "ws";
            _url = $"{scheme}://{config.Host}:{config.Port}";
        }

        // ── Connection lifecycle ──────────────────────────────────────────────

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (State == ConnectionState.Connected)
                throw new InvalidOperationException("Already connected. Call DisconnectAsync() first.");

            SetState(ConnectionState.Connecting);
            try
            {
                _ws = new ClientWebSocket();
                // Keep the socket alive across idle periods so the event stream
                // stays connected through ordinary tick gaps.
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(_config.ConnectTimeout);
                await _ws.ConnectAsync(new Uri(_url), connectCts.Token);

                _recvCts  = new CancellationTokenSource();
                _recvLoop = ReceiveLoopAsync(_recvCts.Token);

                SetState(ConnectionState.Connected);
            }
            catch (Exception ex)
            {
                SetState(ConnectionState.Faulted);
                await SafeCloseAsync();
                if (ex is BiomataException) throw;
                throw new BiomataException($"WebSocket connect to {_url} failed: {ex.Message}", ex);
            }
        }

        public async Task DisconnectAsync()
        {
            if (State == ConnectionState.Disconnected || State == ConnectionState.Disconnecting)
                return;

            SetState(ConnectionState.Disconnecting);
            await SafeCloseAsync();
            SetState(ConnectionState.Disconnected);
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            _ws?.Dispose();
        }

        private async Task SafeCloseAsync()
        {
            // Cancel any pending RPCs.
            lock (_pendingLock)
            {
                foreach (var tcs in _pending.Values)
                    tcs.TrySetException(new BiomataException("Transport closed before response."));
                _pending.Clear();
            }

            _recvCts?.Cancel();

            if (_ws != null)
            {
                try
                {
                    if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client close", closeCts.Token);
                    }
                }
                catch { /* best-effort close */ }
            }

            if (_recvLoop != null)
            {
                try { await _recvLoop; }
                catch (Exception) { /* loop exit is fine */ }
                _recvLoop = null;
            }

            _recvCts?.Dispose();
            _recvCts = null;
        }

        // ── Receive loop ──────────────────────────────────────────────────────

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            using var ms = new MemoryStream();
            Exception faultCause = null;

            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult res;
                    try
                    {
                        res = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        faultCause = ex;
                        break;
                    }

                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        try
                        {
                            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "server close", CancellationToken.None);
                        }
                        catch { /* ignore */ }
                        break;
                    }

                    ms.Write(buffer, 0, res.Count);
                    if (!res.EndOfMessage) continue;

                    var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    ms.SetLength(0);

                    try
                    {
                        DispatchFrame(text);
                    }
                    catch (Exception ex)
                    {
                        // Don't kill the loop on a single malformed frame.
                        UnityEngine.Debug.LogWarning($"[Biomata] dropped malformed frame: {ex.Message}");
                    }
                }
            }
            finally
            {
                // Propagate disconnection to any waiting RPCs.
                lock (_pendingLock)
                {
                    foreach (var tcs in _pending.Values)
                        tcs.TrySetException(new BiomataException(
                            "Transport disconnected during request.", faultCause));
                    _pending.Clear();
                }
                OnEventStreamDisconnected?.Invoke(faultCause);
            }
        }

        private void DispatchFrame(string text)
        {
            var obj  = JObject.Parse(text);
            var type = (string)obj["type"];

            if (type == T_HELLO)
            {
                var serverV = (int?)obj["v"] ?? 0;
                if (serverV != PROTOCOL_VERSION)
                    UnityEngine.Debug.LogWarning(
                        $"[Biomata] Server protocol v{serverV} differs from client v{PROTOCOL_VERSION}. " +
                        "Some features may not work correctly.");
                ServerVersion      = (string)obj["server_version"] ?? string.Empty;
                ServerCapabilities = obj["capabilities"] is JArray caps
                    ? caps.ToObject<List<string>>() ?? (IReadOnlyList<string>)Array.Empty<string>()
                    : Array.Empty<string>();
                ServerTickMode = (string)obj["tick_mode"] ?? "host_driven";
                UnityEngine.Debug.Log($"[Biomata] Connected — tick mode: {ServerTickMode}");
                return;
            }

            if (type == T_RESPONSE)
            {
                var id = (string)obj["id"];
                if (string.IsNullOrEmpty(id)) return;
                TaskCompletionSource<JObject> tcs;
                lock (_pendingLock)
                {
                    if (!_pending.TryGetValue(id, out tcs)) return;
                    _pending.Remove(id);
                }
                if ((bool?)obj["ok"] == true)
                {
                    tcs.TrySetResult(obj["result"] as JObject ?? new JObject());
                }
                else
                {
                    // v1 structured error: {"code": N, "name": "...", "message": "..."}
                    // Fall back to plain string for any pre-v1 server still in the field.
                    string errMsg;
                    var errToken = obj["error"];
                    if (errToken is JObject errObj)
                    {
                        var name = (string)errObj["name"] ?? "ERROR";
                        var msg  = (string)errObj["message"] ?? "unknown error";
                        errMsg = $"[{name}] {msg}";
                    }
                    else
                    {
                        errMsg = errToken?.ToString() ?? "unknown error";
                    }
                    tcs.TrySetException(new BiomataException(errMsg));
                }
                return;
            }

            if (type == T_EVENT)
            {
                var ev = new SimulationEvent(
                    sessionId : (string)obj["session_id"] ?? string.Empty,
                    eventType : (string)obj["event_type"] ?? string.Empty,
                    tick      : (int?)  obj["tick"]       ?? 0,
                    agentId   : (string)obj["agent_id"]   ?? string.Empty,
                    data      : JsonHelpers.FromObject(obj["data"] as JObject)
                );
                OnEvent?.Invoke(ev);
            }
        }

        // ── RPC helper ────────────────────────────────────────────────────────

        private async Task<JObject> RequestAsync(string method, JObject parameters, CancellationToken ct)
        {
            if (!IsConnected)
                throw new BiomataException("Transport is not connected. Call ConnectAsync() first.");

            var id  = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock) _pending[id] = tcs;

            // Apply the configured per-call deadline so a misbehaving server
            // can't leave RPCs hanging forever.
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeout = _config.DefaultCallTimeout;
            if (timeout > TimeSpan.Zero)
                deadlineCts.CancelAfter(timeout);

            using var reg = deadlineCts.Token.Register(() =>
            {
                lock (_pendingLock) _pending.Remove(id);
                tcs.TrySetCanceled(deadlineCts.Token);
            });

            var envelope = new JObject
            {
                ["type"]   = T_REQUEST,
                ["v"]      = PROTOCOL_VERSION,
                ["id"]     = id,
                ["method"] = method,
                ["params"] = parameters ?? new JObject(),
            };
            await SendTextAsync(envelope.ToString(Formatting.None), deadlineCts.Token);

            try
            {
                return await tcs.Task;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new BiomataException($"{method} cancelled by caller");
            }
            catch (OperationCanceledException)
            {
                throw new BiomataException($"{method} timed out after {timeout.TotalSeconds:F1}s");
            }
        }

        private async Task SendTextAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _sendLock.WaitAsync(ct);
            try
            {
                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ── Typed RPCs ────────────────────────────────────────────────────────

        public async Task<HealthStatus> HealthCheckAsync(CancellationToken ct = default)
        {
            var r = await RequestAsync(M_HEALTH, null, ct);
            return new HealthStatus
            {
                Status       = (string)r["status"]        ?? string.Empty,
                SessionState = (string)r["session_state"] ?? string.Empty,
                Tick         = (int?)  r["tick"]          ?? 0,
                AgentCount   = (int?)  r["agent_count"]   ?? 0,
            };
        }

        public async Task RegisterAgentAsync(AgentRegistration reg, CancellationToken ct = default)
        {
            if (reg == null) throw new ArgumentNullException(nameof(reg));
            if (string.IsNullOrEmpty(reg.AgentId))    throw new ArgumentException("AgentId required", nameof(reg));
            if (string.IsNullOrEmpty(reg.BrainClass)) throw new ArgumentException("BrainClass required", nameof(reg));

            var p = new JObject
            {
                ["agent_id"]    = reg.AgentId,
                ["agent_name"]  = reg.AgentName ?? reg.AgentId,
                ["brain_class"] = reg.BrainClass,
            };
            if (reg.BrainConfig != null)
                p["brain_config"]  = JsonHelpers.ToToken(reg.BrainConfig);
            if (!string.IsNullOrEmpty(reg.MemoryClass))
                p["memory_class"]  = reg.MemoryClass;
            if (reg.MemoryConfig != null)
                p["memory_config"] = JsonHelpers.ToToken(reg.MemoryConfig);
            if (reg.Capabilities != null && reg.Capabilities.Length > 0)
                p["capabilities"] = new JArray((object[])reg.Capabilities);

            var r = await RequestAsync(M_REGISTER, p, ct);
            EnsureSuccess(r, $"RegisterAgent({reg.AgentId})");
        }

        public async Task RemoveAgentAsync(string agentId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(agentId)) throw new ArgumentException("agentId required", nameof(agentId));
            var p = new JObject { ["agent_id"] = agentId };
            var r = await RequestAsync(M_REMOVE, p, ct);
            EnsureSuccess(r, $"RemoveAgent({agentId})");
        }

        public async Task SendObservationAsync(string agentId, Dictionary<string, object> observation, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(agentId)) throw new ArgumentException("agentId required", nameof(agentId));
            var p = new JObject
            {
                ["agent_id"]    = agentId,
                ["observation"] = JsonHelpers.ToToken(observation ?? new Dictionary<string, object>()),
            };
            var r = await RequestAsync(M_OBSERVATION, p, ct);
            EnsureSuccess(r, $"SendObservation({agentId})");
        }

        public async Task<TickResult> TickAsync(
            IEnumerable<AgentObservationData> observations,
            Dictionary<string, object>        metadata,
            CancellationToken                 ct = default)
        {
            var obsArray = new JArray();
            if (observations != null)
            {
                foreach (var o in observations)
                {
                    if (o == null || string.IsNullOrEmpty(o.AgentId)) continue;
                    obsArray.Add(new JObject
                    {
                        ["agent_id"]    = o.AgentId,
                        ["observation"] = JsonHelpers.ToToken(o.Observation ?? new Dictionary<string, object>()),
                    });
                }
            }

            var p = new JObject
            {
                ["agent_observations"] = obsArray,
                ["world_metadata"]     = JsonHelpers.ToToken(metadata ?? new Dictionary<string, object>()),
            };
            var r = await RequestAsync(M_TICK, p, ct);
            return ParseTickResult(r);
        }

        public async Task<string> PauseAsync(CancellationToken ct = default)
        {
            var r = await RequestAsync(M_PAUSE, null, ct);
            return (string)r["state"] ?? string.Empty;
        }

        public async Task<string> ResumeAsync(CancellationToken ct = default)
        {
            var r = await RequestAsync(M_RESUME, null, ct);
            return (string)r["state"] ?? string.Empty;
        }

        public async Task<SnapshotData> SnapshotAsync(CancellationToken ct = default)
        {
            var r = await RequestAsync(M_SNAPSHOT, null, ct);
            var b64 = (string)r["data_b64"];
            return new SnapshotData
            {
                Data      = string.IsNullOrEmpty(b64) ? Array.Empty<byte>() : Convert.FromBase64String(b64),
                Tick      = (int?)r["tick"] ?? 0,
                CreatedAt = (string)r["created_at"] ?? string.Empty,
            };
        }

        public async Task<int> RestoreAsync(byte[] snapshotData, CancellationToken ct = default)
        {
            if (snapshotData == null || snapshotData.Length == 0)
                throw new ArgumentException("snapshotData required", nameof(snapshotData));
            var p = new JObject { ["data_b64"] = Convert.ToBase64String(snapshotData) };
            var r = await RequestAsync(M_RESTORE, p, ct);
            EnsureSuccess(r, "Restore");
            return (int?)r["tick"] ?? 0;
        }

        // ── Event stream ──────────────────────────────────────────────────────

        public async Task SubscribeEventsAsync(IEnumerable<string> eventTypeFilter, CancellationToken ct = default)
        {
            var p = new JObject();
            if (eventTypeFilter != null)
            {
                var arr = new JArray();
                foreach (var t in eventTypeFilter) if (!string.IsNullOrEmpty(t)) arr.Add(t);
                if (arr.Count > 0) p["event_types"] = arr;
            }
            await RequestAsync(M_SUBSCRIBE, p, ct);
        }

        public async Task UnsubscribeEventsAsync(CancellationToken ct = default)
        {
            if (!IsConnected) return;
            try { await RequestAsync(M_UNSUBSCRIBE, null, ct); }
            catch { /* best-effort */ }
        }

        public async Task<RolesData> RolesListAsync(CancellationToken ct = default)
        {
            var r = await RequestAsync(M_ROLES_LIST, null, ct);
            var data = new RolesData { version = (string)r["version"] ?? "1" };
            var arr  = r["roles"] as JArray;
            if (arr != null)
            {
                var entries = new List<RoleEntry>(arr.Count);
                foreach (var item in arr)
                {
                    if (!(item is JObject o)) continue;
                    entries.Add(new RoleEntry
                    {
                        name           = (string)o["name"]           ?? string.Empty,
                        capabilities   = (o["capabilities"]  as JArray)?.ToObject<string[]>() ?? Array.Empty<string>(),
                        observations   = (o["observations"]  as JArray)?.ToObject<string[]>() ?? Array.Empty<string>(),
                        brain_provider = (string)o["brain_provider"],
                        brain_class    = (string)o["brain_class"],
                    });
                }
                data.roles = entries.ToArray();
            }
            else
            {
                data.roles = Array.Empty<RoleEntry>();
            }
            return data;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void EnsureSuccess(JObject result, string operation)
        {
            var ok    = (bool?)result["success"];
            var error = (string)result["error"];
            if (ok == false)
                throw new BiomataException($"{operation} failed: {error ?? "(no message)"}");
        }

        private static TickResult ParseTickResult(JObject r)
        {
            var tick      = (int?)r["tick"] ?? 0;
            var decisions = ParseDecisions(r["decisions"] as JArray);
            var errors    = ParseErrors(r["errors"] as JArray);
            return new TickResult(tick, decisions, errors);
        }

        private static IReadOnlyList<AgentDecisionResult> ParseDecisions(JArray arr)
        {
            if (arr == null || arr.Count == 0) return Array.Empty<AgentDecisionResult>();
            var list = new List<AgentDecisionResult>(arr.Count);
            foreach (var item in arr)
            {
                if (!(item is JObject d)) continue;
                var paramsDict = JsonHelpers.FromObject(d["parameters"] as JObject);
                var cmds = new List<Dictionary<string, object>>();
                if (d["engine_commands"] is JArray cmdArr)
                {
                    foreach (var c in cmdArr)
                        if (c is JObject co) cmds.Add(JsonHelpers.FromObject(co));
                }
                list.Add(new AgentDecisionResult(
                    agentId        : (string)d["agent_id"]   ?? string.Empty,
                    agentName      : (string)d["agent_name"] ?? string.Empty,
                    action         : (string)d["action"]     ?? string.Empty,
                    parameters     : paramsDict,
                    outcomeText    : (string)d["outcome_text"] ?? string.Empty,
                    engineCommands : cmds,
                    error          : (string)d["error"]
                ));
            }
            return list;
        }

        private static IReadOnlyList<(string AgentId, string Msg)> ParseErrors(JArray arr)
        {
            if (arr == null || arr.Count == 0) return Array.Empty<(string, string)>();
            var list = new List<(string, string)>(arr.Count);
            foreach (var item in arr)
            {
                if (!(item is JObject e)) continue;
                list.Add((
                    (string)e["agent_id"] ?? string.Empty,
                    (string)e["message"]  ?? string.Empty));
            }
            return list;
        }

        private void SetState(ConnectionState s)
        {
            if (State == s) return;
            State = s;
            OnStateChanged?.Invoke(s);
        }
    }
}
