"""
Shared AGAMOTTO orchestration: Unity play-mode launch, terrain readiness, CoT TCP ingest.

Used by AGAMOTTO Orchestrator.py (interactive CoT server and optional Unity_Data replay).
"""

from __future__ import annotations

import asyncio
import json
import os
import re
import subprocess
import socket
import threading
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any, Callable, Dict, Optional

from XMLHelpers import parse_cot

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 65432
# If 65432 fails on Windows (WinError 10013 / excluded range), try these in order.
COT_PORT_FALLBACKS = (65432, 55032, 165432)


def resolve_cot_port(explicit: Optional[int] = None) -> int:
    if explicit is not None:
        return int(explicit)
    env = os.environ.get("AGAMOTTO_COT_PORT")
    if env:
        return int(env)
    return DEFAULT_PORT


def format_bind_error(host: str, port: int, ex: OSError) -> str:
    return (
        f"Cannot bind CoT server to {host}:{port}: {ex}\n"
        "  • Close any other AGAMOTTO Orchestrator process.\n"
        "  • In PowerShell: netstat -ano | findstr :{port}\n"
        "  • Try another port: $env:AGAMOTTO_COT_PORT = \"55032\" "
        "(set the same port in the WinTAK plugin Orchestrator field).\n"
        "  • Check Windows excluded ports: "
        "netsh int ipv4 show excludedportrange protocol=tcp"
    ).format(port=port)


def repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def default_unity_project() -> Path:
    return repo_root() / "AGAMOTTO Simulation"


def default_streaming_assets() -> Path:
    return default_unity_project() / "Assets" / "StreamingAssets"


def default_status_json() -> Path:
    return default_unity_project() / "Assets" / "status.json"


def read_project_unity_version(project_path: Optional[Path] = None) -> Optional[str]:
    """Read m_EditorVersion from ProjectSettings/ProjectVersion.txt."""
    project = project_path or default_unity_project()
    version_file = project / "ProjectSettings" / "ProjectVersion.txt"
    if not version_file.is_file():
        return None
    for line in version_file.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith("m_EditorVersion:"):
            return line.split(":", 1)[1].strip()
    return None


def hub_unity_exe_for_version(version: str) -> Path:
    return Path(r"C:\Program Files\Unity\Hub\Editor") / version / "Editor" / "Unity.exe"


def list_hub_unity_versions() -> list[str]:
    hub = Path(r"C:\Program Files\Unity\Hub\Editor")
    if not hub.is_dir():
        return []
    return sorted(
        p.name
        for p in hub.iterdir()
        if p.is_dir() and (p / "Editor" / "Unity.exe").is_file()
    )


def resolve_unity_exe(
    project_path: Optional[Path] = None,
    explicit: Optional[str] = None,
) -> str:
    """
    Pick Unity.exe matching the project (ProjectVersion.txt), unless overridden.

    Order: explicit arg -> AGAMOTTO_UNITY_EXE env -> Hub path for project version.
    """
    if explicit:
        return str(Path(explicit).resolve())
    env = os.environ.get("AGAMOTTO_UNITY_EXE")
    if env:
        return str(Path(env).resolve())

    project = Path(project_path or default_unity_project()).resolve()
    version = read_project_unity_version(project)
    if version:
        candidate = hub_unity_exe_for_version(version)
        if candidate.is_file():
            return str(candidate)

    installed = list_hub_unity_versions()
    hint = (
        f"Install Unity {version} via Unity Hub, or set AGAMOTTO_UNITY_EXE."
        if version
        else "Set AGAMOTTO_UNITY_EXE to your Unity.exe path."
    )
    raise FileNotFoundError(
        f"Unity editor not found for project version {version!r} at "
        f"{hub_unity_exe_for_version(version or '?')}. "
        f"Installed Hub editors: {installed or '(none)'}. {hint}"
    )


def ensure_streaming_dirs(streaming_root: Optional[Path] = None) -> Dict[str, Path]:
    root = streaming_root or default_streaming_assets()
    ent = root / "entities"
    route = root / "routes"
    eval_dir = root / "evaluation"
    ent.mkdir(parents=True, exist_ok=True)
    route.mkdir(parents=True, exist_ok=True)
    eval_dir.mkdir(parents=True, exist_ok=True)
    return {"streaming": root, "entities": ent, "routes": route, "evaluation": eval_dir}


