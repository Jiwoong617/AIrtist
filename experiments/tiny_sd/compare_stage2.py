"""Compare matched images, measured size, and decoder isolation across variants."""
import argparse
import html
import json
import math
from pathlib import Path
import statistics

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
RUNS = ROOT / "outputs/tiny-sd/runs"
LABELS = {"base": "Original FP32", "taesd": "TAESD", "int8": "INT8 U-Net", "int8-taesd": "INT8 + TAESD"}
PATHS = {"base": RUNS / "baseline-fp32-20260913-01", **{v: RUNS / f"stage2-{v}-offload-02" for v in ["taesd", "int8", "int8-taesd"]}}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=RUNS / "stage2-comparison-01")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    records, summaries = {}, {}
    for variant, path in PATHS.items():
        metrics = path / "metrics.jsonl"
        if metrics.exists():
            records[variant] = {(r["case_id"], r["language"], r["seed"]): r
                                for line in metrics.read_text(encoding="utf-8").splitlines()
                                if (r := json.loads(line))["kind"] == "quality" and r["status"] == "ok"}
        else:
            records[variant] = {}
        if (path / "summary.json").exists():
            summaries[variant] = json.loads((path / "summary.json").read_text())
    prompts = json.loads((PATHS["base"] / "prompts.json").read_text(encoding="utf-8"))
    cases = {c["id"]: c for c in prompts["cases"]}
    font = ImageFont.truetype("C:/Windows/Fonts/malgun.ttf", 15)
    small = ImageFont.truetype("C:/Windows/Fonts/malgun.ttf", 12)
    sections, differences = [], []
    # Keep every original quality sample. Original failed semantics remain visible.
    for language in ["en", "ko"]:
        keys = [key for key in records["base"] if key[1] == language]
        for offset in range(0, len(keys), 3):
            subset = keys[offset:offset + 3]
            sheet = Image.new("RGB", (1200, 385 * len(subset)), "#eef2f6")
            draw = ImageDraw.Draw(sheet)
            for row_index, key in enumerate(subset):
                y = row_index * 385
                case_id, lang, seed = key
                draw.text((8, y + 6), f"{case_id} | {lang} | seed {seed} | {cases[case_id]['ko']}", fill="#111827", font=font)
                with Image.open(PATHS["base"] / records["base"][key]["image"]) as img:
                    original = np.asarray(img, dtype=np.float32)
                cards = []
                for column, (variant, label) in enumerate(LABELS.items()):
                    draw.text((column * 300 + 8, y + 31), label, fill="#334155", font=font)
                    record = records[variant].get(key)
                    if record is None:
                        draw.text((column * 300 + 8, y + 160), "Pending", fill="#64748b", font=font)
                        continue
                    path = PATHS[variant] / record["image"]
                    with Image.open(path) as img:
                        sheet.paste(img.resize((288, 288), Image.Resampling.LANCZOS), (column * 300 + 6, y + 59))
                        actual = np.asarray(img, dtype=np.float32)
                    rmse = float(np.sqrt(np.mean((actual - original) ** 2)))
                    psnr = 20 * math.log10(255 / rmse) if rmse else None
                    if variant != "base":
                        differences.append({"variant": variant, "case_id": case_id, "language": lang,
                                            "seed": seed, "pixel_rmse_0_255": rmse, "psnr_db": psnr})
                    timing_label = "decode " if record.get("timing_scope") == "decoder_only" else ""
                    draw.text((column * 300 + 8, y + 355), f"{timing_label}{record['generation_seconds']:.2f}s | pixel RMSE {rmse:.2f}",
                              fill="#334155", font=small)
                    relative = Path("..") / PATHS[variant].name / record["image"]
                    src = html.escape(relative.as_posix(), quote=True)
                    cards.append(f'<figure><figcaption>{html.escape(label)}</figcaption><a href="{src}"><img loading="lazy" src="{src}"></a>'
                                 f'<small>{timing_label}{record["generation_seconds"]:.2f}s · RMSE {rmse:.2f}</small></figure>')
                sections.append(f'<section data-language="{lang}"><h2>{html.escape(case_id)} · {lang} · seed {seed}</h2>'
                                f'<p>{html.escape(cases[case_id][lang])}</p><div class="grid">{"".join(cards)}</div></section>')
            sheet.save(args.output / f"comparison-{language}-{offset // 3 + 1:02d}.png")
    matched = set(records["int8"]) & set(records["int8-taesd"])
    isolation = {"matched_int8_vs_combined_cases": len(matched), "identical_final_latents":
                 sum(bool(records["int8"][k].get("final_latent_sha256")) and
                     records["int8"][k]["final_latent_sha256"] == records["int8-taesd"][k].get("final_latent_sha256") for k in matched)}
    isolation["method"] = "Combined quality images decode the saved, hash-verified INT8 final latents; identity is controlled by construction. Independent combined full inference is a separate smoke run."
    result = {"completed_quality_images": {v: len(rows) for v, rows in records.items()}, "summaries": summaries,
              "decoder_isolation": isolation,
              "pixel_difference_means": {variant: statistics.mean(r["pixel_rmse_0_255"] for r in differences if r["variant"] == variant)
                                         for variant in LABELS if any(r["variant"] == variant for r in differences)},
              "pixel_difference_rows": differences,
              "note": "Pixel RMSE and PSNR describe image changes, not semantic correctness, game score, or quality acceptance."}
    (args.output / "comparison.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    document = '''<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Tiny-SD 경량화 비교</title><style>body{font:16px/1.6 system-ui;max-width:1440px;margin:36px auto;padding:0 24px;background:#eef2f6;color:#182132}
section{padding:20px;margin:24px 0;background:white;border-radius:12px}h1{font-size:28px}h2{font-size:18px}.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}
figure{margin:0}img{width:100%}small{color:#526070}button{padding:9px 20px;margin-right:8px}nav{position:sticky;top:0;background:#eef2f6;padding:12px 0}
@media(max-width:800px){.grid{grid-template-columns:repeat(2,1fr)}}</style><h1>Tiny-SD 원본 · 경량화 비교</h1>
<p>동일 묘사·시드·25단계·512×512. 모든 구성은 FP32 활성값을 사용합니다. RMSE는 픽셀 차이이며 의미 정확도가 아닙니다.</p>
<p>원본 측정과 이번 측정의 PC 부하가 다르므로 표시 시간만으로 가속률을 결론내리지 않습니다.</p>
<p>2단계는 부품별 CPU offload를 사용합니다. TAESD는 중단 전 완료한 33개 품질 표본을 재사용했으며 나머지 15개만 offload로 생성했습니다.</p>
<p>INT8+TAESD 열은 INT8 실행에서 저장한 동일 잠재값을 TAESD로 복원한 결과입니다. decode 시간은 복원만 포함하며 전체 생성 시간이 아닙니다.</p>
<nav><button onclick="filter('en')">영어</button><button onclick="filter('ko')">한국어</button><button onclick="filter('all')">전체</button></nav>
''' + "".join(sections) + '''<script>function filter(lang){document.querySelectorAll('section').forEach(s=>s.hidden=lang!=='all'&&s.dataset.language!==lang)}filter('en')</script></html>'''
    (args.output / "gallery.html").write_text(document, encoding="utf-8")
    print(json.dumps({k: result[k] for k in ["completed_quality_images", "decoder_isolation", "pixel_difference_means"]}))


if __name__ == "__main__":
    main()
