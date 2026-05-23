#!/usr/bin/env python3
"""
vendor.py
─────────
Developer tool. Run this when:
  - simulation.proto changes, OR
  - you bump pinned NuGet versions in PINNED_PACKAGES, OR
  - you need to regenerate the committed C# stubs from scratch.

End users do not run this. The outputs are committed into the package
(Runtime/Generated/*.cs and Runtime/Plugins/*.dll) so a fresh Unity 6
project gets a clean compile via Package Manager alone.

What it does
────────────
1. Downloads the pinned NuGet .nupkg files from nuget.org.
2. Extracts the Grpc.Tools binaries (protoc + grpc_csharp_plugin) into
   a temp dir and runs them against Proto/simulation.proto.
3. Extracts the runtime DLLs from the other packages into
   Runtime/Plugins/ (netstandard2.0 builds — the broadest Unity-safe TFM).

Pinned versions are chosen for Unity 6 (.NET Standard 2.1 API) compatibility
and align with grpc-dotnet's last stable release that still targets
netstandard2.0. Don't upgrade to a version that drops netstandard2.0 without
re-validating in a Unity 6 project.
"""
from __future__ import annotations

import hashlib
import io
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

# ── Configuration ─────────────────────────────────────────────────────────────

SDK_ROOT     = Path(__file__).resolve().parent.parent
PROTO_FILE   = SDK_ROOT / "Proto" / "simulation.proto"
PLUGINS_DIR  = SDK_ROOT / "Runtime" / "Plugins"
GENERATED   = SDK_ROOT / "Runtime" / "Generated"

NUGET_FEED   = "https://api.nuget.org/v3-flatcontainer"

# Pinned NuGet packages. Versions chosen to retain netstandard2.0 lib folder
# (Unity 6's .NET Standard 2.1 runtime is a superset of netstandard2.0).
#
# Tuple format: (package_id_lower, version, dll_path_inside_nupkg, output_filename)
#
# Note: package IDs are lowercased here because nuget.org's flat-container
# API is case-sensitive and expects lowercase.
PINNED_PACKAGES: list[tuple[str, str, str, str]] = [
    ("google.protobuf",                          "3.27.3", "lib/netstandard2.0/Google.Protobuf.dll",                          "Google.Protobuf.dll"),
    ("grpc.core.api",                            "2.65.0", "lib/netstandard2.0/Grpc.Core.Api.dll",                            "Grpc.Core.Api.dll"),
    ("grpc.net.common",                          "2.65.0", "lib/netstandard2.0/Grpc.Net.Common.dll",                          "Grpc.Net.Common.dll"),
    ("grpc.net.client",                          "2.65.0", "lib/netstandard2.0/Grpc.Net.Client.dll",                          "Grpc.Net.Client.dll"),
    ("system.io.pipelines",                      "8.0.0",  "lib/netstandard2.0/System.IO.Pipelines.dll",                      "System.IO.Pipelines.dll"),
    ("system.diagnostics.diagnosticsource",      "8.0.0",  "lib/netstandard2.0/System.Diagnostics.DiagnosticSource.dll",      "System.Diagnostics.DiagnosticSource.dll"),
    ("microsoft.extensions.logging.abstractions","8.0.0",  "lib/netstandard2.0/Microsoft.Extensions.Logging.Abstractions.dll","Microsoft.Extensions.Logging.Abstractions.dll"),
]

# Grpc.Tools is a build-only dependency that ships native protoc and the
# C# gRPC plugin for every supported platform. We grab the host platform's
# binaries to generate the stubs and then discard the package.
GRPC_TOOLS_VERSION = "2.65.0"


# ── Helpers ───────────────────────────────────────────────────────────────────

def log(msg: str) -> None:
    # Force ASCII for Windows consoles using cp1252 (default in many shells).
    print(f"[vendor] {msg}".encode("ascii", "replace").decode("ascii"), flush=True)