def write_active_run_config(overrides: Dict[str, Any], streaming_root: Optional[Path] = None) -> Path:
    dirs = ensure_streaming_dirs(streaming_root)
    path = dirs["evaluation"] / "active_run.json"
    payload = {"version": 1, **overrides}
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    return path


def clear_active_run_config(streaming_root: Optional[Path] = None) -> None:
    dirs = ensure_streaming_dirs(streaming_root)
    path = dirs["evaluation"] / "active_run.json"
    if path.exists():
        path.unlink()


def save_xml(kind: str, uid: str, xml_data: ET.Element, streaming_root: Optional[Path] = None) -> Path:
    dirs = ensure_streaming_dirs(streaming_root)
    base = dirs["entities"] if kind == "entity" else dirs["routes"]
    out_path = base / f"{uid}.xml"
    ET.ElementTree(xml_data).write(out_path, encoding="utf-8", xml_declaration=True)
    return out_path


def try_link_route_to_entity(route: Dict[str, Any], entities: Dict[str, Dict[str, Any]]) -> Optional[str]:
    verts = route.get("verticies") or []
    if not verts:
        return None
    first = verts[0]
    r_lat = f"{float(first.get('lat')):.5f}" if first.get("lat") is not None else None
    r_lon = f"{float(first.get('lon')):.5f}" if first.get("lon") is not None else None
    if r_lat is None or r_lon is None:
        return None
    for ent in entities.values():
        e_lat, e_lon = ent.get("lat"), ent.get("lon")
        if e_lat is None or e_lon is None:
            continue
        if f"{float(e_lat):.5f}" == r_lat and f"{float(e_lon):.5f}" == r_lon:
            return ent["uid"]
    return None


async def play_unity_scene_async(
    unity_exe: str,
    project_path: str,
    scene_path: Optional[str] = None,
    batchmode: bool = False,
    extra_args: Optional[list] = None,
    *,
    minimal_logs: bool = False,
) -> int:
    cmd = [
        unity_exe,
        "-projectPath",
        project_path,
        "-executeMethod",
        "PlaySceneCommand.PlayScene",
        "-logFile",
        "-",
    ]
    if scene_path:
        cmd += ["-scenePath", scene_path]
    if batchmode:
        cmd += ["-batchmode", "-nographics"]
    if extra_args:
        cmd += extra_args

    process = await asyncio.create_subprocess_exec(
        *cmd,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
    )
    async for line in process.stdout:
        text = line.decode(errors="replace").rstrip("\r\n")
        if minimal_logs:
            text = _collapse_whitespace(text)
            if not should_emit_unity_log_line(text):
                continue
        print(f"[unity] {text}", flush=True)
    return await process.wait()


_UNITY_NOISE_MARKERS = (
    "unityengine.",
    "unityeditor.",
    " (at ",
    "setupcoroutine",
    "invokemovenext",
    "tick tree",
    "assetdatabase.",
    "garbagecollector",
    "used heap",
    "[physx]",
    "shader ",
    "d3d12:",
    "compute shader",
    "gi: ",
    "licensing::",
)

_UNITY_SUPPRESS_MARKERS = (
    "calculatepath failed",
    "calculate path failed",
)

_UNITY_KEEP_MARKERS = (
    "agamotto",
    "[exportdata]",
    "ml-agents",
    "mlagents",
    "academy",
    "terrain",
    "navmesh",
    "cot",
    "spawn",
    "episode",
    "error",
    "warning",
    "exception",
    "failed",
    "valid",
    "enemy",
    "squad",
    "connect",
    "behavior",
)


def _collapse_whitespace(text: str) -> str:
    return " ".join(text.split())


def should_emit_unity_log_line(line: str) -> bool:
    """Keep high-signal Unity lines when unity_log_minimal is enabled."""
    s = _collapse_whitespace(line)
    if not s:
        return False
    low = s.lower()
    if any(m in low for m in _UNITY_SUPPRESS_MARKERS):
        return False
    if re.search(r"\b(error|exception|assertion failed)\b", low):
        if "unityengine.debug:logerror" in low or " (at " in low:
            return False
        return True
    if any(n in low for n in _UNITY_NOISE_MARKERS):
        return False
    if any(k in low for k in _UNITY_KEEP_MARKERS):
        return True
    if re.match(r"^[\w.]+:\w+\(.*\)$", s):
        return False
    return False


