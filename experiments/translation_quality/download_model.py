"""Pinned public Korean-English translation candidate; development only."""
import json
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "experiments/app_stage3"))
from download_sources import download

REPO = "Helsinki-NLP/opus-mt-tc-big-ko-en"
REVISION = "fa26583a41d95346933f26b4cb8f9b700da0d445"
DEST = ROOT / "models/opus-mt-tc-big-ko-en" / REVISION
with urllib.request.urlopen(f"https://huggingface.co/api/models/{REPO}/revision/{REVISION}?blobs=true", timeout=60) as response:
    info = json.load(response)
if info["sha"] != REVISION or info["cardData"]["license"] != "cc-by-4.0":
    raise ValueError("Unexpected checkpoint or license")
files = {row["rfilename"]: row for row in info["siblings"]}
manifest = {"repo": REPO, "revision": REVISION, "license": "cc-by-4.0", "files": {}}
for name in ["README.md", "config.json", "generation_config.json", "model.safetensors", "source.spm", "target.spm", "tokenizer_config.json", "special_tokens_map.json", "vocab.json"]:
    manifest["files"][name] = download(f"https://huggingface.co/{REPO}/resolve/{REVISION}/{name}", DEST / name, files[name].get("lfs", {}).get("sha256"))
license_url = "https://creativecommons.org/licenses/by/4.0/legalcode.txt"
license_path = DEST / "CC-BY-4.0.txt"
if not license_path.exists():
    request = urllib.request.Request(license_url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(request, timeout=60) as response:
        license_path.write_bytes(response.read())
manifest["license_file"] = download(license_url, license_path, "9ba9550ad48438d0836ddab3da480b3b69ffa0aac7b7878b5a0039e7ab429411")
(ROOT / "experiments/translation_quality/source.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
