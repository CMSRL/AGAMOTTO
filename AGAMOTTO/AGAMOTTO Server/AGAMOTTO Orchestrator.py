"""
Interactive AGAMOTTO orchestrator: launch Unity BaseScene, wait for terrain, then serve CoT on TCP.

Use --replay-unity-data to push CoT from Unity_Data (no WinTAK GUI).
"""

from __future__ import annotations

import argparse
import asyncio
import os
import sys
import time
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(SERVER_DIR))

import orchestrator_core as oc  # noqa: E402
from unity_data_loader import load_unity_data_scenario  # noqa: E402


def _env_path(key: str, default: Path) -> str:
    return os.environ.get(key, str(default))


async def main(replay_unity_data: bool = False, unity_data_dir: str = "Unity_Data") -> None:
    project_path = Path(_env_path("AGAMOTTO_UNITY_PROJECT", oc.default_unity_project()))
    unity_exe = oc.resolve_unity_exe(project_path)
    scene_path = os.environ.get("AGAMOTTO_SCENE_PATH", r"Assets\Scenes\BaseScene.unity")
    status_path = project_path / "Assets" / "status.json"
    project_version = oc.read_project_unity_version(project_path)

    print(f"[orchestrator] Project version: {project_version}")
    print(f"[orchestrator] Unity: {unity_exe}")
    print(f"[orchestrator] Project: {project_path}")
    #oc.launch_unity_play_mode(unity_exe, str(project_path), scene_path, batchmode=False)

    if not oc.wait_for_terrain_valid(status_path):
        print("[orchestrator] ERROR: terrain never reached Valid", file=sys.stderr)
        return

    cot_port = oc.resolve_cot_port()
    print(f"[orchestrator] Terrain Valid — starting CoT TCP server on port {cot_port} (Ctrl+C to stop)")
    print("[orchestrator] Override port: $env:AGAMOTTO_COT_PORT = \"55032\" (match WinTAK plugin port)")
    streaming = project_path / "Assets" / "StreamingAssets"
    server = oc.CotTcpServer(port=cot_port, streaming_root=streaming)
    server.start()

    if replay_unity_data:
        data_dir = Path(unity_data_dir)
        if not data_dir.is_absolute():
            data_dir = (SERVER_DIR / data_dir).resolve()
        oc.write_active_run_config(
            {"runId": "orchestrator_unity_data", "sendTrainingFeedbackToTak": False},
            streaming,
        )
        entries = load_unity_data_scenario(data_dir, dispatch_mode="Training")
        print(f"[orchestrator] Replaying {len(entries)} CoT file(s) from {data_dir}")
        time.sleep(3.0)
        oc.spawn_cot_entries(entries, port=cot_port, streaming_root=streaming)
        print("[orchestrator] Unity_Data replay complete (WinTAK not required)")

    while True:
        await asyncio.sleep(3600)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="AGAMOTTO Unity + CoT orchestrator")
    parser.add_argument(
        "--replay-unity-data",
        action="store_true",
        help="After terrain loads, spawn entities from Unity_Data over TCP (no WinTAK)",
    )
    parser.add_argument(
        "--unity-data-dir",
        default=os.environ.get("AGAMOTTO_UNITY_DATA_DIR", "Unity_Data"),
        help="Folder with entities/ and routes/ subdirs",
    )
    cli = parser.parse_args()
    asyncio.run(main(replay_unity_data=cli.replay_unity_data, unity_data_dir=cli.unity_data_dir))