def run_async_in_thread(coro) -> threading.Thread:
    def thread_target():
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        loop.run_until_complete(coro)

    t = threading.Thread(target=thread_target, daemon=True)
    t.start()
    return t


def wait_for_terrain_valid(
    status_path: Optional[Path] = None,
    timeout_s: float = 600.0,
    poll_s: float = 2.0,
    *,
    min_mtime: Optional[float] = None,
    log_progress: bool = True,
) -> bool:
    """Wait until status.json reports Valid.

    Pass min_mtime (epoch seconds) to ignore a leftover Valid from a previous Unity run.
    """
    path = status_path or default_status_json()
    deadline = time.time() + timeout_s
    started = time.time()
    last_log = started
    while time.time() < deadline:
        if path.exists():
            try:
                if min_mtime is not None and path.stat().st_mtime < min_mtime:
                    pass
                else:
                    data = json.loads(path.read_text(encoding="utf-8"))
                    if data.get("status") == "Valid":
                        if log_progress:
                            elapsed = time.time() - started
                            print(
                                f"[orchestrator] terrain Valid ({elapsed:.1f}s, mtime ok)",
                                flush=True,
                            )
                        return True
            except (json.JSONDecodeError, OSError):
                pass
        now = time.time()
        if log_progress and now - last_log >= 15.0:
            print(
                f"[orchestrator] waiting for terrain Valid... ({now - started:.0f}s)",
                flush=True,
            )
            last_log = now
        time.sleep(poll_s)
    if log_progress:
        print(f"[orchestrator] terrain not Valid within {timeout_s}s", flush=True)
    return False


def send_cot_payload(host: str, port: int, xml_payload: str) -> None:
    data = xml_payload.encode("utf-8")
    header = len(data).to_bytes(4, byteorder="little")
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.connect((host, port))
        s.sendall(header + data)


def send_cot_file(host: str, port: int, xml_path: Path) -> None:
    send_cot_payload(host, port, xml_path.read_text(encoding="utf-8"))


def spawn_cot_entries(
    entries: list,
    host: str = DEFAULT_HOST,
    port: int = DEFAULT_PORT,
    delay_s: float = 1.0,
    streaming_root: Optional[Path] = None,
    *,
    send_tcp: bool = True,
    persist_tcp_to_streaming: bool = True,
) -> None:
    """Write CoT spawn dicts to StreamingAssets and optionally push over TCP."""
    dirs = ensure_streaming_dirs(streaming_root)

    for ent in entries:
        uid = ent["uid"]
        kind = ent.get("kind", "entity")
        cot_xml = ent["cot_xml"]

        subdir = dirs["routes"] if kind == "route" else dirs["entities"]
        xml_path = subdir / f"{uid}.xml"

        xml_path.parent.mkdir(parents=True, exist_ok=True)
        xml_path.write_text(cot_xml, encoding="utf-8")
        time.sleep(delay_s)

        if send_tcp:
            send_cot_payload(host, port, cot_xml)
        print(
            f"[orchestrator] spawned {ent.get('label', uid)} ({kind}) "
            f"-> {xml_path.name}"
            + (f" (+TCP {host}:{port})" if send_tcp else "")
        )

        if send_tcp and not persist_tcp_to_streaming:
            pass  # payload sent; CotTcpServer should not duplicate-save (see persist_streaming_files)


