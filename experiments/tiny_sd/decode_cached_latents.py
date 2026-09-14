"""Compare TAESD with original VAE using the exact same INT8 final latents.

This measures decoder-only time. Full combined inference is tested separately.
"""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import time
import traceback

from benchmark import MemorySampler, save_json
import numpy as np
from PIL import Image
import torch
from variants import VARIANT_ROOT, load_taesd


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source_summary = json.loads((args.source / "summary.json").read_text())
    environment = json.loads((args.source / "environment.json").read_text())
    if source_summary["status"] != "completed" or environment["settings"]["variant"] != "int8":
        raise ValueError("Requires a completed INT8/original-VAE run")
    rows = [json.loads(line) for line in (args.source / "metrics.jsonl").read_text(encoding="utf-8").splitlines()]
    rows = [row for row in rows if row["kind"] == "quality"]
    args.output.mkdir(parents=True, exist_ok=False)
    (args.output / "images").mkdir()
    source_code = Path(__file__).read_bytes()
    (args.output / "benchmark-source.py").write_bytes(source_code)
    variant_source = Path(__file__).with_name("variants.py").read_bytes()
    (args.output / "variant-source.py").write_bytes(variant_source)
    environment.update(benchmark_sha256=hashlib.sha256(source_code).hexdigest(),
                       variant_source_sha256=hashlib.sha256(variant_source).hexdigest(),
                       cached_latent_source=str(args.source.resolve()),
                       execution_mode="TAESD decoder only from verified INT8 final latents")
    environment["settings"].update(variant="int8-taesd", vae="TAESD decoder-only", cpu_offload=False)
    # The source U-Net environment is provenance, not a claim that it is loaded here.
    environment.pop("loaded_components", None)
    environment.pop("resumed_from", None)
    for name in ["prompts.json", "scheduler.json", "model-manifest.json"]:
        (args.output / name).write_bytes((args.source / name).read_bytes())
    monitor = MemorySampler()
    monitor.start()
    records = []
    summary = {"status": "running", "timing_scope": "decoder_only", "full_pipeline_benchmarked_here": False}
    try:
        torch.set_grad_enabled(False)
        torch.cuda.reset_peak_memory_stats()
        monitor.set_phase("load_decoder")
        started = time.perf_counter()
        decoder, manifest = load_taesd()
        decoder.to("cuda")
        torch.cuda.synchronize()
        summary["decoder_load_seconds"] = time.perf_counter() - started
        save_json(args.output / "variant-manifests.json", {"taesd-decoder": manifest})
        original_manifest = json.loads((args.source / "model-manifest.json").read_text(encoding="utf-8"))
        original_vae_bytes = sum(item["bytes"] for item in original_manifest["files"]
                                 if item["inference_required"] and item["path"].startswith("vae/"))
        # Replace the source run's VAE footprint, including the decoder's runtime-read manifest.
        environment["required_model_bytes"] += (manifest["required_bytes"]
                                                + (VARIANT_ROOT / "taesd-decoder/manifest.json").stat().st_size
                                                - original_vae_bytes)
        save_json(args.output / "environment.json", environment)
        with (args.output / "metrics.jsonl").open("w", encoding="utf-8") as handle:
            for row in rows:
                path = (args.source / row["latent_file"]).resolve()
                if not path.is_relative_to(args.source.resolve()):
                    raise ValueError("Latent path escapes its source run")
                if hashlib.sha256(path.read_bytes()).hexdigest() != row["latent_file_sha256"]:
                    raise ValueError("Cached latent file hash mismatch")
                values = np.load(path, allow_pickle=False)
                if values.shape != (1, 4, 64, 64) or values.dtype != np.float32 or not np.isfinite(values).all():
                    raise ValueError("Invalid cached latent")
                if hashlib.sha256(values.tobytes()).hexdigest() != row["final_latent_sha256"]:
                    raise ValueError("Cached latent values differ from final denoising output")
                monitor.set_phase(row["job_id"])
                torch.cuda.reset_peak_memory_stats()
                started = time.perf_counter()
                with torch.inference_mode():
                    decoded = decoder.decode(torch.from_numpy(values).to("cuda"), return_dict=False)[0]
                if not torch.isfinite(decoded).all().item():
                    raise FloatingPointError("Non-finite decoded pixels")
                pixels = ((decoded / 2 + 0.5).clamp(0, 1).cpu().permute(0, 2, 3, 1).numpy()[0] * 255).round().astype(np.uint8)
                torch.cuda.synchronize()
                seconds = time.perf_counter() - started
                image = Image.fromarray(pixels)
                if all(lo == hi for lo, hi in image.getextrema()):
                    raise FloatingPointError("Uniform decoder output")
                destination = args.output / "images" / (row["job_id"] + ".png")
                image.save(destination)
                record = {key: row[key] for key in ["job_id", "kind", "case_id", "language", "seed", "prompt", "token_count", "token_limit", "truncated", "final_latent_sha256"]}
                record.update(status="ok", generation_seconds=seconds, timing_scope="decoder_only",
                              source_latent_file=row["latent_file"], source_latent_file_sha256=row["latent_file_sha256"],
                              image=destination.relative_to(args.output).as_posix(), image_bytes=destination.stat().st_size,
                              image_sha256=hashlib.sha256(destination.read_bytes()).hexdigest(),
                              pixel_sha256=hashlib.sha256(image.tobytes()).hexdigest(), uniform_image=False,
                              peak_allocated_bytes=torch.cuda.max_memory_allocated(), peak_reserved_bytes=torch.cuda.max_memory_reserved())
                records.append(record)
                handle.write(json.dumps(record, ensure_ascii=False) + "\n")
                handle.flush()
                del decoded
        summary.update(status="completed", generations=len(records),
                       decoder_first_seconds=records[0]["generation_seconds"],
                       decoder_mean_seconds=statistics.mean(row["generation_seconds"] for row in records),
                       peak_allocated_bytes=max(row["peak_allocated_bytes"] for row in records),
                       peak_reserved_bytes=max(row["peak_reserved_bytes"] for row in records))
    except BaseException as exc:
        summary.update(status="failed", error=repr(exc), traceback=traceback.format_exc())
        raise
    finally:
        monitor.stop()
        summary["rss_peak_sampled_bytes"] = max(monitor.peaks.values(), default=None)
        summary["device_vram_peak_sampled_mib"] = max((row["used_mib"] for row in monitor.gpu_samples), default=None)
        save_json(args.output / "summary.json", summary)
        save_json(args.output / "memory.json", {"rss_phase_peaks_bytes": monitor.peaks, "gpu_samples": monitor.gpu_samples})
        print(json.dumps(summary), flush=True)


if __name__ == "__main__":
    main()
