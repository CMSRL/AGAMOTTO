"""Build CoT XML payloads for scripted AGAMOTTO training / evaluation spawns."""

from __future__ import annotations

import uuid
from datetime import datetime, timezone
from typing import List, Tuple


def _iso_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def build_entity_cot(
    *,
    uid: str,
    cot_type: str,
    lat: float,
    lon: float,
    hae: float = 0.0,
    mode: str = "Training",
    callsign: str | None = None,
) -> str:
    """CoT event XML matching AGAMOTTOScenarioCreator.ParseCotAndCreateEntity expectations."""
    now = _iso_now()
    cs = callsign or uid[:8]
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<event version="2.0" uid="{uid}" type="{cot_type}" how="m-g"
  time="{now}" start="{now}" stale="{now}" mode="{mode}">
  <point lat="{lat}" lon="{lon}" hae="{hae}" ce="30.0" le="30.0" />
  <detail>
    <contact callsign="{cs}" />
    <remarks>AGAMOTTO evaluation suite</remarks>
  </detail>
</event>"""


def default_atd_scenario(
    friendly_lat: float = 35.331081,
    friendly_lon: float = -116.5210077,
    hostile_lat: float = 35.335081,
    hostile_lon: float = -116.5160077,
) -> List[dict]:
    """Hostile first, then friendly (Training mode) so RL can attach after enemies exist."""
    hostile_uid = str(uuid.uuid4())
    friendly_uid = str(uuid.uuid4())
    return [
        {
            "label": "hostile_squad",
            "uid": hostile_uid,
            "cot_xml": build_entity_cot(
                uid=hostile_uid,
                cot_type="a-h-G-U-C",
                lat=hostile_lat,
                lon=hostile_lon,
                mode="Evaluation",
            ),
        },
        {
            "label": "friendly_squad",
            "uid": friendly_uid,
            "cot_xml": build_entity_cot(
                uid=friendly_uid,
                cot_type="a-f-G-U-C",
                lat=friendly_lat,
                lon=friendly_lon,
                mode="Training",
            ),
        },
    ]
