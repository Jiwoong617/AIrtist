"""Experiment adapters for published TAESD and Quanto W8/FP32 U-Net."""
import hashlib
import json
from pathlib import Path

import torch
from diffusers import AutoencoderTiny, UNet2DConditionModel
from safetensors.torch import load_file

ROOT = Path(__file__).resolve().parents[2]
VARIANT_ROOT = ROOT / "models" / "tiny-sd-stage2"


def checked_manifest(kind):
    folder = VARIANT_ROOT / kind
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    for item in manifest["files"]:
        path = folder / item["path"]
        with path.open("rb") as handle:
            sha = hashlib.file_digest(handle, "sha256").hexdigest()
        if path.stat().st_size != item["bytes"] or sha != item["sha256"]:
            raise RuntimeError(f"Variant integrity failure: {path}")
    return folder, manifest


def load_taesd():
    folder, manifest = checked_manifest("taesd-decoder")
    model = AutoencoderTiny.from_config(str(folder / "config.json"))
    model.encoder = torch.nn.Identity()
    model.decoder.load_state_dict(load_file(str(folder / "decoder.safetensors")), strict=True)
    model.eval()
    return model, manifest


def load_int8_unet():
    from optimum.quanto import requantize
    folder, manifest = checked_manifest("unet-int8")
    with torch.device("meta"):
        model = UNet2DConditionModel.from_config(str(folder / "config.json"))
    state = load_file(str(folder / "model.safetensors"))
    # Non-quantized weights are stored at their exact original FP16 values.
    # Restore those values to FP32 without altering int8 payloads or FP32 scales.
    state = {name: value.float() if value.dtype == torch.float16 else value for name, value in state.items()}
    qmap = json.loads((folder / "quantization_map.json").read_text())
    requantize(model, state, qmap, device=torch.device("cpu"))
    # Quanto internally uses strict=False; explicitly assert a full round-trip.
    loaded = model.state_dict()
    if set(loaded) != set(state):
        raise RuntimeError("Quantized checkpoint keys did not load completely")
    for name in state:
        if not torch.equal(loaded[name].cpu(), state[name].cpu()):
            raise RuntimeError(f"Quantized checkpoint round-trip mismatch: {name}")
    model.eval()
    return model, manifest
