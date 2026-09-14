"""Offline original Tiny-SD benchmark. Never reads review targets or aliases."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import shutil
import statistics
import subprocess
import threading
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["HF_HOME"] = str(ROOT / ".cache" / "huggingface")

import psutil


def save_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, default=str), encoding="utf-8")


class MemorySampler:
    """Process RSS sampled at 50 ms; device-wide NVIDIA memory at 500 ms.

    Device-wide VRAM includes desktop and other apps. PyTorch allocator high-water
    marks are recorded separately; neither is called exclusive process VRAM.
    """
    def __init__(self):
        self.process = psutil.Process()
        self.stop_event = threading.Event()
        self.lock = threading.Lock()
        self.phase = "startup"
        self.peaks = {}
        self.gpu_samples = []
        self.errors = []
        self.threads = [threading.Thread(target=self.ram, daemon=True),
                        threading.Thread(target=self.gpu, daemon=True)]

    def start(self):
        for thread in self.threads:
            thread.start()

    def set_phase(self, phase):
        with self.lock:
            self.phase = phase

    def ram(self):
        while not self.stop_event.is_set():
            rss = self.process.memory_info().rss
            with self.lock:
                self.peaks[self.phase] = max(rss, self.peaks.get(self.phase, 0))
            self.stop_event.wait(0.05)

    def gpu(self):
        while not self.stop_event.is_set():
            try:
                result = subprocess.run(
                    ["nvidia-smi", "--id=0", "--query-gpu=memory.used,utilization.gpu,temperature.gpu",
                     "--format=csv,noheader,nounits"], capture_output=True, text=True,
                    check=True, timeout=5, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
                values = [v.strip() for v in result.stdout.strip().split(",")]
                with self.lock:
                    self.gpu_samples.append({"time": time.time(), "phase": self.phase,
                                             "used_mib": int(values[0]), "utilization_percent": int(values[1]),
                                             "temperature_c": int(values[2])})
            except Exception as exc:
                self.errors.append(str(exc))
                return
            self.stop_event.wait(0.5)

    def stop(self):
        self.stop_event.set()
        for thread in self.threads:
            thread.join(timeout=6)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seeds", type=int, nargs="+", default=[20260913, 42])
    parser.add_argument("--steps", type=int, default=25)
    parser.add_argument("--cases", nargs="*")
    parser.add_argument("--repeats", type=int, default=2)
    parser.add_argument("--precision", choices=["fp16", "fp32"], default="fp32")
    parser.add_argument("--variant", choices=["base", "taesd", "int8", "int8-taesd"], default="base")
    parser.add_argument("--cpu-offload", action="store_true", help="Keep only the active neural component on GPU")
    parser.add_argument("--resume-from", type=Path, help="Copy verified completed jobs into a new run and generate only the missing jobs")
    parser.add_argument("--save-latents", action="store_true", help="Save final latents for controlled decoder comparisons")
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit("Output directory already exists; choose a new run name to preserve evidence.")
    if args.steps < 1 or args.repeats < 0:
        raise SystemExit("Invalid steps/repeats")
    if args.variant != "base" and args.precision != "fp32":
        raise SystemExit("Stage 2 variants currently use FP32 activations only")
    args.output.mkdir(parents=True)
    (args.output / "images").mkdir()
    (args.output / "benchmark-source.py").write_bytes(Path(__file__).read_bytes())
    monitor = MemorySampler()
    monitor.start()
    records = []
    summary = {"status": "running", "started_utc": datetime.now(timezone.utc).isoformat()}
    try:
        started = time.perf_counter()
        import torch
        from diffusers import DiffusionPipeline
        summary["import_seconds"] = time.perf_counter() - started
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA unavailable; refusing to silently replace the GPU benchmark with CPU inference")
        torch.set_grad_enabled(False)
        torch.backends.cudnn.benchmark = False
        torch.backends.cuda.matmul.allow_tf32 = False
        prompts_path = Path(__file__).with_name("prompts.json")
        prompts = json.loads(prompts_path.read_text(encoding="utf-8"))
        manifest_path = ROOT / "outputs" / "tiny-sd" / "model-manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        model_path = ROOT / "models" / "tiny-sd" / manifest["revision"]
        has_int8 = args.variant in {"int8", "int8-taesd"}
        has_taesd = args.variant in {"taesd", "int8-taesd"}
        # Verify files before deserialization. These original .bin files are loaded
        # by the pinned modern libraries using their weights_only=True path.
        monitor.set_phase("verify_files")
        for item in manifest["files"]:
            if (has_int8 and item["path"].startswith("unet/")) or (has_taesd and item["path"].startswith("vae/")):
                continue
            path = model_path / item["path"]
            with path.open("rb") as handle:
                actual = hashlib.file_digest(handle, "sha256").hexdigest()
            if path.stat().st_size != item["bytes"] or actual != item["sha256"]:
                raise RuntimeError(f"Model manifest mismatch: {item['path']}")
        # Reading these files primes OS cache: this is process-first loading,
        # not a cold-disk/clean-machine load measurement.
        environment = {
            "python": platform.python_version(), "platform": platform.platform(),
            "cpu": platform.processor(), "ram_total_bytes": psutil.virtual_memory().total,
            "gpu": torch.cuda.get_device_name(0),
            "gpu_total_bytes": torch.cuda.get_device_properties(0).total_memory,
            "compute_capability": list(torch.cuda.get_device_capability(0)),
            "torch_cuda": torch.version.cuda, "cudnn": torch.backends.cudnn.version(),
            "nvidia_smi": subprocess.check_output(
                ["nvidia-smi", "--query-gpu=name,driver_version,memory.total,memory.used", "--format=csv"], text=True),
            "packages": {dist.metadata["Name"]: dist.version for dist in importlib.metadata.distributions()},
            "settings": {"precision": args.precision, "width": 512, "height": 512,
                         "steps": args.steps, "guidance_scale": 7.0, "batch_size": 1,
                         "seeds": args.seeds, "style": prompts["style"],
                         "negative_prompt": prompts["negative_prompt"], "cpu_offload": args.cpu_offload,
                         "attention_slicing": False, "quantization": "U-Net W8 / FP32 activation" if has_int8 else False,
                         "vae": "TAESD decoder-only" if has_taesd else "original", "variant": args.variant, "cudnn_benchmark": False,
                         "tf32": False, "generator_device": "cpu", "offline": True,
                         "finite_latents_check_every_step": True},
            "model_revision": manifest["revision"], "required_model_bytes": manifest["required_bytes"],
            "prompt_sha256": hashlib.sha256(prompts_path.read_bytes()).hexdigest(),
            "benchmark_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        }
        save_json(args.output / "environment.json", environment)
        save_json(args.output / "prompts.json", prompts)
        save_json(args.output / "model-manifest.json", manifest)
        monitor.set_phase("model_load")
        torch.cuda.synchronize()
        torch.cuda.reset_peak_memory_stats()
        started = time.perf_counter()
        dtype = torch.float32 if args.precision == "fp32" else torch.float16
        overrides = {}
        variant_manifests = {}
        if args.variant != "base":
            from variants import load_taesd, load_int8_unet, VARIANT_ROOT
            variant_source = Path(__file__).with_name("variants.py").read_bytes()
            (args.output / "variant-source.py").write_bytes(variant_source)
            environment["variant_source_sha256"] = hashlib.sha256(variant_source).hexdigest()
            if has_int8:
                overrides["unet"], variant_manifests["unet-int8"] = load_int8_unet()
            if has_taesd:
                overrides["vae"], variant_manifests["taesd-decoder"] = load_taesd()
            required = sum(item["bytes"] for item in manifest["files"] if item["inference_required"]
                           and not (has_int8 and item["path"].startswith("unet/"))
                           and not (has_taesd and item["path"].startswith("vae/")))
            environment["required_model_bytes"] = required + sum(v["required_bytes"] + (VARIANT_ROOT / name / "manifest.json").stat().st_size
                                                                   for name, v in variant_manifests.items())
            save_json(args.output / "variant-manifests.json", variant_manifests)
        pipe = DiffusionPipeline.from_pretrained(str(model_path), torch_dtype=dtype,
                                                 local_files_only=True, use_safetensors=False, **overrides)
        if args.cpu_offload:
            pipe.enable_model_cpu_offload(gpu_id=0)
        else:
            pipe.to("cuda")
        pipe.set_progress_bar_config(disable=True)
        torch.cuda.synchronize()
        summary["model_load_seconds"] = time.perf_counter() - started
        summary["load_peak_allocated_bytes"] = torch.cuda.max_memory_allocated()
        summary["load_peak_reserved_bytes"] = torch.cuda.max_memory_reserved()
        save_json(args.output / "scheduler.json", dict(pipe.scheduler.config))
        environment["scheduler_class"] = type(pipe.scheduler).__name__
        environment["attention_processors"] = sorted({type(p).__name__ for p in pipe.unet.attn_processors.values()})
        environment["loaded_components"] = {
            name: {"dtype": str(module.dtype), "parameters": sum(p.numel() for p in module.parameters()),
                   "parameter_dtypes": dict(Counter(str(p.dtype) for p in module.parameters()))}
            for name, module in [("unet", pipe.unet), ("text_encoder", pipe.text_encoder), ("vae", pipe.vae)]}
        save_json(args.output / "environment.json", environment)
        cases = [c for c in prompts["cases"] if not args.cases or c["id"] in args.cases]
        if not cases or (args.cases and set(args.cases) - {c["id"] for c in cases}):
            raise ValueError("Unknown or empty cases")
        # Warm-up uses an existing indirect English description and remains part
        # of the timing evidence, but is not double-counted as a quality sample.
        jobs = [("first_generation", cases[0], "en", args.seeds[0])]
        jobs += [("repeat", cases[0], "en", args.seeds[0]) for _ in range(args.repeats)]
        for case in cases:
            for seed in args.seeds:
                for language in ("ko", "en"):
                    jobs.append(("quality", case, language, seed))
        previous = {}
        if args.resume_from:
            previous_environment = json.loads((args.resume_from / "environment.json").read_text(encoding="utf-8"))
            for key in ["model_revision", "variant_source_sha256"]:
                if previous_environment.get(key) != environment.get(key):
                    raise ValueError(f"Cannot resume with changed model or adapter: {key}")
            for key in ["precision", "width", "height", "steps", "guidance_scale", "seeds", "style", "negative_prompt", "variant"]:
                if previous_environment["settings"].get(key) != environment["settings"].get(key):
                    raise ValueError(f"Cannot resume with changed generation setting: {key}")
            if previous_environment["prompt_sha256"] != environment["prompt_sha256"]:
                raise ValueError("Cannot resume with changed prompts")
            old_source = (args.resume_from / "benchmark-source.py").read_bytes()
            if hashlib.sha256(old_source).hexdigest() != previous_environment["benchmark_sha256"]:
                raise ValueError("Previous benchmark source hash mismatch")
            for line in (args.resume_from / "metrics.jsonl").read_text(encoding="utf-8").splitlines():
                row = json.loads(line)
                if row["status"] != "ok":
                    continue
                if row["job_id"] in previous:
                    raise ValueError("Duplicate previous job")
                previous[row["job_id"]] = row
            save_json(args.output / "previous-environment.json", previous_environment)
            (args.output / "previous-benchmark-source.py").write_bytes(old_source)
            environment["resumed_from"] = str(args.resume_from.resolve())
            environment["previous_settings"] = previous_environment["settings"]
            environment["previous_benchmark_sha256"] = previous_environment["benchmark_sha256"]
            save_json(args.output / "environment.json", environment)
        print(f"Loaded original model in {summary['model_load_seconds']:.3f}s; {len(jobs)} generations", flush=True)
        with (args.output / "metrics.jsonl").open("w", encoding="utf-8") as metrics:
            for index, (kind, case, language, seed) in enumerate(jobs):
                job_id = f"{index:03d}_{kind}_{case['id']}_{language}_{seed}"
                # Only the supplied indirect description and common style enter the model.
                prompt = f"{case[language]}, {prompts['style']}"
                token_ids = pipe.tokenizer(prompt, truncation=False)["input_ids"]
                record = {"job_id": job_id, "kind": kind, "case_id": case["id"],
                          "language": language, "seed": seed, "prompt": prompt,
                          "token_count": len(token_ids), "token_limit": pipe.tokenizer.model_max_length,
                          "truncated": len(token_ids) > pipe.tokenizer.model_max_length}
                if job_id in previous:
                    old = previous.pop(job_id)
                    if any(old[key] != record[key] for key in record):
                        raise ValueError(f"Previous job input mismatch: {job_id}")
                    original_image = (args.resume_from / old["image"]).resolve()
                    if not original_image.is_relative_to(args.resume_from.resolve()):
                        raise ValueError("Previous image path escapes its run")
                    if hashlib.sha256(original_image.read_bytes()).hexdigest() != old["image_sha256"]:
                        raise ValueError("Previous image hash mismatch")
                    destination = args.output / "images" / original_image.name
                    shutil.copyfile(original_image, destination)
                    old.update(image=destination.relative_to(args.output).as_posix(), resumed=True)
                    records.append(old)
                    metrics.write(json.dumps(old, ensure_ascii=False) + "\n")
                    metrics.flush()
                    continue
                monitor.set_phase(job_id)
                torch.cuda.synchronize()
                torch.cuda.reset_peak_memory_stats()
                started = time.perf_counter()
                try:
                    def finite_latents(pipeline, step, timestep, callback_kwargs):
                        if not torch.isfinite(callback_kwargs["latents"]).all().item():
                            raise FloatingPointError(f"Non-finite denoising latents at step {step}, timestep {timestep}")
                        if step == args.steps - 1:
                            latent_array = callback_kwargs["latents"].detach().float().cpu().numpy()
                            record["final_latent_sha256"] = hashlib.sha256(latent_array.tobytes()).hexdigest()
                            if args.save_latents:
                                import numpy as np
                                (args.output / "latents").mkdir(exist_ok=True)
                                latent_path = args.output / "latents" / f"{job_id}.npy"
                                np.save(latent_path, latent_array)
                                record["latent_file"] = latent_path.relative_to(args.output).as_posix()
                                record["latent_file_sha256"] = hashlib.sha256(latent_path.read_bytes()).hexdigest()
                        return callback_kwargs
                    with torch.inference_mode():
                        result = pipe(prompt=prompt, negative_prompt=prompts["negative_prompt"],
                                      width=512, height=512, num_inference_steps=args.steps,
                                      guidance_scale=7.0, num_images_per_prompt=1,
                                      generator=torch.Generator("cpu").manual_seed(seed),
                                      callback_on_step_end=finite_latents)
                    torch.cuda.synchronize()
                    record["generation_seconds"] = time.perf_counter() - started
                    record["peak_allocated_bytes"] = torch.cuda.max_memory_allocated()
                    record["peak_reserved_bytes"] = torch.cuda.max_memory_reserved()
                    image_path = args.output / "images" / f"{job_id}.png"
                    started = time.perf_counter()
                    result.images[0].save(image_path)
                    record["png_save_seconds"] = time.perf_counter() - started
                    record["image"] = image_path.relative_to(args.output).as_posix()
                    record["image_bytes"] = image_path.stat().st_size
                    record["image_sha256"] = hashlib.sha256(image_path.read_bytes()).hexdigest()
                    record["pixel_sha256"] = hashlib.sha256(result.images[0].tobytes()).hexdigest()
                    record["pixel_extrema"] = result.images[0].getextrema()
                    record["uniform_image"] = all(lo == hi for lo, hi in result.images[0].getextrema())
                    if record["uniform_image"]:
                        raise FloatingPointError("Uniform output image; excluded from valid generation results")
                    record["status"] = "ok"
                    del result
                except Exception as exc:
                    record.update(status="error", error=repr(exc), elapsed_seconds=time.perf_counter() - started)
                    records.append(record)
                    metrics.write(json.dumps(record, ensure_ascii=False) + "\n")
                    metrics.flush()
                    raise
                record["rss_peak_sampled_bytes"] = monitor.peaks.get(job_id)
                records.append(record)
                metrics.write(json.dumps(record, ensure_ascii=False) + "\n")
                metrics.flush()
                print(f"[{index + 1}/{len(jobs)}] {job_id}: {record['generation_seconds']:.2f}s; "
                      f"tokens={record['token_count']}/{record['token_limit']}; "
                      f"allocated={record['peak_allocated_bytes'] / 2**20:.0f}MiB", flush=True)
        times = [r["generation_seconds"] for r in records if r["kind"] != "first_generation"]
        if previous:
            raise ValueError("Previous run contains jobs outside the requested matrix")
        new_rows = [r for r in records if not r.get("resumed")]
        summary.update(resumed_generations=len(records) - len(new_rows), new_generations=len(new_rows),
                       new_segment_first_seconds=new_rows[0]["generation_seconds"] if new_rows else None,
                       new_segment_mean_seconds=statistics.mean(r["generation_seconds"] for r in new_rows) if new_rows else None,
                       new_segment_peak_allocated_bytes=max((r["peak_allocated_bytes"] for r in new_rows), default=0))
        summary.update(status="completed", generations=len(records),
                       first_generation_seconds=records[0]["generation_seconds"],
                       subsequent_mean_seconds=statistics.mean(times) if times else None,
                       subsequent_median_seconds=statistics.median(times) if times else None,
                       subsequent_max_seconds=max(times) if times else None,
                       peak_allocated_bytes=max(r["peak_allocated_bytes"] for r in records),
                       peak_reserved_bytes=max(r["peak_reserved_bytes"] for r in records),
                       truncated_jobs=sum(r["truncated"] for r in records),
                       same_prompt_repeat_pixels_identical=len({r["pixel_sha256"] for r in records
                                                               if r["kind"] in {"first_generation", "repeat"}}) == 1)
    except BaseException as exc:
        summary.update(status="failed", error=repr(exc), traceback=traceback.format_exc())
        raise
    finally:
        monitor.stop()
        summary["finished_utc"] = datetime.now(timezone.utc).isoformat()
        summary["rss_peak_sampled_bytes"] = max(monitor.peaks.values(), default=None)
        summary["rss_os_lifetime_peak_bytes"] = getattr(monitor.process.memory_info(), "peak_wset", None)
        summary["device_vram_peak_sampled_mib"] = max((s["used_mib"] for s in monitor.gpu_samples), default=None)
        summary["memory_sampler_errors"] = monitor.errors
        save_json(args.output / "summary.json", summary)
        save_json(args.output / "memory.json", {"rss_phase_peaks_bytes": monitor.peaks, "gpu_samples": monitor.gpu_samples})
        print(json.dumps(summary, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
