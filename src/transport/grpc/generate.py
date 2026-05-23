"""
src/transport/grpc/generate.py
──────────────────────────────────────────────────────────────
Regenerate Python gRPC stubs from simulation.proto.

Run after editing the proto file:

    python src/transport/grpc/generate.py

Requires grpcio-tools:
    pip install grpcio-tools

The generated files are committed to the repo so that users don't need
grpcio-tools at runtime — only when the proto changes.
"""
from __future__ import annotations

import pathlib
import subprocess
import sys


def main() -> None:
    repo_root = pathlib.Path(__file__).parent.parent.parent.parent  # biomata-engine/
    grpc_root = pathlib.Path(__file__).parent                       # src/transport/grpc/
    proto_dir = grpc_root / "proto"
    out_dir   = grpc_root / "generated"
    out_dir.mkdir(exist_ok=True)

    try:
        import grpc_tools
        proto_include = pathlib.Path(grpc_tools.__file__).parent / "_proto"
    except ImportError:
        print("ERROR: grpcio-tools is not installed. Run: pip install grpcio-tools")
        sys.exit(1)

    cmd = [
        sys.executable, "-m", "grpc_tools.protoc",
        f"-I{proto_include}",
        f"-I{proto_dir}",
        f"--python_out={out_dir}",
        f"--grpc_python_out={out_dir}",
        str(proto_dir / "simulation.proto"),
    ]
    print("Running:", " ".join(str(c) for c in cmd))
    result = subprocess.run(cmd, check=False)
    if result.returncode != 0:
        print("ERROR: protoc failed")
        sys.exit(result.returncode)

    # Fix the relative import in the generated grpc file so it works
    # as part of the src.transport.grpc.generated package.
    grpc_file = out_dir / "simulation_pb2_grpc.py"
    text = grpc_file.read_text(encoding="utf-8")
    fixed = text.replace(
        "import simulation_pb2 as simulation__pb2",
        "from src.transport.grpc.generated import simulation_pb2 as simulation__pb2",
    )
    grpc_file.write_text(fixed, encoding="utf-8")

    print(f"Generated stubs written to {out_dir}")


if __name__ == "__main__":
    main()
