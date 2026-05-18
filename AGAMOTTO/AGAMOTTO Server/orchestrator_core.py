"""
Shared AGAMOTTO orchestration: Unity play-mode launch, terrain readiness, CoT TCP ingest.

Used by AGAMOTTO Orchestrator.py (interactive CoT server) and evaluation_suite.py (batch RL runs).
"""

from __future__ import annotations

import asyncio
import json
import os
import socket
import threading
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any, Callable, Dict, Optional

from XMLHelpers import parse_cot

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 65432


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
        print(line.decode(errors="replace").strip())
    return await process.wait()


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
) -> bool:
    path = status_path or default_status_json()
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        if path.exists():
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                if data.get("status") == "Valid":
                    return True
            except (json.JSONDecodeError, OSError):
                pass
        time.sleep(poll_s)
    return False


def send_cot_payload(host: str, port: int, xml_payload: str) -> None:
    data = xml_payload.encode("utf-8")
    header = len(data).to_bytes(4, byteorder="little")
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.connect((host, port))
        s.sendall(header + data)


def send_cot_file(host: str, port: int, xml_path: Path) -> None:
    send_cot_payload(host, port, xml_path.read_text(encoding="utf-8"))


def spawn_training_entities_from_scenario(
    scenario: Dict[str, Any],
    host: str = DEFAULT_HOST,
    port: int = DEFAULT_PORT,
    delay_s: float = 1.0,
    streaming_root: Optional[Path] = None,
) -> None:
    """Write entity XML to StreamingAssets and push over TCP so Unity creates units."""
    dirs = ensure_streaming_dirs(streaming_root)
    for ent in scenario.get("entities", []):
        uid = ent["uid"]
        xml_path = dirs["entities"] / f"{uid}.xml"
        xml_path.write_text(ent["cot_xml"], encoding="utf-8")
        time.sleep(delay_s)
        send_cot_file(host, port, xml_path)
        print(f"[orchestrator] spawned {ent.get('label', uid)} -> {host}:{port}")


class CotTcpServer:
    """Background CoT listener (same behavior as legacy AGAMOTTO Orchestrator start_server)."""

    def __init__(self, host: str = DEFAULT_HOST, port: int = DEFAULT_PORT, streaming_root: Optional[Path] = None):
        self.host = host
        self.port = port
        self.streaming_root = streaming_root
        self._entities: Dict[str, Dict[str, Any]] = {}
        self._thread: Optional[threading.Thread] = None
        self._stop = threading.Event()

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    s.settimeout(1.0)
                    s.bind((self.host, self.port))
                    s.listen(1)
                    print(f"[orchestrator] CoT server listening on {self.host}:{self.port}")
                    try:
                        conn, addr = s.accept()
                    except socket.timeout:
                        continue
                    with conn:
                        print(f"[orchestrator] Connected by {addr}")
                        self._handle_connection(conn)
            except OSError as ex:
                if not self._stop.is_set():
                    print(f"[orchestrator] CoT server error: {ex}")
                    time.sleep(1.0)

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
                save_xml("route", route_uid, raw_xml, self.streaming_root)
            else:
                entity_uid = parsed.get("uid") or "entity-unknown"
                raw_xml = ET.fromstring(data)
                save_xml("entity", entity_uid, raw_xml, self.streaming_root)
                uid, lat, lon = parsed.get("uid"), parsed.get("lat"), parsed.get("lon")
                if uid is not None and lat is not None and lon is not None:
                    self._entities[uid] = {
                        "uid": uid,
                        "lat": lat,
                        "lon": lon,
                        "callsign": parsed.get("callsign"),
                    }


def launch_unity_play_mode(
    unity_exe: str,
    project_path: str,
    scene_path: str,
    batchmode: bool = False,
) -> threading.Thread:
    return run_async_in_thread(
        play_unity_scene_async(unity_exe, project_path, scene_path, batchmode=batchmode)
    )
