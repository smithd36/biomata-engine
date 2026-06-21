# Observation contract

The observation each agent sends the backend is a `Dictionary<string, object>` built per tick
by `ObservationCollector` from its sibling `ObservationProviderBase` components. The keys are a
**producer/consumer contract**: Unity providers write them, the Python brain reads them. A
typo'd key fails silently, so the SDK-canonical keys are pinned as constants in
[`ObservationKeys.cs`](ObservationKeys.cs) — reference those instead of string literals.

Validate a scene from **Biomata ▸ Validate Observation Contract** (lists each agent's declared
keys, flags two providers colliding on one key).

## Canonical keys (SDK producers)

| Key | Type | Producer | Meaning |
|-----|------|----------|---------|
| `role` | string | `BiomataAgent` | Agent role string |
| `capabilities` | string[] | `BiomataAgent` | Capability tags |
| `position_x` / `position_y` / `position_z` | double | `TransformObservationProvider` | World position |
| `rotation_y` | double | `TransformObservationProvider` | Yaw degrees (optional) |
| `velocity_x` / `velocity_z` | double | `TransformObservationProvider` | Rigidbody velocity (optional) |
| `sim_time` | double | `TimeObservationProvider` | Seconds since sim start |
| `time_of_day` | double | `TimeObservationProvider` | 0–1 fraction or 0–24 h (optional) |
| `frame_count` | int | `TimeObservationProvider` | Unity frame (debug, optional) |
| `nearby_agents` | list of `{id,name,distance?}` | `NearbyAgentsObservationProvider` | Agents in radius, nearest-first |
| `nearby_agent_count` | int | `NearbyAgentsObservationProvider` | Count of the above |
| `nearest_agent_id` | string | `NearbyAgentsObservationProvider` | Closest agent (omitted if none) |
| `nearest_agent_distance` | double | `NearbyAgentsObservationProvider` | Distance to closest (optional) |
| `incoming_messages` | list of `{from,from_name,text}` | `ObservationCollector` | Speech directed at this agent; persists `messageLifetimeTicks` |

## Suffix conventions (prefixed / dynamic producers)

These providers use a configurable base key plus fixed suffixes (constants in `ObservationKeys`):

| Producer | Default base | Keys |
|----------|--------------|------|
| `POIObservationProvider` | `nearby_pois` | `{key}` (list), `{key}_count`, `{key}_nearest` |
| `NearbyObjectsObservationProvider` | `nearby_objects` | `{key}` (list), `{key}_count`, `{key}_nearest` |
| `NeedsObservationProvider` (per need) | the need key | `{key}`, `{key}_max`, `{key}_threshold`, `{key}_critical` |

## Engine-injected keys (read-only)

The backend adds these on its side; **don't overwrite them from a Unity provider**:
`agent_id`, `agent_name`, `inventory`, `state_str`, `state_advice`, `state_ext`.

## Custom keys

Sims are free to emit additional keys (e.g. `hunger`, `suspicion`, `supply`). The brain renders
unknown keys generically, so no registration is needed — but keep names stable and document them
for whoever writes the brain prompt. For fixed custom keys, add a `DeclaredObservationKeys`
override to your provider so the validator can include them and catch collisions.
