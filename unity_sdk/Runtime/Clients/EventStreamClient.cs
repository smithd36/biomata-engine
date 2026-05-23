// Biomata.SDK — EventStreamClient.cs
// Subscribe to the engine's real-time event stream and dispatch to handlers.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;
using Biomata.SDK.Transport;
using UnityEngine;

namespace Biomata.SDK.Clients
{
    /// <summary>
    /// Subscribes to the engine's real-time event stream and fans events out
    /// to per-type C# handlers. Backend-agnostic — works over WebSocket or gRPC.
    ///
    /// Performance: subscriptions are bucketed by event type at subscribe-time
    /// and cached as immutable arrays so per-event dispatch is allocation-free.
    /// </summary>
    public class EventStreamClient
    {
        private readonly ITransport _transport;

        // ── Subscription bookkeeping ──────────────────────────────────────────

        private readonly struct Sub
        {
            public readonly string Filter;          // null = wildcard
            public readonly Action<SimulationEvent> Handler;
            public readonly bool MainThread;

            public Sub(string filter, Action<SimulationEvent> handler, bool mainThread)
            {
                Filter     = filter;
                Handler    = handler;
                MainThread = mainThread;
            }
        }

        private readonly Dictionary<string, Sub> _subscriptions = new Dictionary<string, Sub>();
        private readonly object _subLock = new object();

        // Cached bucketed arrays — rebuilt only when subscriptions change.
        private Dictionary<string, Sub[]> _byType   = new Dictionary<string, Sub[]>();
        private Sub[]                     _wildcard = Array.Empty<Sub>();

        // ── State ─────────────────────────────────────────────────────────────

        private CancellationTokenSource _streamCts;
        private Task                    _streamTask;

        public int ReconnectAttempts { get; private set; }

        public event Action                  OnConnected;
        public event Action<Exception>       OnDisconnected;
        public event Action<BiomataException> OnFailed;

        internal EventStreamClient(ITransport transport)
        {
            _transport = transport;
            _transport.OnEvent                  += DispatchEvent;
            _transport.OnEventStreamDisconnected += ex => OnDisconnected?.Invoke(ex);
        }

        // ── Subscription API ──────────────────────────────────────────────────

        public string On(
            string                  eventType,
            Action<SimulationEvent> handler,
            bool                    dispatchOnMainThread = true)
        {
            if (string.IsNullOrEmpty(eventType)) throw new ArgumentException("eventType required");
            if (handler == null)                 throw new ArgumentNullException(nameof(handler));
            return AddSubscription(eventType, handler, dispatchOnMainThread);
        }

        public string OnAll(
            Action<SimulationEvent> handler,
            bool                    dispatchOnMainThread = true)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return AddSubscription(null, handler, dispatchOnMainThread);
        }

        public void Unsubscribe(string subscriptionId)
        {
            lock (_subLock)
            {
                if (_subscriptions.Remove(subscriptionId)) RebuildBucketsLocked();
            }
        }

        public void UnsubscribeAll()
        {
            lock (_subLock)
            {
                _subscriptions.Clear();
                _byType   = new Dictionary<string, Sub[]>();
                _wildcard = Array.Empty<Sub>();
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        /// <summary>
        /// Open the event stream and begin delivering events.
        /// Returns once the SubscribeEvents call has been acknowledged.
        /// </summary>
        public async Task StartAsync(CancellationToken ct = default)
        {
            if (_streamTask != null && !_streamTask.IsCompleted)
                return;

            _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var filters = new HashSet<string>();
            lock (_subLock)
            {
                if (_wildcard.Length == 0)
                {
                    foreach (var key in _byType.Keys) filters.Add(key);
                }
                // If any wildcard subscription exists, send no filter — server returns all events.
            }

            try
            {
                await _transport.SubscribeEventsAsync(
                    filters.Count > 0 ? filters : null,
                    _streamCts.Token);
                ReconnectAttempts = 0;
                OnConnected?.Invoke();
            }
            catch (Exception ex)
            {
                OnFailed?.Invoke(ex is BiomataException be ? be : new BiomataException("event subscribe failed", ex));
                throw;
            }
        }

        public async Task StopAsync()
        {
            _streamCts?.Cancel();
            try { await _transport.UnsubscribeEventsAsync(); }
            catch { /* best-effort */ }
            if (_streamTask != null)
            {
                try { await _streamTask; }
                catch { /* ignore */ }
                _streamTask = null;
            }
            _streamCts?.Dispose();
            _streamCts = null;
        }

        public bool IsRunning => _streamCts != null && !_streamCts.IsCancellationRequested;

        // ── Dispatch ──────────────────────────────────────────────────────────

        private void DispatchEvent(SimulationEvent ev)
        {
            Sub[] specific;
            Sub[] wildcard;
            lock (_subLock)
            {
                if (!_byType.TryGetValue(ev.EventType, out specific))
                    specific = Array.Empty<Sub>();
                wildcard = _wildcard;
            }
            for (int i = 0; i < specific.Length; i++) InvokeSub(in specific[i], ev);
            for (int i = 0; i < wildcard.Length; i++) InvokeSub(in wildcard[i], ev);
        }

        private static void InvokeSub(in Sub sub, SimulationEvent ev)
        {
            try
            {
                if (sub.MainThread)
                {
                    var h = sub.Handler;
                    MainThreadDispatcher.Dispatch(() => h(ev));
                }
                else
                {
                    sub.Handler(ev);
                }
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private string AddSubscription(string filter, Action<SimulationEvent> handler, bool mainThread)
        {
            var id = Guid.NewGuid().ToString();
            lock (_subLock)
            {
                _subscriptions[id] = new Sub(filter, handler, mainThread);
                RebuildBucketsLocked();
            }
            return id;
        }

        // Caller must hold _subLock.
        private void RebuildBucketsLocked()
        {
            var wildcardCount = 0;
            var perTypeCount  = new Dictionary<string, int>();
            foreach (var sub in _subscriptions.Values)
            {
                if (sub.Filter == null) wildcardCount++;
                else perTypeCount[sub.Filter] = perTypeCount.TryGetValue(sub.Filter, out var c) ? c + 1 : 1;
            }

            var newWildcard = wildcardCount == 0 ? Array.Empty<Sub>() : new Sub[wildcardCount];
            var newByType   = new Dictionary<string, Sub[]>(perTypeCount.Count);
            foreach (var kv in perTypeCount) newByType[kv.Key] = new Sub[kv.Value];

            var wildIdx = 0;
            var typeIdx = new Dictionary<string, int>(perTypeCount.Count);
            foreach (var sub in _subscriptions.Values)
            {
                if (sub.Filter == null) { newWildcard[wildIdx++] = sub; }
                else
                {
                    var arr = newByType[sub.Filter];
                    var idx = typeIdx.TryGetValue(sub.Filter, out var i) ? i : 0;
                    arr[idx]                = sub;
                    typeIdx[sub.Filter]     = idx + 1;
                }
            }
            _wildcard = newWildcard;
            _byType   = newByType;
        }
    }
}