def download(url: str) -> bytes:
    log(f"download {url}")
    with urllib.request.urlopen(url, timeout=60) as resp:
        return resp.read()


def nupkg_url(pkg_id_lower: str, version: str) -> str:
    return f"{NUGET_FEED}/{pkg_id_lower}/{version}/{pkg_id_lower}.{version}.nupkg"


def extract_dll(nupkg_bytes: bytes, inner_path: str, dest: Path) -> None:
    """Extract a single file from a .nupkg (which is just a ZIP) to dest."""
    with zipfile.ZipFile(io.BytesIO(nupkg_bytes)) as zf:
        # Be tolerant of leading slashes and casing differences across packages.
        wanted = inner_path.replace("\\", "/").lstrip("/").lower()
        for name in zf.namelist():
            if name.replace("\\", "/").lstrip("/").lower() == wanted:
                with zf.open(name) as src, open(dest, "wb") as dst:
                    shutil.copyfileobj(src, dst)
                return
        raise FileNotFoundError(
            f"{inner_path} not found in package. Available entries:\n  " +
            "\n  ".join(sorted(zf.namelist()))
        )


def host_grpc_tools_platform() -> str:
    """Return the subdirectory under Grpc.Tools/tools/ that holds host binaries."""
    sysname = platform.system().lower()
    arch    = platform.machine().lower()
    if sysname == "windows":
        return "windows_x64" if "64" in arch or arch == "amd64" else "windows_x86"
    if sysname == "darwin":
        return "macosx_arm64" if arch in ("arm64", "aarch64") else "macosx_x64"
    if sysname == "linux":
        return "linux_arm64" if arch in ("aarch64", "arm64") else "linux_x64"
    raise RuntimeError(f"Unsupported host platform: {sysname}/{arch}")


def fetch_grpc_tools(workdir: Path) -> tuple[Path, Path, Path]:
    """Download Grpc.Tools and extract protoc + grpc_csharp_plugin + well-known-types include dir.

    Returns (protoc_path, plugin_path, wkt_include_dir).
    """
    url   = nupkg_url("grpc.tools", GRPC_TOOLS_VERSION)
    data  = download(url)
    plat  = host_grpc_tools_platform()
    is_win = platform.system().lower() == "windows"
    exe    = ".exe" if is_win else ""

    protoc_inner = f"tools/{plat}/protoc{exe}"
    plugin_inner = f"tools/{plat}/grpc_csharp_plugin{exe}"

    protoc_path = workdir / f"protoc{exe}"
    plugin_path = workdir / f"grpc_csharp_plugin{exe}"
    extract_dll(data, protoc_inner, protoc_path)
    extract_dll(data, plugin_inner, plugin_path)

    if not is_win:
        protoc_path.chmod(0o755)
        plugin_path.chmod(0o755)

    # Extract the well-known-types include directory (google/protobuf/*.proto)
    # so simulation.proto can reference google.protobuf.Struct.
    wkt_dir = workdir / "include"
    wkt_dir.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(io.BytesIO(data)) as zf:
        prefix = "build/native/include/"
        for name in zf.namelist():
            n = name.replace("\\", "/")
            if n.lower().startswith(prefix) and n.endswith(".proto"):
                rel = n[len(prefix):]
                out_path = wkt_dir / rel
                out_path.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(name) as src, open(out_path, "wb") as dst:
                    shutil.copyfileobj(src, dst)

    return protoc_path, plugin_path, wkt_dir


def run_protoc(protoc: Path, plugin: Path, wkt_include: Path) -> None:
    GENERATED.mkdir(parents=True, exist_ok=True)
    log(f"protoc -> {GENERATED}")
    subprocess.run(
        [
            str(protoc),
            f"--proto_path={PROTO_FILE.parent}",
            f"--proto_path={wkt_include}",
            f"--csharp_out={GENERATED}",
            f"--grpc_out={GENERATED}",
            f"--plugin=protoc-gen-grpc={plugin}",
            str(PROTO_FILE),
        ],
        check=True,
    )


