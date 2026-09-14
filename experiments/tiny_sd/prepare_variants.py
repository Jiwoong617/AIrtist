"""Create measured decoder-only and W8 artifacts, without training."""
from collections import Counter
import gc
import hashlib
import json
from pathlib import Path
import time

import torch
from diffusers import AutoencoderTiny, UNet2DConditionModel
from optimum.quanto import quantize, freeze, qint8, quantization_map
from safetensors.torch import save_file
from variants import ROOT, VARIANT_ROOT, load_int8_unet, load_taesd


def write_manifest(folder, details):
    records = []
    for path in sorted(folder.rglob("*")):
        if path.is_file() and path.name != "manifest.json":
            records.append({"path": path.relative_to(folder).as_posix(), "bytes": path.stat().st_size,
                            "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    details.update(files=records, required_bytes=sum(r["bytes"] for r in records))
    (folder / "manifest.json").write_text(json.dumps(details, indent=2), encoding="utf-8")
    return details


def main():
    torch.set_grad_enabled(False)
    torch.set_num_threads(6)
    taesd_manifest = json.loads((ROOT / "outputs/tiny-sd/stage2/taesd-manifest.json").read_text())
    taesd_root = ROOT / "models/taesd" / taesd_manifest["revision"]
    target = VARIANT_ROOT / "taesd-decoder"
    target.mkdir(parents=True, exist_ok=False)
    original = AutoencoderTiny.from_pretrained(str(taesd_root), local_files_only=True, torch_dtype=torch.float32)
    original.eval()
    original.save_config(str(target))
    save_file(original.decoder.state_dict(), str(target / "decoder.safetensors"))
    (target / "NOTICE.txt").write_text("Derived from madebyollin/taesd: decoder extracted without training or weight changes. Encoder omitted. Diffusers tensor names retained.\n", encoding="utf-8")
    details = write_manifest(target, {"source": taesd_manifest["repo"], "revision": taesd_manifest["revision"],
                                      "scaling_factor": original.config.scaling_factor, "precision": "FP32"})
    reloaded, _ = load_taesd()
    z = torch.randn(1, 4, 8, 8, generator=torch.Generator().manual_seed(7))
    assert torch.equal(original.decode(z).sample, reloaded.decode(z).sample)
    details["full_vs_decoder_only_exact_equal"] = True
    write_manifest(target, details)
    print(f"TAESD decoder-only verified: {details['required_bytes']:,} bytes", flush=True)
    del original, reloaded
    gc.collect()

    source = json.loads((ROOT / "outputs/tiny-sd/model-manifest.json").read_text())
    model_root = ROOT / "models/tiny-sd" / source["revision"]
    target = VARIANT_ROOT / "unet-int8"
    target.mkdir(parents=True, exist_ok=False)
    model = UNet2DConditionModel.from_pretrained(str(model_root / "unet"), torch_dtype=torch.float32,
                                               local_files_only=True, use_safetensors=False)
    model.eval()
    model.save_config(str(target))
    exclusions = ["conv_in", "conv_out", "time_embedding.*"]
    started = time.perf_counter()
    quantize(model, weights=qint8, activations=None, exclude=exclusions)
    freeze(model)
    qmap = quantization_map(model)
    state = model.state_dict()
    storage = {}
    compact_names = []
    for name, value in state.items():
        value = value.detach().cpu().contiguous()
        # Keep every quantization scale at FP32. Compact only values that are
        # exactly representable at the original checkpoint's FP16 precision.
        if value.dtype == torch.float32 and not name.endswith("._scale") and torch.equal(value.half().float(), value):
            value = value.half()
            compact_names.append(name)
        storage[name] = value
    save_file(storage, str(target / "model.safetensors"))
    (target / "quantization_map.json").write_text(json.dumps(qmap, indent=2), encoding="utf-8")
    (target / "NOTICE.txt").write_text("Modified Tiny-SD U-Net: post-training per-output-channel symmetric INT8 weights via optimum-quanto 0.2.7; FP32 activations. conv_in, conv_out, time_embedding, norms and biases retain floating precision. No training.\n", encoding="utf-8")
    details = write_manifest(target, {"source": source["repo"], "revision": source["revision"],
                                      "method": "optimum-quanto 0.2.7 qint8 weight-only", "activations": "FP32",
                                      "exclude": exclusions, "quantization_map_entries": len(qmap),
                                      "quantized_modules": sum(v["weights"] == "qint8" for v in qmap.values()),
                                      "storage_dtypes": dict(Counter(str(v.dtype) for v in storage.values())),
                                      "lossless_fp16_storage_tensor_count": len(compact_names),
                                      "conversion_seconds": time.perf_counter() - started})
    del model, state, storage
    gc.collect()
    reloaded, _ = load_int8_unet()
    details["checkpoint_round_trip_exact_equal"] = True
    write_manifest(target, details)
    print(f"INT8 U-Net round-trip verified: {details['required_bytes']:,} bytes", flush=True)
    report = ROOT / "outputs/tiny-sd/stage2/variant-manifests.json"
    report.write_text(json.dumps({kind: json.loads((VARIANT_ROOT / kind / "manifest.json").read_text())
                                 for kind in ["taesd-decoder", "unet-int8"]}, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
