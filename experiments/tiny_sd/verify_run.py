"""Verify completed evidence and input isolation; does not assign quality scores."""
import argparse
import hashlib
import json
from pathlib import Path
import unicodedata

from PIL import Image


def normalize(text):
    return "".join(unicodedata.normalize("NFKC", text).casefold().split())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("run", type=Path)
    args = parser.parse_args()
    summary = json.loads((args.run / "summary.json").read_text(encoding="utf-8"))
    environment = json.loads((args.run / "environment.json").read_text(encoding="utf-8"))
    prompts = json.loads((args.run / "prompts.json").read_text(encoding="utf-8"))
    reviews = json.loads(Path(__file__).with_name("review_cases.json").read_text(encoding="utf-8"))
    rows = [json.loads(line) for line in (args.run / "metrics.jsonl").read_text(encoding="utf-8").splitlines()]
    assert summary["status"] == "completed", "Run is not complete"
    assert summary["generations"] == len(rows)
    cases = {c["id"]: c for c in prompts["cases"]}
    aliases = {c["id"]: c["forbidden"] for c in reviews["cases"]}
    quality_keys = []
    for row in rows:
        case = cases[row["case_id"]]
        assert set(case) == {"id", "ko", "en"}, "Generation input contains unexpected metadata"
        assert row["prompt"] == case[row["language"]] + ", " + prompts["style"]
        assert not any(normalize(a) in normalize(row["prompt"]) for a in aliases[row["case_id"]])
        assert row["status"] == "ok" and not row["truncated"] and not row["uniform_image"]
        assert row["generation_seconds"] > 0
        path = args.run / row["image"]
        assert hashlib.sha256(path.read_bytes()).hexdigest() == row["image_sha256"]
        with Image.open(path) as image:
            assert image.size == (512, 512)
            assert hashlib.sha256(image.tobytes()).hexdigest() == row["pixel_sha256"]
        if row["kind"] == "quality":
            quality_keys.append((row["case_id"], row["language"], row["seed"]))
        if row.get("latent_file"):
            import numpy as np
            latent_path = (args.run / row["latent_file"]).resolve()
            assert latent_path.is_relative_to(args.run.resolve())
            assert hashlib.sha256(latent_path.read_bytes()).hexdigest() == row["latent_file_sha256"]
            latent = np.load(latent_path, allow_pickle=False)
            assert latent.shape == (1, 4, 64, 64) and latent.dtype == np.float32 and np.isfinite(latent).all()
            assert hashlib.sha256(latent.tobytes()).hexdigest() == row["final_latent_sha256"]
    assert len(set(quality_keys)) == len(quality_keys), "Duplicate quality sample"
    used_cases = {key[0] for key in quality_keys}
    expected = {(case, language, seed) for case in used_cases
                for language in ("ko", "en") for seed in environment["settings"]["seeds"]}
    assert set(quality_keys) == expected, "Incomplete paired matrix"
    assert hashlib.sha256((args.run / "benchmark-source.py").read_bytes()).hexdigest() == environment["benchmark_sha256"]
    if environment["settings"].get("variant", "base") != "base":
        assert hashlib.sha256((args.run / "variant-source.py").read_bytes()).hexdigest() == environment["variant_source_sha256"]
        assert all(len(row.get("final_latent_sha256", "")) == 64 for row in rows), "Missing final latent evidence"
    if environment.get("resumed_from"):
        assert hashlib.sha256((args.run / "previous-benchmark-source.py").read_bytes()).hexdigest() == environment["previous_benchmark_sha256"]
        assert sum(bool(row.get("resumed")) for row in rows) == summary["resumed_generations"]
    result = {"status": "passed", "saved_images": len(rows), "quality_images": len(quality_keys),
              "case_count": len(used_cases), "image_dimensions": [512, 512],
              "checks": ["complete paired matrix", "no duplicate quality samples", "no target aliases in model prompts",
                         "no truncation or uniform output", "PNG and pixel hashes", "executed script snapshot hash"],
              "limitations": "Alias check is exact NFKC/case/whitespace matching for this curated experiment, not the product input validator. No automated semantic quality score."}
    (args.run / "verification.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
