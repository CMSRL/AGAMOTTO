"""
Interactive AGAMOTTO orchestrator: launch Unity BaseScene, wait for terrain, then serve CoT on TCP 65432.

For batch training + ablations use evaluation_suite.py (same orchestrator_core module).
"""

from __future__ import annotations

import asyncio
import os
import sys
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(SERVER_DIR))

import orchestrator_core as oc  # noqa: E402


def _env_path(key: str, default: Path) -> str:
    return os.environ.get(key, str(default))


async def main() -> None:
    project_path = Path(_env_path("AGAMOTTO_UNITY_PROJECT", oc.default_unity_project()))
    unity_exe = oc.resolve_unity_exe(project_path)
    scene_path = os.environ.get("AGAMOTTO_SCENE_PATH", r"Assets\Scenes\BaseScene.unity")
    status_path = project_path / "Assets" / "status.json"
    project_version = oc.read_project_unity_version(project_path)

    print(f"[orchestrator] Project version: {project_version}")
    print(f"[orchestrator] Unity: {unity_exe}")
    print(f"[orchestrator] Project: {project_path}")
    oc.launch_unity_play_mode(unity_exe, str(project_path), scene_path, batchmode=False)

    if not oc.wait_for_terrain_valid(status_path):
        print("[orchestrator] ERROR: terrain never reached Valid", file=sys.stderr)
        return

    print("[orchestrator] Terrain Valid — starting CoT TCP server (Ctrl+C to stop)")
    server = oc.CotTcpServer()
    server.start()
    while True:
        await asyncio.sleep(3600)


if __name__ == "__main__":
    asyncio.run(main())
