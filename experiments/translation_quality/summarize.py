"""Keep measurements separate from semantic judgments and package claims."""
import hashlib
import json
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "outputs/translation-quality"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def timing(rows, key):
    seconds = [row["seconds"] for row in rows if any("가" <= c <= "힣" for c in row["source"])]
    return {"scope": key, "count": len(seconds), "mean_seconds": statistics.mean(seconds),
            "min_seconds": min(seconds), "max_seconds": max(seconds)}


report = {"candidate": read(ROOT / "experiments/translation_quality/source.json"),
          "conversion": read(OUT / "conversion.json"), "splits": {}, "decision": "not_adopted",
          "reason": "Critical ambiguity/negation errors and new shape/tail omissions remain; no application assets replaced.",
          "notes": ["Exact text agreement is not semantic accuracy.",
                    "Warm ONNX timing excludes session creation; C# baseline includes it.",
                    "Only the model file size was measured; no candidate app package was built."]}
model_path = ROOT / "models/opus-mt-tc-big-ko-en/converted/step-mixed.onnx"
with model_path.open("rb") as stream:
    report["conversion"]["sha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
for split in ["development", "holdout"]:
    source = {r["source"]: r["actual"] for r in read(OUT / f"source-{split}.json") if r["beams"] == 1}
    full = read(OUT / f"onnx-fp32-{split}.json")
    compact = read(OUT / f"compact-{split}.json")
    cases = read(ROOT / f"experiments/translation_quality/{split}.json")
    if len(source) != len(cases) or len(full) != len(cases) or len(compact) != len(cases):
        raise ValueError("Incomplete evaluation")
    if any(row["compact"] != source[row["source"]] for row in full):
        raise ValueError("FP32 ONNX export differs from source greedy decoding")
    logs = (OUT / f"compact-{split}.log").read_text(encoding="utf-8-sig").splitlines()
    report["splits"][split] = {"count": len(cases), "fp32_export_exact_matches": len(full),
          "compact_exact_matches": sum(row["compact"] == source[row["source"]] for row in compact),
          "compact_load_seconds": json.loads(logs[-1])["load_seconds"],
          "warm_timing": timing(compact, "Python ORT CPU; reused session"),
          "changed_translations": [{"source": row["source"], "fp32": source[row["source"]], "compact": row["compact"]}
                                   for row in compact if row["compact"] != source[row["source"]]]}
report["baseline_holdout_timing"] = timing(read(OUT / "csharp-baseline-holdout.json"), "Existing C# app translator, including each session load")
(OUT / "summary.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({key: value for key, value in report.items() if key != "candidate"}, ensure_ascii=True, indent=2))