class CotTcpServer:
    """Background CoT listener (same behavior as legacy AGAMOTTO Orchestrator start_server)."""

    def __init__(
        self,
        host: str = DEFAULT_HOST,
        port: Optional[int] = None,
        streaming_root: Optional[Path] = None,
        port_fallbacks: Optional[tuple] = None,
        persist_streaming_files: bool = True,
    ):
        self.host = host
        self.port = resolve_cot_port(port)
        self._port_fallbacks = port_fallbacks or COT_PORT_FALLBACKS
        self.streaming_root = streaming_root
        self.persist_streaming_files = persist_streaming_files
        self._entities: Dict[str, Dict[str, Any]] = {}
        self._thread: Optional[threading.Thread] = None
        self._stop = threading.Event()
        self._listen_socket: Optional[socket.socket] = None

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._listen_socket is not None:
            try:
                self._listen_socket.close()
            except OSError:
                pass
            self._listen_socket = None

    def _try_bind(self, host: str, port: int) -> socket.socket:
        last_error: Optional[OSError] = None
        for bind_host in (host, "0.0.0.0"):
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            try:
                sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                sock.bind((bind_host, port))
                sock.listen(8)
                sock.settimeout(1.0)
                if bind_host != host:
                    print(f"[orchestrator] CoT bind fallback: listening on {bind_host}:{port}")
                return sock
            except OSError as ex:
                last_error = ex
                sock.close()
        assert last_error is not None
        raise last_error

    def _open_listener(self) -> socket.socket:
        ports = []
        for p in (self.port, *self._port_fallbacks):
            if p not in ports:
                ports.append(p)
        last_error: Optional[OSError] = None
        for port in ports:
            try:
                sock = self._try_bind(self.host, port)
                self.port = port
                return sock
            except OSError as ex:
                last_error = ex
                print(format_bind_error(self.host, port, ex))
        assert last_error is not None
        raise last_error

    def _run(self) -> None:
        try:
            self._listen_socket = self._open_listener()
        except OSError:
            return

        print(f"[orchestrator] CoT server listening on {self.host}:{self.port}")
        listen_sock = self._listen_socket
        while not self._stop.is_set():
            try:
                conn, addr = listen_sock.accept()
            except socket.timeout:
                continue
            except OSError as ex:
                if not self._stop.is_set():
                    print(f"[orchestrator] CoT accept error: {ex}")
                    time.sleep(1.0)
                continue
            with conn:
                print(f"[orchestrator] Connected by {addr}")
                self._handle_connection(conn)
        if listen_sock is not None:
            try:
                listen_sock.close()
            except OSError:
                pass

    def _handle_connection(self, conn: socket.socket) -> None:
        while not self._stop.is_set():
            size_bytes = conn.recv(4)
            if not size_bytes:
                break
            size_number = int.from_bytes(size_bytes, byteorder="little")
            data = conn.recv(size_number).decode("utf-8")
            if not data:
                break
            parsed = parse_cot(data)
            if not parsed:
                print("[orchestrator] WARN could not parse CoT payload")
                continue
            if parsed.get("__normalized_type__") == "route":
                raw_xml = ET.fromstring(data)
                route_uid = parsed.get("uid") or "route-unknown"
                linked_uid = try_link_route_to_entity(parsed.get("route", {}), self._entities)
                contact = raw_xml.find(".//contact")
                if contact is not None and linked_uid:
                    contact.set("callsign", linked_uid)
                if self.persist_streaming_files:
                    save_xml("route", route_uid, raw_xml, self.streaming_root)
            else:
                entity_uid = parsed.get("uid") or "entity-unknown"
                raw_xml = ET.fromstring(data)
                if self.persist_streaming_files:
                    save_xml("entity", entity_uid, raw_xml, self.streaming_root)
                uid, lat, lon = parsed.get("uid"), parsed.get("lat"), parsed.get("lon")
                if uid is not None and lat is not None and lon is not None:
                    self._entities[uid] = {
                        "uid": uid,
                        "lat": lat,
                        "lon": lon,
                        "callsign": parsed.get("callsign"),
                    }


def build_unity_play_command(
    unity_exe: str,
    project_path: str,
    scene_path: Optional[str] = None,
    batchmode: bool = False,
    extra_args: Optional[list] = None,
) -> list:
    cmd = [
        unity_exe,
        "-projectPath",
        project_path,
        "-executeMethod",
        "PlaySceneCommand.PlayScene",
        "-logFile",
        "-",
    ]
    if scene_path:
        cmd += ["-scenePath", scene_path]
    if batchmode:
        cmd += ["-batchmode", "-nographics"]
    if extra_args:
        cmd += extra_args
    return cmd


def launch_unity_play_mode(
    unity_exe: str,
    project_path: str,
    scene_path: str,
    batchmode: bool = False,
    minimal_logs: bool = False,
) -> threading.Thread:
    return run_async_in_thread(
        play_unity_scene_async(
            unity_exe,
            project_path,
            scene_path,
            batchmode=batchmode,
            minimal_logs=minimal_logs,
        )
    )
