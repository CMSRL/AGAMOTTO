"""
Deprecated CoT middleman.

Unity Play now listens for WinTAK/ATAK CoT on TCP port 65432.
Start Unity yourself, wait for terrain Valid, then Send Map Data from the plugin
to the Unity machine (127.0.0.1 if local).

Optional: --replay-unity-data still pushes saved XML to that Unity listener.
"""

from __future__ import annotations

import argparse
import os
import sys
import time
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(SERVER_DIR))

import orchestrator_core as oc  # noqa: E402
from unity_data_loader import load_unity_data_scenario  # noqa: E402


def _print_direct_tak_instructions(port: int) -> None:
    print("Python orchestrator CoT middleman is retired.")
    print()
    print("  1. Open AGAMOTTO Simulation → BaseScene → Play.")
    print("  2. Wait until terrain status is Valid.")
    print(f"  3. In WinTAK: IP = Unity host (127.0.0.1 if local), Port = {port}")
    print("  4. Evaluation or SAC → Send Map Data to Listener.")
    print()
    print("Unity log should show: AGAMOTTO_CotTcpListener: listening on 0.0.0.0:" + str(port))


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Deprecated. Unity listens for CoT; this script no longer hosts the TAK socket."
    )
    parser.add_argument(
        "--replay-unity-data",
        action="store_true",
        help="Push Unity_Data CoT XML to the running Unity listener (no WinTAK)",
    )
    parser.add_argument(
        "--unity-data-dir",
        default=os.environ.get("AGAMOTTO_UNITY_DATA_DIR", "Unity_Data"),
        help="Folder with entities/ and routes/ subdirs",
    )
    parser.add_argument(
        "--host",
        default=os.environ.get("AGAMOTTO_COT_HOST", oc.DEFAULT_HOST),
        help="Unity CoT listener host (default 127.0.0.1)",
    )
    cli = parser.parse_args()
    port = oc.resolve_cot_port()

    _print_direct_tak_instructions(port)

    if not cli.replay_unity_data:
        return 0

    data_dir = Path(cli.unity_data_dir)
    if not data_dir.is_absolute():
        data_dir = (SERVER_DIR / data_dir).resolve()
    entries = load_unity_data_scenario(data_dir, dispatch_mode="Evaluation")
    print(f"Replaying {len(entries)} CoT file(s) from {data_dir} → {cli.host}:{port}")
    time.sleep(1.0)
    for ent in entries:
        xml = ent.get("cot_xml")
        if not xml:
            continue
        oc.send_cot_payload(cli.host, port, xml)
        print(f"  sent {ent.get('label', ent.get('uid', 'cot'))}")
        time.sleep(0.25)
    print("Unity_Data replay sent. Confirm Unity spawned units.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
