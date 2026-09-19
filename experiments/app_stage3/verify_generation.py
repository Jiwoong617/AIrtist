"""Compare the three C# DirectML images with the existing independent Python run."""
from collections import Counter
import argparse
import json
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
RESULTS = ROOT / "outputs/app-stage3"
parser = argparse.ArgumentParser()
parser.add_argument("--run", default="csharp-generation")
args = parser.parse_args()
rows = []
for name in ["fruit_red", "animal_ears", "sphere_below"]:
    actual = np.asarray(Image.open(RESULTS / args.run / (name + ".png")).convert("RGB"), dtype=np.float32)
    expected = np.asarray(Image.open(ROOT / "outputs/tiny-sd/runs/stage2-onnx-pipeline-02/images" / (name + "_en_20260913.png")).convert("RGB"), dtype=np.float32)
    counts = {}
    for component in ["text", "unet", "decoder"]:
        profiles = sorted((RESULTS / args.run / name).glob(component + "_*.json"), key=lambda p: p.stat().st_mtime)
        events = json.loads(profiles[-1].read_text())
        counter = Counter(e.get("args", {}).get("provider") for e in events if e.get("args", {}).get("provider"))
        if not counter["DmlExecutionProvider"]:
            raise ValueError(f"Missing DirectML execution: {name} {component}")
        counts[component] = dict(counter)
    maximum = float(np.abs(actual - expected).max())
    rmse = float(np.sqrt(((actual - expected) ** 2).mean()))
    if maximum > 2 or rmse > .2:
        raise ValueError(f"Pixel mismatch: {name}, max {maximum}, rmse {rmse}")
    rows.append({"case": name, "max_channel_difference": maximum, "rmse_0_255": rmse, "provider_events": counts})
(RESULTS / (args.run + "-comparison.json")).write_text(json.dumps(rows, indent=2), encoding="utf-8")
print(json.dumps(rows, indent=2))
