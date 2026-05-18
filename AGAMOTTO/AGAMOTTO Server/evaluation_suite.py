#!/usr/bin/env python3
"""
AGAMOTTO evaluation suite: scripted Unity + CoT training runs and ablation matrix.

Coordinates:
  1. Unity play mode (orchestrator_core)
  2. Terrain readiness (Assets/status.json)
  3. Training CoT spawns (hostile then friendly)
  4. mlagents-learn per experiment YAML
  5. training_eval_summarize.py on TensorBoard output

Prerequisites:
  - Copy evaluation/paths.example.yaml -> evaluation/paths.local.yaml
  - ML-Agents Python env with mlagents-learn, tensorboard
  - Unity project opens BaseScene with ML-Agents Academy

Usage:
  cd "AGAMOTTO/AGAMOTTO Server"
  python evaluation_suite.py --paths evaluation/paths.local.yaml
  python evaluation_suite.py --only baseline_sac --dry-run
  python evaluation_suite.py --list
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

try:
    import yaml
except ImportError:
    print("Install PyYAML: pip install pyyaml", file=sys.stderr)
    sys.exit(1)

SERVER_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(SERVER_DIR))

from evaluation.cot_builder import default_atd_scenario  # noqa: E402
import orchestrator_core as oc  # noqa: E402


def load_yaml(path: Path) -> Dict[str, Any]:
    with path.open(encoding="utf-8") as f:
        return yaml.safe_load(f) or {}


def resolve_path(base: Path, value: str) -> Path:
    p = Path(value)
    return p if p.is_absolute() else (base / p).resolve()


def build_scenario(manifest: Dict[str, Any], scenario_name: str) -> List[dict]:
    scenarios = manifest.get("scenarios") or {}
    cfg = scenarios.get(scenario_name) or scenarios.get(manifest.get("default_scenario", "atd_default"), {})
    return default_atd_scenario(
        friendly_lat=float(cfg.get("friendly_lat", 35.331081)),
        friendly_lon=float(cfg.get("friendly_lon", -116.5210077)),
        hostile_lat=float(cfg.get("hostile_lat", 35.335081)),
        hostile_lon=float(cfg.get("hostile_lon", -116.5160077)),
    )


def unity_overrides_payload(exp: Dict[str, Any]) -> Dict[str, Any]:
    overrides = dict(exp.get("unity_overrides") or {})
    payload = {"runId": exp.get("run_id") or exp.get("id")}
    if "sendTrainingFeedbackToTak" in overrides:
        payload["sendTrainingFeedbackToTak"] = bool(overrides["sendTrainingFeedbackToTak"])
    if "rewardPerEnemySquadMemberKilled" in overrides:
        payload["rewardPerEnemySquadMemberKilled"] = float(overrides["rewardPerEnemySquadMemberKilled"])
    if "penaltyPerFriendlyWingmanDeath" in overrides:
        payload["penaltyPerFriendlyWingmanDeath"] = float(overrides["penaltyPerFriendlyWingmanDeath"])
    return payload


def find_latest_run_dir(results_dir: Path, behavior: str) -> Optional[Path]:
    """ML-Agents layout: <results_dir>/<behavior>/<run_id>/events..."""
    behavior_dir = results_dir / behavior
    if not behavior_dir.is_dir():
        # Sometimes run_id is directly under results_dir
        behavior_dir = results_dir
    candidates = []
    for root, _dirs, files in os.walk(behavior_dir):
        if any(f.startswith("events.out.tfevents") for f in files):
            candidates.append(Path(root))
    if not candidates:
        return None
    return max(candidates, key=lambda p: p.stat().st_mtime)


def run_summarize(python_exe: str, run_dir: Path, export_csv: Optional[Path]) -> Dict[str, Any]:
    script = SERVER_DIR / "training_eval_summarize.py"
    cmd = [python_exe, str(script), str(run_dir), "--json"]
    if export_csv:
        cmd += ["--export-csv", str(export_csv)]
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        return {"error": proc.stderr or proc.stdout, "returncode": proc.returncode}
    try:
        return json.loads(proc.stdout)
    except json.JSONDecodeError:
        return {"raw_stdout": proc.stdout, "raw_stderr": proc.stderr}


def run_mlagents_learn(
    mlagents_bin: str,
    trainer_config: Path,
    run_id: str,
    results_dir: Path,
    dry_run: bool,
) -> int:
    results_dir.mkdir(parents=True, exist_ok=True)
    cmd = [
        mlagents_bin,
        str(trainer_config),
        f"--run-id={run_id}",
        f"--results-dir={results_dir}",
        "--force",
    ]
    print("[eval] mlagents-learn:", " ".join(cmd))
    if dry_run:
        return 0
    env = os.environ.copy()
    env.setdefault("PYTHONUNBUFFERED", "1")
    proc = subprocess.run(cmd, cwd=str(SERVER_DIR), env=env)
    return proc.returncode


def run_experiment(
    exp: Dict[str, Any],
    paths: Dict[str, Any],
    manifest: Dict[str, Any],
    suite_results: Path,
    dry_run: bool,
    skip_train: bool,
) -> Dict[str, Any]:
    exp_id = exp["id"]
    run_id = exp.get("run_id") or exp_id
    behavior = manifest.get("behavior_name", "AGAMOTTO_RL")
    description = exp.get("description", "")

    server_base = SERVER_DIR
    unity_project = resolve_path(server_base, paths["unity_project"])
    unity_exe = oc.resolve_unity_exe(
        unity_project,
        explicit=paths.get("unity_exe"),
    )
    scene_path = paths.get("scene_path", "Assets/Scenes/BaseScene.unity")
    streaming = unity_project / "Assets" / "StreamingAssets"
    status_json = unity_project / "Assets" / "status.json"

    trainer_rel = exp.get("trainer_config") or paths.get("trainer_config_base")
    trainer_config = resolve_path(server_base, trainer_rel)

    cot_host = paths.get("cot_host", oc.DEFAULT_HOST)
    cot_port = int(paths.get("cot_port", oc.DEFAULT_PORT))
    terrain_timeout = float(paths.get("terrain_wait_timeout_s", 600))
    spawn_delay = float(paths.get("spawn_delay_s", 2.0))
    unity_grace = float(paths.get("unity_startup_grace_s", 15.0))

    exp_results = suite_results / exp_id
    exp_results.mkdir(parents=True, exist_ok=True)

    record: Dict[str, Any] = {
        "experiment_id": exp_id,
        "run_id": run_id,
        "description": description,
        "trainer_config": str(trainer_config),
        "results_dir": str(exp_results),
        "started_at": datetime.now(timezone.utc).isoformat(),
    }

    unity_payload = unity_overrides_payload(exp)
    oc.write_active_run_config(unity_payload, streaming)

    scenario_name = exp.get("scenario") or manifest.get("default_scenario", "atd_default")
    entities = build_scenario(manifest, scenario_name)
    record["scenario"] = scenario_name
    record["unity_overrides"] = unity_payload

    if dry_run:
        record["status"] = "dry_run"
        return record

    cot_server = oc.CotTcpServer(cot_host, cot_port, streaming)
    cot_server.start()

    unity_thread = oc.launch_unity_play_mode(unity_exe, str(unity_project), scene_path, batchmode=False)
    record["unity_launched"] = True

    if not oc.wait_for_terrain_valid(status_json, timeout_s=terrain_timeout):
        record["status"] = "failed"
        record["error"] = f"terrain not Valid within {terrain_timeout}s"
        return record

    print(f"[eval] terrain Valid; waiting {unity_grace}s before CoT spawns...")
    time.sleep(unity_grace)

    oc.spawn_training_entities_from_scenario(
        {"entities": entities},
        host=cot_host,
        port=cot_port,
        delay_s=spawn_delay,
        streaming_root=streaming,
    )

    time.sleep(5.0)

    if not skip_train:
        rc = run_mlagents_learn(
            paths.get("mlagents_learn", "mlagents-learn"),
            trainer_config,
            run_id,
            exp_results,
            dry_run=False,
        )
        record["mlagents_returncode"] = rc
        if rc != 0:
            record["status"] = "failed"
            record["error"] = f"mlagents-learn exited {rc}"
            return record

    run_dir = find_latest_run_dir(exp_results, behavior)
    if run_dir:
        csv_path = exp_results / f"{run_id}_episode_metrics.csv"
        summary = run_summarize(sys.executable, run_dir, csv_path)
        record["tensorboard_run_dir"] = str(run_dir)
        record["summarize"] = summary
        record["status"] = "completed"
    else:
        record["status"] = "completed_no_events"
        record["warning"] = "No TensorBoard events found under results dir"

    record["finished_at"] = datetime.now(timezone.utc).isoformat()
    return record


def write_suite_artifacts(suite_results: Path, rows: List[Dict[str, Any]], manifest: Dict[str, Any]) -> None:
    suite_results.mkdir(parents=True, exist_ok=True)
    json_path = suite_results / "suite_summary.json"
    json_path.write_text(json.dumps({"suite": manifest.get("suite_name"), "runs": rows}, indent=2), encoding="utf-8")

    csv_path = suite_results / "suite_summary.csv"
    fieldnames = [
        "experiment_id",
        "run_id",
        "status",
        "description",
        "mean_reward_last_20pct",
        "mean_enemies_killed_last_20pct",
        "mean_friendlies_lost_last_20pct",
    ]
    with csv_path.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fieldnames, extrasaction="ignore")
        w.writeheader()
        for row in rows:
            flat = {k: row.get(k) for k in ("experiment_id", "run_id", "status", "description")}
            summ = row.get("summarize") or {}
            if isinstance(summ, dict) and "error" not in summ:
                flat["mean_reward_last_20pct"] = summ.get("mean_return_last_k")
                ek = summ.get("enemies_killed") or {}
                fl = summ.get("friendlies_lost") or {}
                flat["mean_enemies_killed_last_20pct"] = ek.get("mean_last_k")
                flat["mean_friendlies_lost_last_20pct"] = fl.get("mean_last_k")
            w.writerow(flat)
    print(f"[eval] Wrote {json_path} and {csv_path}")


def main() -> int:
    parser = argparse.ArgumentParser(description="AGAMOTTO scripted training + ablation suite")
    parser.add_argument("--paths", default="evaluation/paths.local.yaml", help="Machine paths YAML")
    parser.add_argument("--manifest", default="evaluation/manifest.yaml", help="Experiment matrix YAML")
    parser.add_argument("--only", action="append", help="Run only these experiment ids (repeatable)")
    parser.add_argument("--dry-run", action="store_true", help="Print planned commands only")
    parser.add_argument("--list", action="store_true", help="List experiments and exit")
    parser.add_argument("--skip-train", action="store_true", help="Unity + CoT only; no mlagents-learn")
    parser.add_argument(
        "--suite-output",
        help="Shared results folder for a multi-experiment suite (set by parent re-invocation)",
    )
    parser.add_argument("--_child-run", dest="child_run", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()

    paths_path = resolve_path(SERVER_DIR, args.paths)
    manifest_path = resolve_path(SERVER_DIR, args.manifest)

    if not paths_path.exists():
        print(f"Missing paths file: {paths_path}", file=sys.stderr)
        print("Copy evaluation/paths.example.yaml to evaluation/paths.local.yaml", file=sys.stderr)
        return 2

    paths = load_yaml(paths_path)
    manifest = load_yaml(manifest_path)
    experiments = manifest.get("experiments") or []

    if args.list:
        for exp in experiments:
            print(f"{exp['id']}: {exp.get('description', '')}")
        return 0

    only = set(args.only or [])
    if only:
        experiments = [e for e in experiments if e["id"] in only]
        if not experiments:
            print(f"No experiments matched --only {only}", file=sys.stderr)
            return 2

    suite_name = manifest.get("suite_name", "agamotto_suite")
    results_root = resolve_path(SERVER_DIR, paths.get("results_root", "evaluation/results"))
    if args.suite_output:
        suite_results = Path(args.suite_output)
    else:
        suite_results = results_root / suite_name / datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")

    # Each experiment gets its own Unity + mlagents-learn process (re-invoke this script).
    if len(experiments) > 1 and not args.child_run and not args.dry_run:
        suite_results.mkdir(parents=True, exist_ok=True)
        print(f"[eval] Suite '{suite_name}' -> {suite_results} (isolated subprocess per experiment)")
        rows: List[Dict[str, Any]] = []
        for exp in experiments:
            cmd = [
                sys.executable,
                str(Path(__file__).resolve()),
                "--paths",
                args.paths,
                "--manifest",
                args.manifest,
                "--only",
                exp["id"],
                "--suite-output",
                str(suite_results),
                "--_child-run",
            ]
            if args.skip_train:
                cmd.append("--skip-train")
            print(f"[eval] subprocess: {' '.join(cmd)}")
            proc = subprocess.run(cmd, cwd=str(SERVER_DIR))
            summary_path = suite_results / exp["id"] / "run_record.json"
            if summary_path.exists():
                rows.append(json.loads(summary_path.read_text(encoding="utf-8")))
            else:
                rows.append(
                    {
                        "experiment_id": exp["id"],
                        "status": "failed",
                        "error": f"subprocess exit {proc.returncode}",
                    }
                )
        write_suite_artifacts(suite_results, rows, manifest)
        return 0

    print(f"[eval] Suite '{suite_name}' -> {suite_results}")
    rows = []
    for exp in experiments:
        print(f"\n[eval] === {exp['id']} ===")
        row = run_experiment(exp, paths, manifest, suite_results, args.dry_run, args.skip_train)
        rows.append(row)
        print(f"[eval] {exp['id']} status={row.get('status')}")
        if not args.dry_run and args.child_run:
            exp_dir = suite_results / exp["id"]
            exp_dir.mkdir(parents=True, exist_ok=True)
            (exp_dir / "run_record.json").write_text(json.dumps(row, indent=2), encoding="utf-8")

    if not args.dry_run and not args.child_run:
        write_suite_artifacts(suite_results, rows, manifest)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
