"""Compare the public checkpoint on predeclared descriptions without target words."""
import argparse
import json
from pathlib import Path
import re
import time
import torch
from transformers import MarianMTModel
from candidate import tokenizer as corrected_tokenizer

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "models/opus-mt-tc-big-ko-en/fa26583a41d95346933f26b4cb8f9b700da0d445"
OUT = ROOT / "outputs/translation-quality"


def cases(split):
    if split == "development":
        return json.loads((ROOT / "experiments/translation_quality/development.json").read_text(encoding="utf-8"))
    return json.loads((ROOT / "experiments/translation_quality/holdout.json").read_text(encoding="utf-8"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("split", choices=["development", "holdout"])
    parser.add_argument("--beams", type=int, nargs="+", default=[1, 4])
    args = parser.parse_args()
    torch.set_grad_enabled(False)
    torch.set_num_threads(6)
    tokenizer = corrected_tokenizer()
    model = MarianMTModel.from_pretrained(SOURCE, local_files_only=True, attn_implementation="eager", dtype=torch.float32).eval()
    OUT.mkdir(parents=True, exist_ok=True)
    rows = []
    for case in cases(args.split):
        source = case["source"]
        inputs = tokenizer(source, return_tensors="pt")
        for beams in args.beams:
            watch = time.perf_counter()
            if not re.search(r"[가-힣ㄱ-ㅎㅏ-ㅣ]", source):
                actual = source
                ids = []
            else:
                ids = model.generate(**inputs, num_beams=beams, max_new_tokens=64)[0].tolist()
                actual = tokenizer.decode(ids, skip_special_tokens=True)
            rows.append({"source": source, "beams": beams, "actual": actual, "seconds": time.perf_counter()-watch,
                         "input_ids": inputs.input_ids[0].tolist(), "output_ids": ids})
            (OUT / f"source-{args.split}.json").write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps(rows[-1], ensure_ascii=True), flush=True)


if __name__ == "__main__":
    main()
