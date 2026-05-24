// Biomata.SDK — SimulationEvent.cs
// A real-time engine event delivered via EventStreamClient.

using System.Collections.Generic;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// A real-time event emitted by the biomata-engine during simulation.
    /// Delivered via <c>EventStreamClient.On()</c> subscriptions.
    ///
    /// Common event types (see Python <c>src/engine/event_bus.py</c>):
    ///   "tick_start"        — start of a new tick (data: world metadata)
    ///   "tick_end"          — tick completed (data: agent_count)
    ///   "action_completed"  — one agent's action finished (data: action, outcome, agent_name)
    ///   "action_failed"     — action dispatch failed
    ///   "brain_decided"     — brain chose an intent (data: prompt, raw output)
    ///   "agent_step_error"  — unhandled exception in an agent step
    ///   "social_updated"    — inter-agent relationship weight changed
    /// </summary>
    public class SimulationEvent
    {
        /// <summary>Session identifier from the server.</summary>
        public string SessionId { get; }

        /// <summary>Event type string (see above).</summary>
        public string EventType { get; }

        /// <summary>Tick during which this event was emitted.</summary>
        public int Tick { get; }

        /// <summary>Agent that triggered the event, or <c>"engine"</c> for system events.</summary>
        public string AgentId { get; }

        /// <summary>
        /// Event-type-specific payload decoded from the JSON event frame.
        /// Use the extension methods for convenient access:
        ///   <c>ev.Data.GetString("action")</c>, <c>ev.Data.GetNumber("health")</c>
        /// </summary>
        public Dictionary<string, object> Data { get; }

        /// <summary>
        /// Transport-neutral constructor. Used by WebSocketTransport after
        /// decoding the JSON event frame into plain BCL types.
        /// </summary>
        internal SimulationEvent(
            string                     sessionId,
            string                     eventType,
            int                        tick,
            string                     agentId,
            Dictionary<string, object> data)
        {
            SessionId = sessionId ?? string.Empty;
            EventType = eventType ?? string.Empty;
            Tick      = tick;
            AgentId   = agentId ?? string.Empty;
            Data      = data    ?? new Dictionary<string, object>();
        }

        public override string ToString() =>
            $"[t{Tick}][{EventType}] agent={AgentId}";
    }
}
