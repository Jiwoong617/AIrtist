"""Build a local evidence gallery and labeled contact sheets from recorded PNGs."""
import argparse
import html
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("run", type=Path)
    args = parser.parse_args()
    rows = [json.loads(line) for line in (args.run / "metrics.jsonl").read_text(encoding="utf-8").splitlines()]
    rows = [r for r in rows if r["status"] == "ok" and r["kind"] == "quality"]
    prompts = json.loads((args.run / "prompts.json").read_text(encoding="utf-8"))
    cases = {c["id"]: c for c in prompts["cases"]}
    by_case = {}
    for row in rows:
        by_case.setdefault(row["case_id"], []).append(row)
    font = ImageFont.truetype("C:/Windows/Fonts/malgun.ttf", 16)
    small = ImageFont.truetype("C:/Windows/Fonts/malgun.ttf", 13)
    sections = []
    case_ids = list(by_case)
    for offset in range(0, len(case_ids), 3):
        subset = case_ids[offset:offset + 3]
        # Columns: Korean seed 1, English seed 1, Korean seed 2, English seed 2.
        sheet = Image.new("RGB", (1200, 400 * len(subset)), "#f0f2f5")
        draw = ImageDraw.Draw(sheet)
        for y_index, case_id in enumerate(subset):
            y = y_index * 400
            draw.text((10, y + 8), f"{case_id} | {cases[case_id]['ko']}", font=font, fill="#111827")
            draw.text((10, y + 34), cases[case_id]["en"], font=small, fill="#374151")
            for x_index, row in enumerate(by_case[case_id]):
                with Image.open(args.run / row["image"]) as img:
                    sheet.paste(img.resize((288, 288), Image.Resampling.LANCZOS), (x_index * 300 + 6, y + 65))
                draw.text((x_index * 300 + 6, y + 360),
                          f"{row['language']} | seed {row['seed']} | {row['generation_seconds']:.2f}s",
                          font=small, fill="#111827")
        sheet.save(args.run / f"contact-sheet-{offset // 3 + 1:02d}.png")
    for case_id, records in by_case.items():
        cards = []
        for row in records:
            source = html.escape(row["image"], quote=True)
            label = f"{row['language']} · seed {row['seed']} · {row['generation_seconds']:.2f}s"
            cards.append(f'<figure><a href="{source}"><img loading="lazy" src="{source}"></a>'
                         f'<figcaption>{html.escape(label)}</figcaption></figure>')
        sections.append(f'<section><h2>{html.escape(case_id)}</h2>'
                        f'<p>{html.escape(cases[case_id]["ko"])}</p>'
                        f'<p lang="en">{html.escape(cases[case_id]["en"])}</p>'
                        f'<div class="grid">{"".join(cards)}</div></section>')
    document = '''<!doctype html><html lang="ko"><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Tiny-SD 원본 검증 이미지</title>
<style>body{font:16px/1.6 system-ui,sans-serif;max-width:1440px;margin:40px auto;padding:0 24px;background:#f2f4f7;color:#17202e}
h1{font-size:30px}h2{font-size:20px}section{background:white;padding:24px;margin:24px 0;border-radius:12px}
p{margin:6px 0}.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:14px}figure{margin:12px 0}img{width:100%;border-radius:6px}
figcaption{font-size:13px;color:#475569}@media(max-width:800px){.grid{grid-template-columns:repeat(2,1fr)}}</style>
<h1>Tiny-SD 원본 · 한국어/영어 비교</h1>
<p>같은 간접 묘사, 동일 시드, 512×512, 25단계, CFG 7. 원본 PNG를 누르면 크게 볼 수 있습니다.</p>
<p>왼쪽부터 한국어/영어 시드 20260913, 한국어/영어 시드 42. 실제 실행 정밀도는 environment.json에 기록합니다.</p>
''' + "".join(sections) + "</html>"
    (args.run / "gallery.html").write_text(document, encoding="utf-8")
    print(f"Gallery updated: {len(rows)} images, {len(by_case)} cases")


if __name__ == "__main__":
    main()