def vendor_runtime_dlls() -> None:
    PLUGINS_DIR.mkdir(parents=True, exist_ok=True)
    for pkg_id, version, inner_path, out_name in PINNED_PACKAGES:
        data = download(nupkg_url(pkg_id, version))
        extract_dll(data, inner_path, PLUGINS_DIR / out_name)
        log(f"  -> {PLUGINS_DIR / out_name}")


# ── Unity .meta files ─────────────────────────────────────────────────────────
#
# UPM packages must ship .meta files alongside every asset so that asset GUIDs
# are stable across all consuming projects. We synthesize them with stable
# (deterministic) GUIDs derived from the path of the asset relative to SDK_ROOT.

def stable_guid(path: Path) -> str:
    """Return a 32-char hex Unity GUID derived deterministically from a path."""
    rel = path.relative_to(SDK_ROOT).as_posix()
    digest = hashlib.md5(("biomata-sdk:" + rel).encode("utf-8")).hexdigest()
    return digest


_DLL_META_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 1
  validateReferences: 1
  platformData:
  - first:
      : Any
    second:
      enabled: 0
      settings:
        Exclude Editor: 0
        Exclude Linux64: 0
        Exclude OSXUniversal: 0
        Exclude Win: 0
        Exclude Win64: 0
        Exclude Android: 0
        Exclude iOS: 0
        Exclude WebGL: 1
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
        DefaultValueInitialized: true
        OS: AnyOS
  - first:
      Standalone: Linux64
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      Standalone: Win
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      Android: Android
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      iPhone: iOS
    second:
      enabled: 1
      settings:
        AddToEmbeddedBinaries: false
        CPU: AnyCPU
        CompileFlags:
        FrameworkDependencies:
  - first:
      WebGL: WebGL
    second:
      enabled: 0
      settings: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

_FOLDER_META_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

_SCRIPT_META_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
MonoImporter:
  externalObjects: {{}}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {{instanceID: 0}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def write_meta(path: Path, template: str) -> None:
    meta_path = path.with_suffix(path.suffix + ".meta")
    content = template.format(guid=stable_guid(path))
    meta_path.write_text(content, encoding="utf-8", newline="\n")


def write_metas_for_generated() -> None:
    """Write .meta files for every DLL plus the Plugins / Generated folders
    and the generated .cs files. Existing .meta files are kept so we don't
    churn GUIDs that Unity may already have linked from a consumer project.
    """
    targets: list[tuple[Path, str]] = []

    # Folder metas
    targets.append((PLUGINS_DIR,                                  _FOLDER_META_TEMPLATE))
    targets.append((GENERATED,                                    _FOLDER_META_TEMPLATE))

    # DLL metas
    for _, _, _, out_name in PINNED_PACKAGES:
        targets.append((PLUGINS_DIR / out_name, _DLL_META_TEMPLATE))

    # Generated .cs metas
    for cs in GENERATED.glob("*.cs"):
        targets.append((cs, _SCRIPT_META_TEMPLATE))

    for path, template in targets:
        meta_path = path.with_suffix(path.suffix + ".meta")
        if meta_path.exists():
            continue  # preserve existing GUIDs
        write_meta(path, template)
        log(f"  meta -> {meta_path.name}")


# ── Main ──────────────────────────────────────────────────────────────────────

def main() -> int:
    log(f"SDK root: {SDK_ROOT}")
    log(f"host: {platform.system()} {platform.machine()}")

    with tempfile.TemporaryDirectory(prefix="biomata-vendor-") as tmpdir:
        workdir = Path(tmpdir)
        protoc, plugin, wkt_include = fetch_grpc_tools(workdir)
        run_protoc(protoc, plugin, wkt_include)

    vendor_runtime_dlls()
    write_metas_for_generated()

    log("done. Generated C# in Runtime/Generated/, DLLs in Runtime/Plugins/.")
    log("Commit both directories (plus their .meta files) to ship a self-contained UPM package.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
