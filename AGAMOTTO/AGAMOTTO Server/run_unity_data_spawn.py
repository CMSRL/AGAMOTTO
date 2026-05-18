#!/usr/bin/env python3
"""
Smoke-test Unity spawns from Unity_Data CoT without WinTAK or mlagents-learn.

  python run_unity_data_spawn.py
  python run_unity_data_spawn.py --data-dir Unity_Data --mode Training
"""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(SERVER_DIR))

import orchestrator_core as oc  # noqa: E402
from evaluation.unity_data_loader import load_unity_data_scenario  # noqa: E402


def main() -> int:
    p = argparse.ArgumentParser(description="Spawn Unity_Data CoT into running Unity (no WinTAK)")
    p.add_argument("--data-dir", default="Unity_Data", help="CoT entities/routes folder")
    p.add_argument("--mode", default="Training", choices=["Training", "Evaluation", "Hybrid"])
    p.add_argument("--no-launch-unity", action="store_true", help="Only push CoT; Unity already in play")
    p.add_argument("--spawn-delay", type=float, default=2.0)
    args = p.parse_args()

    project = oc.default_unity_project()
    streaming = project / "Assets" / "StreamingAssets"
    data_dir = Path(args.data_dir)
    if not data_dir.is_absolute():
        data_dir = (SERVER_DIR / data_dir).resolve()

    entries = load_unity_data_scenario(data_dir, dispatch_mode=args.mode)
    print(f"[spawn] {len(entries)} CoT message(s) from {data_dir}")
    for e in entries:
        print(f"  - {e.get('label')} team={e.get('team')} type={e.get('cot_type')} src={e.get('source')}")

    oc.write_active_run_config(
        {"runId": "unity_data_spawn", "sendTrainingFeedbackToTak": False},
        streaming,
    )

    cot_port = oc.resolve_cot_port()
    server = oc.CotTcpServer(port=cot_port, streaming_root=streaming)
    server.start()

    if not args.no_launch_unity:
        unity_exe = oc.resolve_unity_exe(project)
        oc.launch_unity_play_mode(unity_exe, str(project), "Assets/Scenes/BaseScene.unity")
        if not oc.wait_for_terrain_valid(project / "Assets" / "status.json"):
            print("[spawn] ERROR: terrain not Valid", file=sys.stderr)
            return 2
        print("[spawn] terrain Valid; waiting 15s...")
        time.sleep(15.0)

    oc.spawn_cot_entries(
        entries,
        port=cot_port,
        delay_s=args.spawn_delay,
        streaming_root=streaming,
    )
    print("[spawn] done")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
