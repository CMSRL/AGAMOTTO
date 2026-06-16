"""
Load CoT entity/route XML from AGAMOTTO Server/Unity_Data for manual replay (no WinTAK GUI).

Supports WinTAK-exported files wrapped as <root>&lt;event ...&gt;</root> with HTML-escaped inner XML.
"""

from __future__ import annotations

import html
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

SERVER_DIR = Path(__file__).resolve().parent
DEFAULT_UNITY_DATA_DIR = SERVER_DIR / "Unity_Data"


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
    now = _iso_now()
    cs = callsign or uid[:8]
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<event version="2.0" uid="{uid}" type="{cot_type}" how="m-g"
  time="{now}" start="{now}" stale="{now}" mode="{mode}">
  <point lat="{lat}" lon="{lon}" hae="{hae}" ce="30.0" le="30.0" />
  <detail>
    <contact callsign="{cs}" />
    <remarks>AGAMOTTO CoT replay</remarks>
  </detail>
</event>"""


def normalize_cot_xml(raw: str) -> str:
    """Return a bare <event>...</event> string from file contents."""
    text = raw.strip()
    if not text:
        raise ValueError("empty CoT file")
    try:
        root = ET.fromstring(text)
    except ET.ParseError:
        text = html.unescape(text)
        root = ET.fromstring(text)

    if root.tag == "event":
        return ET.tostring(root, encoding="unicode")

    if root.tag == "root":
        inner = (root.text or "").strip()
        if not inner and len(root):
            inner = ET.tostring(root[0], encoding="unicode")
        if inner:
            inner = html.unescape(inner)
            inner_root = ET.fromstring(inner)
            if inner_root.tag == "event":
                return ET.tostring(inner_root, encoding="unicode")

    text = html.unescape(text)
    root = ET.fromstring(text)
    if root.tag == "event":
        return ET.tostring(root, encoding="unicode")
    raise ValueError(f"unsupported CoT root tag: {root.tag}")


def _cot_type_team(cot_type: Optional[str]) -> Optional[str]:
    if not cot_type:
        return None
    parts = cot_type.split("-")
    if len(parts) < 2:
        return None
    seg = parts[1]
    if seg.startswith("f"):
        return "friendly"
    if seg.startswith("h"):
        return "hostile"
    return None


def apply_dispatch_mode(
    cot_xml: str,
    mode: str,
    *,
    training_friendly_limit: Optional[int] = None,
    friendly_training_count: List[int] | None = None,
) -> str:
    """Set event mode=Training|Evaluation|Hybrid."""
    event = ET.fromstring(cot_xml)
    cot_type = event.attrib.get("type", "")
    team = _cot_type_team(cot_type)
    if cot_type.endswith("-r"):
        effective_mode = mode if str(mode).lower() == "hybrid" else "Evaluation"
    elif team == "hostile":
        effective_mode = "Evaluation"
    elif team == "friendly":
        effective_mode = mode
        if training_friendly_limit is not None:
            if friendly_training_count is None:
                friendly_training_count = [0]
            if friendly_training_count[0] >= training_friendly_limit:
                effective_mode = "Evaluation"
            else:
                friendly_training_count[0] += 1
    else:
        effective_mode = "Evaluation"
    event.set("mode", effective_mode)
    return ET.tostring(event, encoding="unicode")


def load_cot_file(path: Path, mode: str, **mode_kwargs) -> Dict[str, Any]:
    cot_xml = normalize_cot_xml(path.read_text(encoding="utf-8"))
    cot_xml = apply_dispatch_mode(cot_xml, mode, **mode_kwargs)
    event = ET.fromstring(cot_xml)
    uid = event.attrib.get("uid") or path.stem
    cot_type = event.attrib.get("type", "")
    return {
        "label": path.stem,
        "uid": uid,
        "cot_type": cot_type,
        "team": _cot_type_team(cot_type),
        "kind": "route" if cot_type and cot_type.endswith("-r") else "entity",
        "source": str(path),
        "cot_xml": cot_xml,
    }


def load_unity_data_scenario(
    data_dir: Path,
    *,
    dispatch_mode: str = "Training",
    inject_hostile_if_missing: bool = True,
    hostile_lat: float = 35.335081,
    hostile_lon: float = -116.5160077,
    hostile_hae: float = 0.0,
    training_friendly_limit: Optional[int] = 1,
    include_routes: bool = True,
) -> List[Dict[str, Any]]:
    """Build ordered spawn list: hostiles, friendlies, optional routes."""
    data_dir = data_dir.resolve()
    if (data_dir / "entities").is_dir():
        entities_dir = data_dir / "entities"
        routes_dir = data_dir / "routes"
    else:
        entities_dir = data_dir
        routes_dir = data_dir.parent / "routes"

    if not entities_dir.is_dir():
        raise FileNotFoundError(f"entities folder not found: {entities_dir}")

    friendly_count = [0]
    mode_kwargs = {
        "training_friendly_limit": training_friendly_limit,
        "friendly_training_count": friendly_count,
    }

    entries: List[Dict[str, Any]] = []
    for path in sorted(entities_dir.glob("*.xml")):
        item = load_cot_file(path, dispatch_mode, **mode_kwargs)
        if item.get("kind") == "entity":
            entries.append(item)

    has_hostile = any(e.get("team") == "hostile" for e in entries)
    if inject_hostile_if_missing and not has_hostile:
        hostile_uid = str(uuid.uuid4())
        entries.insert(
            0,
            {
                "label": "synthetic_hostile",
                "uid": hostile_uid,
                "cot_type": "a-h-G-U-C",
                "team": "hostile",
                "kind": "entity",
                "source": "synthetic",
                "cot_xml": build_entity_cot(
                    uid=hostile_uid,
                    cot_type="a-h-G-U-C",
                    lat=hostile_lat,
                    lon=hostile_lon,
                    hae=hostile_hae,
                    mode=dispatch_mode,
                ),
            },
        )

    hostiles = [e for e in entries if e.get("team") == "hostile"]
    friendlies = [e for e in entries if e.get("team") == "friendly"]
    routes: List[Dict[str, Any]] = []
    if include_routes and routes_dir.is_dir():
        for path in sorted(routes_dir.glob("*.xml")):
            routes.append(load_cot_file(path, dispatch_mode))

    return hostiles + friendlies + routes
