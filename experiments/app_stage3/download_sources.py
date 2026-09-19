"""Fetch pinned, public development inputs; never called by the shipped app."""
import hashlib
import json
from pathlib import Path
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
TRANSLATION_REVISION = "e42d1f41b66194e6d10512f8a27bebc1f4f5097e"
TINYCLIP_REVISION = "a2a8c6eaa2549ad66eb7c31b85022bf58273a26c"


def download(url, destination, expected=None):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not destination.exists():
        partial = destination.with_suffix(destination.suffix + ".partial")
        with urllib.request.urlopen(url, timeout=90) as response, partial.open("wb") as handle:
            while chunk := response.read(1024 * 1024):
                handle.write(chunk)
        partial.replace(destination)
    with destination.open("rb") as handle:
        sha = hashlib.file_digest(handle, "sha256").hexdigest()
    if expected and sha != expected:
        raise ValueError(f"Unexpected SHA-256: {destination}")
    print(destination.relative_to(ROOT), destination.stat().st_size, flush=True)
    return {"url": url, "bytes": destination.stat().st_size, "sha256": sha}


def main():
    repo = "Helsinki-NLP/opus-mt-ko-en"
    folder = ROOT / "models/opus-mt-ko-en" / TRANSLATION_REVISION
    with urllib.request.urlopen(f"https://huggingface.co/api/models/{repo}/revision/{TRANSLATION_REVISION}?blobs=true", timeout=60) as response:
        info = json.load(response)
    files = {row["rfilename"]: row for row in info["siblings"]}
    manifest = {"translation": {"repo": repo, "revision": TRANSLATION_REVISION, "files": {}}}
    for name in ["README.md", "config.json", "generation_config.json", "pytorch_model.bin", "source.spm", "target.spm", "tokenizer_config.json", "vocab.json"]:
        entry = download(f"https://huggingface.co/{repo}/resolve/{TRANSLATION_REVISION}/{name}", folder / name, files[name].get("lfs", {}).get("sha256"))
        manifest["translation"]["files"][name] = entry
    repo = "wkcn/TinyCLIP-ViT-8M-16-Text-3M-YFCC15M"
    with urllib.request.urlopen(f"https://huggingface.co/api/models/{repo}/revision/{TINYCLIP_REVISION}?blobs=true", timeout=60) as response:
        info = json.load(response)
    files = {row["rfilename"]: row for row in info["siblings"]}
    manifest["scoring"] = {"repo": repo, "revision": TINYCLIP_REVISION, "files": {}}
    for name in ["README.md", "config.json", "model.safetensors", "preprocessor_config.json", "tokenizer.json"]:
        manifest["scoring"]["files"][name] = download(f"https://huggingface.co/{repo}/resolve/{TINYCLIP_REVISION}/{name}", ROOT / "models/tinyclip" / TINYCLIP_REVISION / name, files[name].get("lfs", {}).get("sha256"))
    for name, url in {
        "TinyCLIP_LICENSE.txt": "https://raw.githubusercontent.com/microsoft/Cream/main/TinyCLIP/LICENSE",
        "Apache-2.0.txt": "https://www.apache.org/licenses/LICENSE-2.0.txt",
        "CLIP_LICENSE.txt": "https://raw.githubusercontent.com/openai/CLIP/main/LICENSE",
        "Google.Protobuf_LICENSE.txt": "https://raw.githubusercontent.com/protocolbuffers/protobuf/v30.2/LICENSE",
        "WPF_ThirdPartyNotices.txt": "https://raw.githubusercontent.com/dotnet/wpf/v10.0.3/THIRD-PARTY-NOTICES.TXT",
    }.items():
        download(url, ROOT / "models/app-stage3/licenses" / name)
    output = ROOT / "models/app-stage3-source-manifest.json"
    output.write_text(json.dumps(manifest, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
