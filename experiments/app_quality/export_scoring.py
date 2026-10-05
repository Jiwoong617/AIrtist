"""Weight-only INT8 storage, FP32 activations; no calibration or training."""
import hashlib
import json
from pathlib import Path
import numpy as np
import onnx
from onnx import helper, numpy_helper, TensorProto
import onnxruntime as ort
from PIL import Image
import torch
from transformers import CLIPModel, CLIPImageProcessor

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "outputs/app-quality/40m"
SOURCE = ROOT / "models/tinyclip-40m/95ec8197b3f2fe7f747865c61ca556cf0768b2f7"
MODEL_OUT = ROOT / "models/tinyclip-40m/converted"
MODEL_OUT.mkdir(parents=True, exist_ok=True)
torch.set_grad_enabled(False)
torch.set_num_threads(4)
model = CLIPModel.from_pretrained(SOURCE, local_files_only=True, attn_implementation="eager").eval()
processor = CLIPImageProcessor.from_pretrained(SOURCE, local_files_only=True)


class Encoder(torch.nn.Module):
    def __init__(self):
        super().__init__()
        self.vision = model.vision_model
        self.projection = model.visual_projection

    def forward(self, pixels):
        return self.projection(self.vision(pixel_values=pixels)[1])


full = MODEL_OUT / "image-fp32.onnx"
compact = MODEL_OUT / "image-w8.onnx"
torch.onnx.export(Encoder().eval(), torch.zeros(1, 3, 224, 224), str(full),
                  input_names=["pixels"], output_names=["embedding"], opset_version=17, dynamo=False)
graph = onnx.load(full)
initializers, prefixes, stats = [], [], {"int8": 0, "fp16": 0, "unchanged": 0}
for tensor in graph.graph.initializer:
    values = numpy_helper.to_array(tensor)
    if values.dtype == np.float32 and values.ndim >= 2 and values.size > 1024:
        axis = 1 if values.ndim == 2 else 0
        reduction = tuple(i for i in range(values.ndim) if i != axis)
        scale = np.maximum(np.max(np.abs(values), axis=reduction) / 127, np.finfo(np.float32).tiny)
        shape = [1] * values.ndim
        shape[axis] = len(scale)
        quantized = np.clip(np.rint(values / scale.reshape(shape)), -127, 127).astype(np.int8)
        names = [tensor.name + suffix for suffix in [".int8", ".scale", ".zero"]]
        initializers.extend([numpy_helper.from_array(quantized, names[0]), numpy_helper.from_array(scale, names[1]),
                             numpy_helper.from_array(np.zeros_like(scale, dtype=np.int8), names[2])])
        prefixes.append(helper.make_node("DequantizeLinear", names, [tensor.name], axis=axis))
        stats["int8"] += 1
    elif values.dtype == np.float32 and values.size > 1:
        name = tensor.name + ".fp16"
        initializers.append(numpy_helper.from_array(values.astype(np.float16), name))
        prefixes.append(helper.make_node("Cast", [name], [tensor.name], to=TensorProto.FLOAT))
        stats["fp16"] += 1
    else:
        initializers.append(tensor)
        stats["unchanged"] += 1
nodes = list(graph.graph.node)
del graph.graph.node[:]
del graph.graph.initializer[:]
graph.graph.node.extend(prefixes + nodes)
graph.graph.initializer.extend(initializers)
onnx.checker.check_model(graph)
onnx.save(graph, compact)
options = ort.SessionOptions()
options.intra_op_num_threads = 4
session = ort.InferenceSession(str(compact), sess_options=options, providers=["CPUExecutionProvider"])
vectors = json.loads((OUT / "vectors-candidate.json").read_text(encoding="utf-8"))
positive = np.array(vectors["vectors"])
rows = []
paths = sorted((ROOT / "outputs/app-stage4/quality").glob("*.png"))
paths += sorted((ROOT / "outputs/tiny-sd/runs/stage2-onnx-pipeline-02/images").glob("*.png"))
for path in paths:
    pixels = processor(images=Image.open(path).convert("RGB"), return_tensors="pt").pixel_values
    expected = model.get_image_features(pixel_values=pixels)[0].numpy()
    actual = session.run(None, {"pixels": pixels.numpy()})[0][0]
    expected /= np.linalg.norm(expected)
    actual /= np.linalg.norm(actual)
    error = float(np.max(np.abs(positive @ actual - positive @ expected)))
    rows.append({"image": str(path.relative_to(ROOT)).replace("\\", "/"), "cosine_error": error,
                 "cosines": (positive @ actual).tolist()})
    if error > .01:
        raise RuntimeError(f"Excessive weight-only quantization error: {path.name}, {error}")
(OUT / "scoring-reference.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
result = {"fp32_bytes": full.stat().st_size, "compact_bytes": compact.stat().st_size,
          "sha256": hashlib.sha256(compact.read_bytes()).hexdigest(), "storage": "INT8 per output channel; FP16 small tensors",
          "computation": "FP32", "weights": stats, "images": len(rows), "max_cosine_error": max(row["cosine_error"] for row in rows)}
(OUT / "conversion.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result, indent=2), flush=True)
