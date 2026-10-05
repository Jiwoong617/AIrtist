"""Offline, frozen-checkpoint prompt comparison; no training or model downloads."""
import argparse
import json
from pathlib import Path
import numpy as np
import torch
from PIL import Image
from transformers import CLIPModel, CLIPTokenizerFast, CLIPImageProcessor

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "outputs/app-quality"
REVISION = "a2a8c6eaa2549ad66eb7c31b85022bf58273a26c"


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["development", "holdout"])
    parser.add_argument("--model", choices=["8m", "40m"], default="8m")
    args = parser.parse_args()
    torch.set_grad_enabled(False)
    torch.set_num_threads(4)
    revision = REVISION if args.model == "8m" else "95ec8197b3f2fe7f747865c61ca556cf0768b2f7"
    source = ROOT / ("models/tinyclip" if args.model == "8m" else "models/tinyclip-40m") / revision
    output = OUT / args.model
    model = CLIPModel.from_pretrained(source, local_files_only=True, attn_implementation="eager").eval()
    tokenizer = CLIPTokenizerFast(tokenizer_file=str(source / "tokenizer.json"))
    processor = CLIPImageProcessor.from_pretrained(source, local_files_only=True)
    old = {"words": json.loads((ROOT / "data/words.json").read_text(encoding="utf-8")), "preprocess": processor.to_dict()}

    def encode(texts):
        vectors = model.get_text_features(**tokenizer(texts, padding=True, return_tensors="pt"))
        return vectors / vectors.norm(dim=-1, keepdim=True)

    baseline_vectors = encode([f"a photo of a {word['english']}" for word in old["words"]]).numpy() if args.model == "8m" else None

    if args.mode == "development":
        templates = ["a photo of a {}", "a drawing of a {}", "an illustration of a {}"]
        objects = [word["english"] for word in old["words"]]
        confounds = ["pear", "tomato", "orange", "lemon", "peach", "bowl", "vase", "bottle", "lion", "cat", "fox", "wolf", "mouse", "squirrel", "horse", "bus", "truck", "bicycle", "motorcycle", "chair", "hat", "balloon", "ball", "box"]
        nulls = ["a blank image", "a solid color background", "random noise", "abstract geometric shapes", "written text", "printed letters", "a word on a white background"]
        if args.model == "40m":
            nulls += [f"the word {name} written in text" for name in objects]
            nulls += [f"the printed word {name} on a white background" for name in objects]
        vectors = torch.stack([encode([template.format(name) for template in templates]).mean(0) for name in objects + confounds])
        vectors /= vectors.norm(dim=-1, keepdim=True)
        background = encode(nulls)
        data = {**old, "model": "TinyCLIP-ViT-8M-16-Text-3M-YFCC15M" if args.model == "8m" else "TinyCLIP-ViT-40M-32-Text-19M-LAION400M", "revision": revision, "prompt_templates": templates,
                "vectors": vectors[:len(objects)].tolist(),
                "contrast_labels": confounds + nulls,
                "contrast_vectors": torch.cat([vectors[len(objects):], background]).tolist(),
                "score_floor": 0.10, "score_ceiling": 0.40, "margin_floor": -0.02, "margin_ceiling": 0.06,
                "score_version": "tinyclip-contrast-v1", "score_mapping_provisional": True}
        data.pop("prompt_template", None)
        save(output / "vectors-candidate.json", data)
        cases = json.loads((ROOT / "outputs/app-stage4/quality/scores.json").read_text(encoding="utf-8"))
        for row in cases:
            row["path"] = "outputs/app-stage4/quality/" + row["name"] + ".png"
        # The red fruit is visually ambiguous (apple/tomato); not a labelled positive.
        cases = [row for row in cases if row["name"] != "generated-red-fruit"]
    else:
        data = json.loads((output / "vectors-candidate.json").read_text(encoding="utf-8"))
        cases = json.loads((ROOT / "experiments/app_quality/scoring-holdout.json").read_text(encoding="utf-8"))

    positives, negatives = np.array(data["vectors"]), np.array(data["contrast_vectors"])
    rows = []
    for case in cases:
        pixels = processor(images=Image.open(ROOT / case["path"]).convert("RGB"), return_tensors="pt").pixel_values
        vector = model.get_image_features(pixel_values=pixels)[0].numpy()
        vector /= np.linalg.norm(vector)
        cosines = positives @ vector
        contrasts = negatives @ vector
        ranking = []
        for i, word in enumerate(data["words"]):
            competitor = max(np.max(np.delete(cosines, i)), np.max(contrasts))
            absolute = np.clip((cosines[i] - data["score_floor"]) / (data["score_ceiling"] - data["score_floor"]), 0, 1)
            margin = cosines[i] - competitor
            score = absolute * np.clip((margin - data["margin_floor"]) / (data["margin_ceiling"] - data["margin_floor"]), 0, 1) * 100
            baseline = np.clip((np.dot(baseline_vectors[i], vector) - .1) / .3, 0, 1) * 100 if baseline_vectors is not None else None
            ranking.append({"id": word["id"], "cosine": float(cosines[i]), "margin": float(margin), "score": float(score), "baseline": None if baseline is None else float(baseline)})
        ranking.sort(key=lambda row: row["score"], reverse=True)
        row = {"name": case["name"], "path": case["path"], "category": case["category"], "expected": case.get("expected"), "ranking": ranking,
               "strongest_contrast": data["contrast_labels"][np.argmax(contrasts)], "contrast_cosine": float(max(contrasts))}
        rows.append(row)
        selected = next((r for r in ranking if r["id"] == case.get("expected")), ranking[0])
        print(case["name"], case.get("expected"), selected, row["strongest_contrast"], flush=True)
    save(output / f"scoring-{args.mode}.json", rows)


if __name__ == "__main__":
    main()
