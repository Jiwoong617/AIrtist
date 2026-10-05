"""Pinned public comparison checkpoint; development only."""
import json
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "experiments/app_stage3"))
from download_sources import download

REPO = "wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M"
REVISION = "95ec8197b3f2fe7f747865c61ca556cf0768b2f7"
with urllib.request.urlopen(f"https://huggingface.co/api/models/{REPO}/revision/{REVISION}?blobs=true", timeout=60) as response:
    info = json.load(response)
if info["sha"] != REVISION or info["cardData"]["license"] != "mit":
    raise ValueError("Unexpected checkpoint or license")
files = {row["rfilename"]: row for row in info["siblings"]}
manifest = {"repo": REPO, "revision": REVISION, "license": "mit", "files": {}}
for name in ["README.md", "config.json", "model.safetensors", "preprocessor_config.json", "tokenizer.json"]:
    manifest["files"][name] = download(f"https://huggingface.co/{REPO}/resolve/{REVISION}/{name}",
        ROOT / "models/tinyclip-40m" / REVISION / name, files[name].get("lfs", {}).get("sha256"))
destination = ROOT / "experiments/app_quality/scoring-source.json"
destination.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
