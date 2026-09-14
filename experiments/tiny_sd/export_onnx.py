"""Export fixed-shape original/TAESD ONNX, then transplant verified W8 tensors.

The compact graph keeps floating-point activations. DequantizeLinear expands
weights for ordinary floating operators; this is not an INT8-activation kernel
benchmark and runtime constant folding may expand resident weights.
"""
from collections import Counter
import gc
import hashlib
import json
from pathlib import Path
import time

import numpy as np
import onnx
from onnx import helper, numpy_helper, TensorProto
import torch
from diffusers import UNet2DConditionModel
from transformers import CLIPTextModel
from safetensors.torch import load_file
from variants import ROOT, VARIANT_ROOT, load_taesd

DEST = ROOT / "models" / "tiny-sd-onnx-stage2"
REPORT = ROOT / "outputs/tiny-sd/stage2"


class UnetExport(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, sample, timestep, encoder_hidden_states):
        return self.model(sample, timestep, encoder_hidden_states, return_dict=False)[0]


class TextExport(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, input_ids):
        return self.model(input_ids, return_dict=False)[0]


class DecoderExport(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, latents):
        return self.model.decode(latents, return_dict=False)[0]


def compact_graph(source_path, target_path, quanto_state=None):
    """Keep original initializer names and replace verified constants with DQ/Cast.

    INT8 rounding/scales are produced by Quanto, never recomputed here. Float16
    storage is allowed only when it round-trips to exactly the same float32 bits.
    """
    model = onnx.load(str(source_path))
    initializers, nodes, stats = [], [], Counter()
    for tensor in model.graph.initializer:
        original = numpy_helper.to_array(tensor)
        source_name = tensor.name.removeprefix("model.")
        data_key = source_name + "._data"
        scale_key = source_name + "._scale"
        if quanto_state is not None and data_key in quanto_state:
            quantized = quanto_state[data_key].numpy()
            scale = quanto_state[scale_key].numpy().reshape(-1)
            if quantized.shape != original.shape or scale.shape[0] != original.shape[0]:
                raise RuntimeError(f"Unsupported weight layout: {tensor.name}")
            data_name, scale_name, zero_name = tensor.name + ".int8", tensor.name + ".scale", tensor.name + ".zero"
            initializers.extend([numpy_helper.from_array(quantized, data_name),
                                 numpy_helper.from_array(scale, scale_name),
                                 numpy_helper.from_array(np.zeros_like(scale, dtype=np.int8), zero_name)])
            nodes.append(helper.make_node("DequantizeLinear", [data_name, scale_name, zero_name], [tensor.name],
                                          axis=0, name=tensor.name + ".DQ"))
            stats["int8_weights"] += 1
        elif original.dtype == np.float32 and np.array_equal(original.astype(np.float16).astype(np.float32), original):
            short_name = tensor.name + ".fp16"
            initializers.append(numpy_helper.from_array(original.astype(np.float16), short_name))
            nodes.append(helper.make_node("Cast", [short_name], [tensor.name], to=TensorProto.FLOAT,
                                          name=tensor.name + ".restore_fp32"))
            stats["lossless_fp16_constants"] += 1
        else:
            initializers.append(tensor)
            stats["unchanged_constants"] += 1
    old_nodes = list(model.graph.node)
    del model.graph.node[:]
    model.graph.node.extend(nodes + old_nodes)
    del model.graph.initializer[:]
    model.graph.initializer.extend(initializers)
    if quanto_state is not None:
        expected = sum(key.endswith("._data") for key in quanto_state)
        if stats["int8_weights"] != expected:
            raise RuntimeError(f"Only {stats['int8_weights']} of {expected} quantized weights matched ONNX graph")
    onnx.checker.check_model(model)
    onnx.save(model, str(target_path))
    return dict(stats)


def main():
    torch.set_grad_enabled(False)
    torch.set_num_threads(6)
    DEST.mkdir(parents=True, exist_ok=False)
    source = json.loads((ROOT / "outputs/tiny-sd/model-manifest.json").read_text())
    model_path = ROOT / "models/tiny-sd" / source["revision"]
    gen = torch.Generator("cpu").manual_seed(123)
    sample = torch.randn(2, 4, 64, 64, generator=gen)
    timestep = torch.tensor([500.0])
    embeddings = torch.randn(2, 77, 768, generator=gen)
    token_ids = torch.full((2, 77), 49407, dtype=torch.int64)
    token_ids[:, 0] = 49406
    latents = torch.randn(1, 4, 64, 64, generator=gen)
    jobs = [("unet", lambda: UnetExport(UNet2DConditionModel.from_pretrained(str(model_path / "unet"),
             torch_dtype=torch.float32, local_files_only=True, use_safetensors=False)),
             (sample, timestep, embeddings), ["sample", "timestep", "encoder_hidden_states"], "noise_pred"),
            ("text_encoder", lambda: TextExport(CLIPTextModel.from_pretrained(str(model_path / "text_encoder"),
             torch_dtype=torch.float32, local_files_only=True, attn_implementation="eager")),
             (token_ids,), ["input_ids"], "hidden_states"),
            ("taesd_decoder", lambda: DecoderExport(load_taesd()[0]), (latents,), ["latents"], "image")]
    records = []
    for name, factory, inputs, input_names, output_name in jobs:
        started = time.perf_counter()
        model = factory().eval()
        expected = model(*inputs).detach().numpy()
        output = DEST / f"{name}-fp32.onnx"
        torch.onnx.export(model, inputs, str(output), input_names=input_names, output_names=[output_name],
                          opset_version=17, dynamo=False, do_constant_folding=False)
        onnx.checker.check_model(str(output))
        np.savez(DEST / f"{name}-reference.npz", expected=expected,
                 **{key: val.numpy() for key, val in zip(input_names, inputs)})
        del model
        gc.collect()
        quantized = load_file(str(VARIANT_ROOT / "unet-int8/model.safetensors")) if name == "unet" else None
        compact = DEST / f"{name}-{'w8' if name == 'unet' else 'compact'}.onnx"
        stats = compact_graph(output, compact, quantized)
        del quantized
        gc.collect()
        records.append({"component": name, "export_seconds": time.perf_counter() - started,
                        "original_bytes": output.stat().st_size, "compact_bytes": compact.stat().st_size,
                        "compaction": stats})
        print(json.dumps(records[-1]), flush=True)
    (REPORT / "onnx-export.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
    print("ONNX export completed", flush=True)


if __name__ == "__main__":
    main()
