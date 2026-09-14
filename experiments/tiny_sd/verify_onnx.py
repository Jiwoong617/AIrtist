"""Measure ONNX provider execution and numerical differences on fixed inputs."""
import argparse
from collections import Counter
import gc
import hashlib
import json
from pathlib import Path
import statistics
import time
import traceback

import numpy as np
import onnxruntime as ort
import torch
from variants import ROOT, load_int8_unet

MODELS = ROOT / "models/tiny-sd-onnx-stage2"


def session_options(disable_dml_fusion=False):
    options = ort.SessionOptions()
    options.enable_mem_pattern = False
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.intra_op_num_threads = 6
    options.inter_op_num_threads = 1
    options.enable_profiling = True
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    if disable_dml_fusion:
        options.add_session_config_entry("ep.dml.disable_graph_fusion", "1")
    return options


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--provider", choices=["cpu", "dml"], required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--disable-dml-fusion", action="store_true")
    parser.add_argument("--disable-metacommands", action="store_true")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    source = Path(__file__).read_bytes()
    (args.output / "verify-source.py").write_bytes(source)
    torch.set_num_threads(6)
    torch.set_grad_enabled(False)
    providers = ["CPUExecutionProvider"] if args.provider == "cpu" else ["DmlExecutionProvider", "CPUExecutionProvider"]
    jobs = [("text_encoder", "compact"), ("taesd_decoder", "compact"), ("unet", "w8")]
    results = []
    for name, suffix in jobs:
        record = {"component": name, "provider_requested": providers[0], "disable_dml_fusion": args.disable_dml_fusion,
                  "disable_metacommands": args.disable_metacommands, "verify_source_sha256": hashlib.sha256(source).hexdigest()}
        try:
            fixture = np.load(MODELS / f"{name}-reference.npz")
            feeds = {key: fixture[key] for key in fixture.files if key != "expected"}
            expected = fixture["expected"]
            if name == "unet":
                # Compare against the same quantized PyTorch weights, separately
                # from the intentional difference against original float weights.
                model, _ = load_int8_unet()
                model.eval()
                expected_quant = model(**{key: torch.from_numpy(value) for key, value in feeds.items()}).sample.detach().numpy()
                del model
                gc.collect()
            else:
                expected_quant = expected
            options = session_options(disable_dml_fusion=args.disable_dml_fusion)
            options.profile_file_prefix = str(args.output / name)
            path = MODELS / f"{name}-{suffix}.onnx"
            started = time.perf_counter()
            provider_options = [{"device_id": "0", "disable_metacommands": str(args.disable_metacommands)}, {}] if args.provider == "dml" else [{}]
            session = ort.InferenceSession(str(path), sess_options=options, providers=providers, provider_options=provider_options)
            session.disable_fallback()
            record["session_load_seconds"] = time.perf_counter() - started
            record["providers_registered"] = session.get_providers()
            times = []
            for _ in range(3):
                started = time.perf_counter()
                actual = session.run(None, feeds)[0]
                times.append(time.perf_counter() - started)
                if actual.shape != expected.shape or not np.isfinite(actual).all():
                    raise RuntimeError("ONNX returned invalid output")
            profile = Path(session.end_profiling())
            events = json.loads(profile.read_text())
            provider_events = Counter(event.get("args", {}).get("provider") for event in events
                                      if event.get("args", {}).get("provider"))
            record.update(first_seconds=times[0], subsequent_mean_seconds=statistics.mean(times[1:]),
                          output_shape=list(actual.shape), profile_provider_events=dict(provider_events),
                          max_abs_vs_same_weights=float(np.max(np.abs(actual - expected_quant))),
                          rmse_vs_same_weights=float(np.sqrt(np.mean((actual - expected_quant) ** 2))),
                          rmse_vs_original=float(np.sqrt(np.mean((actual - expected) ** 2))),
                          model_bytes=path.stat().st_size,
                          model_sha256=hashlib.sha256(path.read_bytes()).hexdigest(), status="ok")
            if args.provider == "dml" and not any("Dml" in name for name in provider_events):
                raise RuntimeError("No DirectML execution events; cannot claim GPU execution")
            # Export/provider parity only. This tolerance is not a semantic score.
            if record["max_abs_vs_same_weights"] > 0.01:
                raise RuntimeError("Numerical parity exceeded experiment tolerance 0.01")
            del session
            gc.collect()
        except Exception as exc:
            record.update(status="failed", error=repr(exc), traceback=traceback.format_exc())
        results.append(record)
        (args.output / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(json.dumps(record), flush=True)
    if any(row["status"] != "ok" for row in results):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
