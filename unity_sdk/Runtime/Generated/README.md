# Runtime/Generated/

This directory holds the auto-generated C# protobuf and gRPC stubs that the
SDK ships. **Do not edit these files by hand** — they are regenerated from
`Proto/simulation.proto` whenever the service definition changes.

## Contents

| File | Description |
|---|---|
| `Simulation.cs` | All proto message classes in the `Biomata.Proto` namespace. |
| `SimulationGrpc.cs` | `Biomata.Proto.SimulationService.SimulationServiceClient` gRPC stub. |

## Regenerate

```bash
cd unity_sdk
python Scripts/vendor.py
```

`vendor.py` also re-vendors the runtime DLLs in `Runtime/Plugins/`. See the
package README for details. End users never need to run this — the outputs are
committed so a fresh `git clone` + UPM import yields a working SDK.
