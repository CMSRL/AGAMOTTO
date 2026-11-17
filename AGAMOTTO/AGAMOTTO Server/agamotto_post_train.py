#!/usr/bin/env python3

"""Prepare SAC post-training inference: copy ONNX, write active_run.json, notify WinTAK.



Prefer agamotto_sac_train.py for the full train + auto-inference workflow.

This script remains for re-running setup from an existing results folder.

"""



from __future__ import annotations



import argparse

import sys

from pathlib import Path



try:

    from agamotto_inference_common import (

        DEFAULT_STREAMING,

        run_onnx_inference_episodes,

        setup_inference_from_results,

    )

except ImportError as exc:

    print(f"Run from AGAMOTTO Server directory: {exc}", file=sys.stderr)

    raise SystemExit(1) from exc





def main() -> int:

    p = argparse.ArgumentParser(description="AGAMOTTO SAC post-training inference setup")

    p.add_argument("--run-id", default="sac_run")

    p.add_argument("--behavior-name", default="AGAMOTTO_RL")

    p.add_argument("--results-dir", default="results")

    p.add_argument("--streaming-assets", default="")

    p.add_argument("--total-steps", type=int, default=0)

    p.add_argument("--leader-callsign", default="leader")

    p.add_argument("--inference-episodes", type=int, default=1)

    p.add_argument("--skip-inference", action="store_true")

    p.add_argument("--base-port", type=int, default=5004)

    p.add_argument("--timeout-wait", type=int, default=5000)

    p.add_argument("--time-scale", type=float, default=50.0)

    args = p.parse_args()



    streaming = Path(args.streaming_assets) if args.streaming_assets else DEFAULT_STREAMING

    results = Path(args.results_dir)



    try:

        onnx_dst = setup_inference_from_results(

            streaming,

            results,

            behavior_name=args.behavior_name,

            run_id=args.run_id,

            trainer_type="sac",

            total_steps=args.total_steps,

            leader_callsign=args.leader_callsign,

        )

    except FileNotFoundError as ex:

        print(ex, file=sys.stderr)

        return 1



    print(f"Copied policy to {onnx_dst}")

    print(f"Wrote inference active_run.json under {streaming / 'evaluation'}")

    print("Notified WinTAK plugin (training_complete).")



    if args.skip_inference or args.inference_episodes <= 0:

        return 0



    print("Reconnecting to Unity for inference (keep Unity in Play)...")

    try:

        completed = run_onnx_inference_episodes(

            onnx_dst,

            args.behavior_name,

            episodes=int(args.inference_episodes),

            base_port=int(args.base_port),

            timeout_wait=int(args.timeout_wait),

            time_scale=float(args.time_scale),

            tag="sac",

        )

    except Exception as ex:

        print(f"ERROR during inference: {ex}", file=sys.stderr)

        return 1



    print(f"Finished {completed} inference episode(s). Check WinTAK Assessment.")

    return 0





if __name__ == "__main__":

    raise SystemExit(main())


