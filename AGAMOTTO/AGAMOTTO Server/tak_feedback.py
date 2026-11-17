"""Send JSON feedback to the AGAMOTTO WinTAK plugin (127.0.0.1:65433)."""

from __future__ import annotations

import json
import socket
from typing import Any, Dict, List, Optional

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 65433
MAX_PAYLOAD_BYTES = 10_000_000


def send_json(payload: Dict[str, Any], host: str = DEFAULT_HOST, port: int = DEFAULT_PORT) -> None:
    body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    if len(body) > MAX_PAYLOAD_BYTES:
        raise ValueError(f"payload too large ({len(body)} bytes)")
    header = len(body).to_bytes(4, "little")
    with socket.create_connection((host, port), timeout=5) as sock:
        sock.sendall(header + body)


def send_training_complete(
    trainer: str,
    run_id: str,
    total_steps: int,
    *,
    checkpoint_path: str = "",
    host: str = DEFAULT_HOST,
    port: int = DEFAULT_PORT,
) -> None:
    send_json(
        {
            "kind": "training_complete",
            "trainer": trainer,
            "runId": run_id,
            "totalSteps": total_steps,
            "checkpointPath": checkpoint_path,
        },
        host=host,
        port=port,
    )


def send_inference_complete(
    trainer: str,
    run_id: str,
    mission_success: bool,
    outcome: str,
    step_count: int,
    cumulative_reward: float,
    route_points: Optional[List[Dict[str, float]]] = None,
    leader_callsign: str = "leader",
    route_name: str = "Policy route",
    *,
    host: str = DEFAULT_HOST,
    port: int = DEFAULT_PORT,
) -> None:
    send_json(
        {
            "kind": "inference_complete",
            "trainer": trainer,
            "runId": run_id,
            "missionSuccess": mission_success,
            "outcome": outcome,
            "stepCount": step_count,
            "cumulativeReward": cumulative_reward,
            "leaderCallsign": leader_callsign,
            "routeName": route_name,
            "routePoints": route_points or [],
        },
        host=host,
        port=port,
    )
