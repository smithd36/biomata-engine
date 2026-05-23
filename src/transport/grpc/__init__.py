"""
src/transport/grpc
──────────────────────────────────────────────────────────────
Async gRPC transport for the biomata simulation engine.

Public surface:

    from src.transport.grpc import GrpcServer

    # Programmatic:
    server = GrpcServer.from_simulation(sim, port=50051)
    await server.start()
    await server.wait_for_termination()

    # From YAML config:
    server = await GrpcServer.from_config("sim.yaml", port=50051)
    await server.serve()   # blocks until SIGINT/SIGTERM

    # CLI:
    biomata-grpc --config sim.yaml --port 50051

Regenerate stubs after proto edits:
    python src/transport/grpc/generate.py
"""
from src.transport.grpc.server import GrpcServer
from src.transport.grpc.servicer import SimulationServicer

__all__ = ["GrpcServer", "SimulationServicer"]
