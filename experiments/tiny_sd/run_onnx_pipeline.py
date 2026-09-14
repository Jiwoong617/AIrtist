"""Local ONNX/DirectML text -> U-Net -> TAESD pipeline feasibility experiment.

Neural networks execute in ONNX Runtime. The reference Diffusers DPM-Solver
scheduler runs on CPU; this is not yet a C# product implementation.
"""
import argparse
from collections import Counter
import gc
import hashlib
import json
from pathlib import Path
import time
import traceback

import numpy as np
import onnxruntime as ort
from PIL import Image
import torch
from diffusers import DPMSolverMultistepScheduler
from transformers import CLIPTokenizer
from benchmark import ROOT, MemorySampler, save_json
from verify_onnx import session_options

MODELS = ROOT / "models/tiny-sd-onnx-stage2"
ORIGINAL = ROOT / "models/tiny-sd/cad0bd7495fa6c4bcca01b19a723dc91627fe84f"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cases", nargs="+", default=["fruit_red", "animal_ears", "sphere_below"])
    parser.add_argument("--language", choices=["en", "ko"], default="en")
    parser.add_argument("--seed", type=int, default=20260913)
    parser.add_argument("--disable-dml-fusion", action="store_true")
    parser.add_argument("--disable-metacommands", action="store_true")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    (args.output / "images").mkdir()
    source = Path(__file__).read_bytes()
    (args.output / "pipeline-source.py").write_bytes(source)
    torch.set_grad_enabled(False)
    torch.set_num_threads(6)
    monitor = MemorySampler()
    monitor.start()
    sessions, records = {}, []
    summary = {"status": "running", "backend": "onnxruntime-directml 1.22.0", "neurals": "ONNX Runtime",
               "pipeline_source_sha256": hashlib.sha256(source).hexdigest(),
               "scheduler": "Diffusers DPM-Solver on CPU", "precision": "W8 U-Net storage / FP32 compute",
               "session_residency": "One neural session at a time; every generation includes session loads",
               "provider_events": {},
               "disable_dml_fusion": args.disable_dml_fusion,
               "disable_metacommands": args.disable_metacommands,
               "settings": {"width": 512, "height": 512, "steps": 25, "guidance_scale": 7,
                            "seed": args.seed, "language": args.language, "cases": args.cases}}
    try:
        if "DmlExecutionProvider" not in ort.get_available_providers():
            raise RuntimeError("DirectML is unavailable")
        monitor.set_phase("load")
        summary["models"] = {}
        for name in ["text_encoder-compact.onnx", "unet-w8.onnx", "taesd_decoder-compact.onnx"]:
            path = MODELS / name
            with path.open("rb") as handle:
                digest = hashlib.file_digest(handle, "sha256").hexdigest()
            summary["models"][name] = {"bytes": path.stat().st_size, "sha256": digest}
        def load_session(name, suffix, case_id):
            started_load = time.perf_counter()
            options = session_options(disable_dml_fusion=args.disable_dml_fusion)
            options.profile_file_prefix = str(args.output / (case_id + "-" + name))
            provider_options = {"device_id": "0", "disable_metacommands": str(args.disable_metacommands)}
            sessions[name] = ort.InferenceSession(str(MODELS / f"{name}-{suffix}.onnx"), sess_options=options,
                                                 providers=[("DmlExecutionProvider", provider_options), "CPUExecutionProvider"])
            sessions[name].disable_fallback()
            return time.perf_counter() - started_load

        def close_session(name):
            session = sessions.pop(name)
            events = json.loads(Path(session.end_profiling()).read_text())
            counts = Counter(summary["provider_events"].get(name, {}))
            counts.update(e.get("args", {}).get("provider") for e in events if e.get("args", {}).get("provider"))
            summary["provider_events"][name] = dict(counts)
            del session
            gc.collect()

        started = time.perf_counter()
        tokenizer = CLIPTokenizer.from_pretrained(str(ORIGINAL / "tokenizer"), local_files_only=True)
        scheduler = DPMSolverMultistepScheduler.from_pretrained(str(ORIGINAL / "scheduler"), local_files_only=True)
        summary["tokenizer_scheduler_load_seconds"] = time.perf_counter() - started
        prompts = json.loads(Path(__file__).with_name("prompts.json").read_text(encoding="utf-8"))
        cases = {c["id"]: c for c in prompts["cases"]}
        for case_id in args.cases:
            description = cases[case_id][args.language]
            prompt = description + ", " + prompts["style"]
            token_count = len(tokenizer(prompt, truncation=False).input_ids)
            if token_count > 77:
                raise ValueError("Experiment input exceeds 77 tokens")
            monitor.set_phase(case_id)
            started = time.perf_counter()
            load_times = {"text_encoder": load_session("text_encoder", "compact", case_id)}
            ids = tokenizer(["", prompt], padding="max_length", max_length=77, truncation=True, return_tensors="np").input_ids.astype(np.int64)
            embeds = sessions["text_encoder"].run(None, {"input_ids": ids})[0]
            close_session("text_encoder")
            load_times["unet"] = load_session("unet", "w8", case_id)
            latents = torch.randn((1, 4, 64, 64), generator=torch.Generator("cpu").manual_seed(args.seed))
            scheduler.set_timesteps(25)
            latents *= scheduler.init_noise_sigma
            for timestep in scheduler.timesteps:
                summary["last_started_timestep"] = int(timestep.item())
                sample = scheduler.scale_model_input(torch.cat([latents] * 2), timestep).numpy()
                noise = sessions["unet"].run(None, {"sample": sample, "timestep": np.array([timestep.item()], dtype=np.float32),
                                                    "encoder_hidden_states": embeds})[0]
                unconditional, conditional = np.split(noise, 2)
                guided = unconditional + 7.0 * (conditional - unconditional)
                latents = scheduler.step(torch.from_numpy(guided), timestep, latents).prev_sample
                if not torch.isfinite(latents).all().item():
                    raise FloatingPointError("Non-finite ONNX pipeline latent")
            close_session("unet")
            load_times["taesd_decoder"] = load_session("taesd_decoder", "compact", case_id)
            # Exported TAESD wraps Diffusers' [-1,1] decoder and uses latent scale 1.
            decoded = sessions["taesd_decoder"].run(None, {"latents": latents.numpy()})[0]
            close_session("taesd_decoder")
            if not np.isfinite(decoded).all():
                raise FloatingPointError("Non-finite ONNX decoder output")
            pixels = np.clip(decoded[0] / 2 + 0.5, 0, 1).transpose(1, 2, 0)
            pixels = np.round(pixels * 255).astype(np.uint8)
            if np.ptp(pixels) == 0:
                raise FloatingPointError("Uniform ONNX output")
            elapsed = time.perf_counter() - started
            image_path = args.output / "images" / f"{case_id}_{args.language}_{args.seed}.png"
            Image.fromarray(pixels).save(image_path)
            row = {"case_id": case_id, "language": args.language, "seed": args.seed, "prompt": prompt,
                   "token_count": token_count, "final_latent_sha256": hashlib.sha256(latents.numpy().tobytes()).hexdigest(),
                   "session_load_seconds": load_times, "timing_includes_session_load_and_release": True,
                   "generation_seconds": elapsed, "image": image_path.relative_to(args.output).as_posix(),
                   "sha256": hashlib.sha256(image_path.read_bytes()).hexdigest(), "status": "ok"}
            records.append(row)
            save_json(args.output / "metrics.json", records)
            print(json.dumps(row), flush=True)
        summary["status"] = "completed"
    except BaseException as exc:
        summary.update(status="failed", error=repr(exc), traceback=traceback.format_exc())
        raise
    finally:
        monitor.stop()
        summary["rss_peak_sampled_bytes"] = max(monitor.peaks.values(), default=None)
        summary["device_vram_peak_sampled_mib"] = max((s["used_mib"] for s in monitor.gpu_samples), default=None)
        for name, session in sessions.items():
            events = json.loads(Path(session.end_profiling()).read_text())
            counts = Counter(summary["provider_events"].get(name, {}))
            counts.update(e.get("args", {}).get("provider") for e in events if e.get("args", {}).get("provider"))
            summary["provider_events"][name] = dict(counts)
        if summary["status"] == "completed" and any(
            not counts.get("DmlExecutionProvider", 0) for counts in summary["provider_events"].values()
        ):
            summary.update(status="failed", error="Missing DirectML execution events for a neural component")
        save_json(args.output / "summary.json", summary)
        save_json(args.output / "memory.json", {"rss_phase_peaks": monitor.peaks, "gpu_samples": monitor.gpu_samples})
        sessions.clear()
        gc.collect()
        print(json.dumps(summary), flush=True)
    if summary["status"] != "completed":
        raise SystemExit(1)


if __name__ == "__main__":
    main()
