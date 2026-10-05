"""Deterministic diagnostic fixtures; never used to generate game images."""
import json
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "outputs/app-quality/holdout"
OUT.mkdir(parents=True, exist_ok=True)
cases = []


def record(name, category, expected=None, image=None, path=None):
    if image is not None:
        path = OUT / (name + ".png")
        image.save(path)
        path = str(path.relative_to(ROOT)).replace("\\", "/")
    cases.append(dict(name=name, category=category, expected=expected, path=path))


for name, color in [("blank-gray", "#888888"), ("blank-blue", "#204d88")]:
    record(name, "unrelated", image=Image.new("RGB", (384, 384), color))
for seed, size in [(711, 384), (917, 48)]:
    rng = np.random.default_rng(seed)
    image = Image.fromarray(rng.integers(0, 256, (size, size, 3), dtype=np.uint8)).resize((384, 384), Image.Resampling.NEAREST)
    record(f"noise-{seed}", "unrelated", image=image)

font = ImageFont.truetype("C:/Windows/Fonts/malgun.ttf", 54)
for name, text, background, ink in [("text-apple-red", "APPLE", "#ffffdd", "#bb2020"),
                                    ("text-rabbit-blue", "rabbit", "#eeeeff", "#2030bb"),
                                    ("text-korean", "자동차", "#222222", "white"),
                                    ("text-two-lines", "cup\nbanana", "#dddddd", "black")]:
    image = Image.new("RGB", (384, 384), background)
    ImageDraw.Draw(image).multiline_text((40, 140), text, font=font, fill=ink, spacing=15)
    record(name, "text-only", image=image)

for name, target, outline in [("green-apple", "apple", False), ("purple-cup", "cup", False), ("thin-cup", "cup", True),
                              ("yellow-banana", "banana", False), ("red-umbrella", "umbrella", False), ("blue-car", "car", False),
                              ("gold-pear", None, False), ("cyan-bottle", None, False), ("red-bowl", None, False)]:
    image = Image.new("RGB", (384, 384), "#fff9e8")
    d = ImageDraw.Draw(image)
    if "apple" in name:
        d.polygon([(190,115),(130,105),(88,140),(85,195),(113,257),(155,284),(190,271),(226,283),(276,240),(299,171),(281,130),(242,108)], fill="#70aa2c", outline="#234817", width=4)
        d.line([(190,129),(199,82)], fill="#784629", width=9)
        d.ellipse((205,82,246,103), fill="#2d6030")
    elif "cup" in name:
        ink = "#303040"
        d.ellipse((237,145,323,238), outline=ink, width=5 if outline else 11)
        d.rounded_rectangle((95,132,264,278), radius=34, fill="#fff9e8" if outline else "#ac77c3", outline=ink, width=4)
        d.ellipse((95,114,264,153), fill="#fff9e8", outline=ink, width=4)
    elif "banana" in name:
        d.polygon([(80,113),(104,170),(153,207),(215,219),(274,189),(302,134),(291,210),(257,261),(204,287),(148,276),(107,241),(79,184)], fill="#f9d829", outline="#886b08", width=4)
        d.line([(294,143),(309,108)], fill="#766627", width=13)
    elif "umbrella" in name:
        d.line([(191,170),(191,280)], fill="#555555", width=8)
        d.arc((145,251,193,310), 0, 180, fill="#555555", width=8)
        d.pieslice((61,80,322,325), 180, 360, fill="#e95757", outline="#8d2020", width=4)
        d.line([(191,80),(191,201)], fill="#8d2020", width=3)
    elif "car" in name:
        d.polygon([(52,214),(88,202),(130,144),(239,144),(284,200),(329,218),(329,264),(52,264)], fill="#3b89c3", outline="#222222", width=4)
        d.polygon([(135,157),(177,157),(177,200),(103,200)], fill="#ddffff")
        d.polygon([(188,157),(232,157),(267,200),(188,200)], fill="#ddffff")
        for x in [107,276]: d.ellipse((x-26,240,x+26,292), fill="#222222")
    elif "pear" in name:
        d.polygon([(189,85),(215,112),(226,164),(268,209),(272,253),(246,287),(181,300),(124,276),(110,227),(139,176),(154,119)], fill="#dab043", outline="#735b12", width=4)
        d.line([(188,93),(204,57)], fill="#675224", width=7)
    elif "bottle" in name:
        d.polygon([(162,82),(220,82),(220,144),(252,181),(252,290),(130,290),(130,181),(162,144)], fill="#55b6c2", outline="#245762", width=4)
        d.rectangle((156,67,226,90), fill="#345963")
    elif "bowl" in name:
        d.chord((70,119,312,300), 0, 180, fill="#d16053", outline="#4b2420", width=5)
        d.ellipse((70,162,312,206), fill="#fff9e8", outline="#4b2420", width=4)
    record(name, "controlled-drawing" if target else "near-wrong", target, image=image)

base = "outputs/tiny-sd/runs/baseline-fp32-20260913-01/images/"
for name, file, category, expected in [
    ("generated-rabbit-other-seed", "016_quality_animal_ears_en_20260913.png", "generated", "rabbit"),
    ("generated-cup-two-handles", "030_quality_vessel_blue_en_42.png", "generated", "cup"),
    ("generated-lemon", "014_quality_fruit_curved_en_42.png", "near-wrong", None),
    ("generated-ring", "022_quality_rain_cover_en_42.png", "unrelated", None)]:
    record(name, category, expected, path=base+file)

(ROOT / "experiments/app_quality/scoring-holdout.json").write_text(json.dumps(cases, indent=2), encoding="utf-8")
print(f"Created {len(cases)} held-out cases. No scoring performed.")
