"""Post-training weight storage conversion. No learning or calibration corpus."""
import json
import logging
from pathlib import Path
import numpy as np
import onnx
from onnx import helper, numpy_helper, TensorProto
from onnxruntime.quantization.matmul_nbits_quantizer import MatMulNBitsQuantizer
import torch
from transformers import MarianMTModel
from candidate import SOURCE, DERIVED, tokenizer


def main():
    torch.set_num_threads(6)
    torch.set_grad_enabled(False)
    logging.getLogger("onnxruntime.quantization.matmul_nbits_quantizer").setLevel(logging.WARNING)
    DERIVED.mkdir(parents=True, exist_ok=True)
    full = DERIVED / "step-fp32.onnx"
    compact = DERIVED / "step-mixed.onnx"
    if not full.exists():
        tokens = tokenizer()
        model = MarianMTModel.from_pretrained(SOURCE, local_files_only=True,
                     attn_implementation="eager", dtype=torch.float32).eval()

        class Step(torch.nn.Module):
            def __init__(self):
                super().__init__()
                self.model = model

            def forward(self, input_ids, attention_mask, decoder_input_ids):
                return self.model(input_ids=input_ids, attention_mask=attention_mask,
                                  decoder_input_ids=decoder_input_ids, use_cache=False,
                                  return_dict=False)[0][:, -1, :]

        inputs = tokens("아삭아삭하고 빨간 과일", return_tensors="pt")
        torch.onnx.export(Step().eval(), (inputs.input_ids, inputs.attention_mask, torch.tensor([[32000, 10, 15]])),
                          str(full), input_names=["input_ids", "attention_mask", "decoder_input_ids"],
                          output_names=["logits"], dynamic_axes={"input_ids": {1: "source_length"},
                          "attention_mask": {1: "source_length"}, "decoder_input_ids": {1: "target_length"}},
                          opset_version=17, dynamo=False)
        del model
    graph = onnx.load(full)
    weights = {t.name: numpy_helper.to_array(t) for t in graph.graph.initializer}
    shared_name = "model.model.shared.weight"
    shared = weights[shared_name]
    # One copy of the shared embedding, including its transposed output projection.
    transposes = []
    retained = []
    for tensor in graph.graph.initializer:
        values = weights[tensor.name]
        if values.shape == shared.T.shape and np.array_equal(values, shared.T):
            transposes.append(helper.make_node("Transpose", [shared_name], [tensor.name], perm=[1, 0]))
        else:
            retained.append(tensor)
    del graph.graph.initializer[:]
    graph.graph.initializer.extend(retained)
    graph.graph.node.extend(transposes)
    del weights, shared
    # Only feed-forward matrices use 4-bit blocks; attention and embeddings stay INT8.
    selected = {node.name for node in graph.graph.node if node.op_type == "MatMul"
                and ("/fc1/" in node.name or "/fc2/" in node.name)}
    if len(selected) != 24:
        raise ValueError(f"Unexpected FFN matrix count: {len(selected)}")
    quantizer = MatMulNBitsQuantizer(graph, block_size=32, is_symmetric=True,
                       nodes_to_exclude=[node.name for node in graph.graph.node if node.name not in selected])
    quantizer.process()
    graph = quantizer.model.model
    initializers, prefixes = [], []
    stats = {"int4_ffn": len(selected), "int8": 0, "fp16": 0}
    for tensor in graph.graph.initializer:
        values = numpy_helper.to_array(tensor)
        if values.dtype == np.float32 and values.ndim == 2 and values.size > 4096 and not tensor.name.endswith("_scales"):
            axis = 0 if tensor.name == shared_name else 1
            scale = np.maximum(np.max(np.abs(values), axis=1-axis) / 127, np.finfo(np.float32).tiny)
            shape = [-1, 1] if axis == 0 else [1, -1]
            quantized = np.clip(np.rint(values / scale.reshape(shape)), -127, 127).astype(np.int8)
            names = [tensor.name + suffix for suffix in [".int8", ".scale", ".zero"]]
            initializers.extend([numpy_helper.from_array(quantized, names[0]), numpy_helper.from_array(scale, names[1]),
                                 numpy_helper.from_array(np.zeros_like(scale, dtype=np.int8), names[2])])
            prefixes.append(helper.make_node("DequantizeLinear", names, [tensor.name], axis=axis))
            stats["int8"] += 1
        elif values.dtype == np.float32 and values.size > 1 and not tensor.name.endswith("_scales"):
            name = tensor.name + ".fp16"
            initializers.append(numpy_helper.from_array(values.astype(np.float16), name))
            prefixes.append(helper.make_node("Cast", [name], [tensor.name], to=TensorProto.FLOAT))
            stats["fp16"] += 1
        else:
            initializers.append(tensor)
    nodes = list(graph.graph.node)
    # Shared transpose must precede the final projection.
    transpose_names = {tuple(n.output) for n in transposes}
    nodes = [n for n in nodes if tuple(n.output) not in transpose_names]
    del graph.graph.node[:]
    del graph.graph.initializer[:]
    graph.graph.node.extend(prefixes + transposes + nodes)
    graph.graph.initializer.extend(initializers)
    onnx.checker.check_model(graph)
    onnx.save(graph, compact)
    report = {"fp32_bytes": full.stat().st_size, "compact_bytes": compact.stat().st_size, "weights": stats,
              "int4_block_size": 32, "activations": "FP32", "decoding": "greedy", "calibration_or_training": False}
    report_path = Path(__file__).resolve().parents[2] / "outputs/translation-quality/conversion.json"
    report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)


if __name__ == "__main__":
    main()
