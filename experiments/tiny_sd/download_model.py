"""Download only original inference files at a pinned revision; verify upstream hashes."""
import hashlib
import json
from pathlib import Path
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
REPO = "segmind/tiny-sd"
REVISION = "cad0bd7495fa6c4bcca01b19a723dc91627fe84f"
DEST = ROOT / "models" / "tiny-sd" / REVISION
REPORT = ROOT / "outputs" / "tiny-sd"


def get(url):
    return urllib.request.urlopen(url, timeout=120)


def hashes(path):
    sha = hashlib.sha256()
    git = hashlib.sha1(f"blob {path.stat().st_size}\0".encode())
    with path.open("rb") as handle:
        while chunk := handle.read(8 * 1024 * 1024):
            sha.update(chunk)
            git.update(chunk)
    return sha.hexdigest(), git.hexdigest()


def main():
    REPORT.mkdir(parents=True, exist_ok=True)
    with get(f"https://huggingface.co/api/models/{REPO}/revision/{REVISION}?blobs=true") as response:
        info = json.load(response)
    assert info["sha"] == REVISION
    (REPORT / "upstream-model-info.json").write_text(json.dumps(info, indent=2), encoding="utf-8")
    records = []
    for item in info["siblings"]:
        name = item["rfilename"]
        if name not in {"README.md", "model_index.json"} and not name.startswith(
            ("scheduler/", "text_encoder/", "tokenizer/", "unet/", "vae/")
        ):
            continue
        path = DEST / name
        path.parent.mkdir(parents=True, exist_ok=True)
        expected = item.get("lfs", {}).get("sha256")
        def valid():
            if not path.exists() or path.stat().st_size != item["size"]:
                return False
            sha, git = hashes(path)
            return sha == expected if expected else git == item["blobId"]
        url = f"https://huggingface.co/{REPO}/resolve/{REVISION}/{name}"
        if not valid():
            partial = path.with_suffix(path.suffix + ".partial")
            for attempt in range(3):
                try:
                    print(f"Downloading {name}: {item['size']:,} bytes", flush=True)
                    with get(url) as response, partial.open("wb") as out:
                        while chunk := response.read(8 * 1024 * 1024):
                            out.write(chunk)
                    partial.replace(path)
                    if not valid():
                        raise ValueError(f"Size/hash mismatch: {name}")
                    break
                except Exception:
                    if attempt == 2:
                        raise
                    time.sleep(2)
        sha, git = hashes(path)
        records.append({"path": name, "bytes": path.stat().st_size, "sha256": sha,
                        "upstream_hash_verified": True, "url": url,
                        "inference_required": name != "README.md"})
        print(f"Verified {name}", flush=True)
    manifest = {"repo": REPO, "revision": REVISION, "files": records,
                "required_bytes": sum(r["bytes"] for r in records if r["inference_required"]),
                "weights_bytes": sum(r["bytes"] for r in records if r["path"].endswith(".bin"))}
    (REPORT / "model-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in manifest.items() if k != "files"}), flush=True)


if __name__ == "__main__":
    main()
