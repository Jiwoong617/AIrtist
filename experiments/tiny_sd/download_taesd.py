"""Fetch pinned TAESD weights, verify upstream hashes, record provenance."""
import hashlib
import json
from pathlib import Path
from datetime import datetime, timezone
from download_model import get, hashes, ROOT

REPO = "madebyollin/taesd"
REVISION = "614f76814bbe30edbe2e627ace1c2234c81a2c0e"
DEST = ROOT / "models" / "taesd" / REVISION
REPORT = ROOT / "outputs" / "tiny-sd" / "stage2"


def main():
    REPORT.mkdir(parents=True, exist_ok=True)
    DEST.mkdir(parents=True, exist_ok=True)
    with get(f"https://huggingface.co/api/models/{REPO}/revision/{REVISION}?blobs=true") as response:
        info = json.load(response)
    assert info["sha"] == REVISION
    names = {"README.md", "config.json", "diffusion_pytorch_model.safetensors", "taesd_decoder.safetensors"}
    records = []
    for item in info["siblings"]:
        name = item["rfilename"]
        if name not in names:
            continue
        url = f"https://huggingface.co/{REPO}/resolve/{REVISION}/{name}"
        path = DEST / name
        if not path.exists():
            print(f"Downloading {name}", flush=True)
            with get(url) as response:
                data = response.read()
            path.write_bytes(data)
        sha, git = hashes(path)
        expected = item.get("lfs", {}).get("sha256")
        assert path.stat().st_size == item["size"]
        assert sha == expected if expected else git == item["blobId"]
        records.append({"path": name, "bytes": path.stat().st_size, "sha256": sha, "url": url})
    with get("https://raw.githubusercontent.com/madebyollin/taesd/main/LICENSE") as response:
        data = response.read()
    (REPORT / "TAESD_LICENSE.txt").write_bytes(data)
    manifest = {"repo": REPO, "revision": REVISION, "checked_utc": datetime.now(timezone.utc).isoformat(),
                "license": "MIT", "license_source": "https://github.com/madebyollin/taesd/blob/main/LICENSE",
                "license_sha256": hashlib.sha256(data).hexdigest(), "files": records}
    (REPORT / "taesd-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2), flush=True)


if __name__ == "__main__":
    main()
