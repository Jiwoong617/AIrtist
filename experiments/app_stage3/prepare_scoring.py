"""Export the published MIT-licensed TinyCLIP checkpoint, with no training."""
import json
from pathlib import Path
import numpy as np
import onnxruntime as ort
from PIL import Image
import torch
from transformers import CLIPModel, CLIPTokenizerFast, CLIPImageProcessor

ROOT = Path(__file__).resolve().parents[2]
REVISION = "a2a8c6eaa2549ad66eb7c31b85022bf58273a26c"


def main():
    torch.set_grad_enabled(False)
    torch.set_num_threads(6)
    source = ROOT / "models/tinyclip" / REVISION
    folder = ROOT / "models/app-stage3/scoring"
    results = ROOT / "outputs/app-stage3"
    folder.mkdir(parents=True, exist_ok=True)
    results.mkdir(parents=True, exist_ok=True)
    model = CLIPModel.from_pretrained(source, local_files_only=True, attn_implementation="eager").eval()
    tokenizer = CLIPTokenizerFast(tokenizer_file=str(source / "tokenizer.json"))
    preprocess = CLIPImageProcessor.from_pretrained(source, local_files_only=True)
    words = json.loads((ROOT / "data/words.json").read_text(encoding="utf-8"))
    texts = [f"a photo of a {w['english']}" for w in words]
    vectors = model.get_text_features(**tokenizer(texts, padding=True, return_tensors="pt"))
    vectors /= vectors.norm(dim=-1, keepdim=True)
    data = {"model": "TinyCLIP-ViT-8M-16-Text-3M-YFCC15M", "revision": REVISION, "prompt_template": "a photo of a {english}",
            "words": words, "vectors": vectors.tolist(), "preprocess": preprocess.to_dict(),
            "score_floor": 0.1, "score_ceiling": 0.4, "score_mapping_provisional": True}
    (folder / "vectors.json").write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")

    class Encoder(torch.nn.Module):
        def __init__(self):
            super().__init__()
            self.vision = model.vision_model
            self.projection = model.visual_projection

        def forward(self, pixels):
            return self.projection(self.vision(pixel_values=pixels)[1])

    path = folder / "tinyclip-image.onnx"
    if not path.exists():
        torch.onnx.export(Encoder().eval(), torch.zeros(1, 3, 224, 224), str(path), input_names=["pixels"], output_names=["embedding"], opset_version=17, dynamo=False)
    options = ort.SessionOptions()
    options.intra_op_num_threads = 6
    session = ort.InferenceSession(str(path), sess_options=options, providers=["CPUExecutionProvider"])
    rows = []
    for image in sorted((ROOT / "outputs/tiny-sd/runs/stage2-onnx-pipeline-02/images").glob("*.png")):
        tensor = preprocess(images=Image.open(image).convert("RGB"), return_tensors="pt").pixel_values
        expected = model.get_image_features(pixel_values=tensor).numpy()[0]
        actual = session.run(None, {"pixels": tensor.numpy()})[0][0]
        error = float(np.max(np.abs(actual - expected)))
        if error > 0.001:
            raise ValueError(f"TinyCLIP export error: {error}")
        actual /= np.linalg.norm(actual)
        rows.append({"image": str(image.relative_to(ROOT)), "max_abs_error": error, "cosines": (vectors.numpy() @ actual).tolist()})
        tensor.numpy().tofile(results / (image.stem + "-scoring-input.f32"))
    (results / "scoring-reference.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    print("TinyCLIP image bytes", path.stat().st_size, "preprocess", preprocess, flush=True)


if __name__ == "__main__":
    main()
