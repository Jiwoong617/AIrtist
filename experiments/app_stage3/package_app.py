"""Compatibility entry point; shared PowerShell scripts own manifests and packaging."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]
MODELS = ROOT / "models/app-stage3"
CACHE = ROOT / ".cache/nuget"


def run_script(name, *arguments):
    shell = shutil.which("pwsh")
    if shell is None:
        raise RuntimeError("PowerShell 7 (pwsh) is required for the shared Windows packaging scripts")
    subprocess.run([shell, "-NoProfile", "-File", str(ROOT / "scripts" / name), *map(str, arguments)], cwd=ROOT, check=True)


def prepare_manifest():
    licenses = MODELS / "licenses"
    licenses.mkdir(parents=True, exist_ok=True)
    inputs = {
        "Tiny-SD_LICENSE.txt": ROOT / "outputs/tiny-sd/upstream-distill-sd-LICENSE.txt",
        "TAESD_LICENSE.txt": ROOT / "outputs/tiny-sd/stage2/TAESD_LICENSE.txt",
        "UNet_NOTICE.txt": ROOT / "models/tiny-sd-stage2/unet-int8/NOTICE.txt",
        "TAESD_NOTICE.txt": ROOT / "models/tiny-sd-stage2/taesd-decoder/NOTICE.txt",
        "OPUS-MT_MODEL_CARD.md": ROOT / "models/opus-mt-ko-en/e42d1f41b66194e6d10512f8a27bebc1f4f5097e/README.md",
        "ONNXRuntime_LICENSE.txt": CACHE / "microsoft.ml.onnxruntime.directml/1.22.0/LICENSE",
        "ONNXRuntime_ThirdPartyNotices.txt": CACHE / "microsoft.ml.onnxruntime.directml/1.22.0/ThirdPartyNotices.txt",
        "DirectML_LICENSE.txt": CACHE / "microsoft.ai.directml/1.15.4/LICENSE.txt",
        "DirectML_ThirdPartyNotices.txt": CACHE / "microsoft.ai.directml/1.15.4/ThirdPartyNotices.txt",
        "Tokenizers_LICENSE.txt": CACHE / "microsoft.ml.tokenizers/2.0.0/LICENSE",
        "DotNet_LICENSE.txt": CACHE / "microsoft.netcore.app.runtime.win-x64/10.0.3/LICENSE.TXT",
        "DotNet_ThirdPartyNotices.txt": CACHE / "microsoft.netcore.app.runtime.win-x64/10.0.3/THIRD-PARTY-NOTICES.TXT",
        "WPF_LICENSE.txt": CACHE / "microsoft.windowsdesktop.app.runtime.win-x64/10.0.3/LICENSE",
    }
    for name, source in inputs.items():
        if source.exists():
            shutil.copy2(source, licenses / name)
        elif not (licenses / name).exists():
            raise FileNotFoundError(f"Required license/notice missing: {source}")
    run_script("prepare-runtime-assets.ps1", "-ModelsDirectory", MODELS)
    return json.loads((MODELS / "manifest.json").read_text(encoding="utf-8"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--report", type=Path, default=ROOT / "outputs/build/package-size.json")
    args = parser.parse_args()
    if args.publish is not None and args.output is None:
        parser.error("--output is required with --publish")
    prepare_manifest()
    if args.publish is not None:
        run_script("package-app.ps1", "-PublishDirectory", args.publish.resolve(), "-OutputDirectory", args.output.resolve(), "-ReportPath", args.report.resolve())


if __name__ == "__main__":
    main()
