"""Build a measured offline folder from an already-published win-x64 application."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[2]
MODELS = ROOT / "models/app-stage3"
CACHE = ROOT / ".cache/nuget"


def inventory(folder):
    rows = []
    for path in sorted(folder.rglob("*")):
        if path.is_file():
            with path.open("rb") as handle:
                sha = hashlib.file_digest(handle, "sha256").hexdigest()
            rows.append({"path": path.relative_to(folder).as_posix(), "bytes": path.stat().st_size, "sha256": sha})
    return rows


def prepare_manifest():
    licenses = MODELS / "licenses"
    licenses.mkdir(exist_ok=True)
    inputs = {
        "Tiny-SD_LICENSE.txt": ROOT / "outputs/tiny-sd/upstream-distill-sd-LICENSE.txt",
        "TAESD_LICENSE.txt": ROOT / "outputs/tiny-sd/stage2/TAESD_LICENSE.txt",
        "UNet_NOTICE.txt": ROOT / "models/tiny-sd-stage2/unet-int8/NOTICE.txt",
        "TAESD_NOTICE.txt": ROOT / "models/tiny-sd-stage2/taesd-decoder/NOTICE.txt",
        "OPUS-MT_MODEL_CARD.md": ROOT / "models/opus-mt-ko-en/e42d1f41b66194e6d10512f8a27bebc1f4f5097e/README.md",
        "TinyCLIP_MODEL_CARD.md": ROOT / "models/tinyclip/a2a8c6eaa2549ad66eb7c31b85022bf58273a26c/README.md",
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
        shutil.copy2(source, licenses / name)
    (licenses / "CHANGES.txt").write_text(
        "AIrtist development preview, 2026-09-14. No new model training.\n"
        "Tiny-SD: published U-Net weights stored as mixed INT8, FP32 computation, converted to ONNX. Original VAE replaced by published TAESD decoder.\n"
        "OPUS-MT ko-en: shared embedding reused, weights rounded to FP16 storage and cast to FP32 for inference. Greedy decoding.\n"
        "TinyCLIP: published image encoder and projection exported to ONNX; normalized text vectors precomputed from the same checkpoint for data/words.json.\n"
        "Scheduler coefficients derived from Diffusers 0.35.1 (Apache-2.0); CLIP BPE based on the published CLIP algorithm (MIT).\n"
        "MobileCLIP research-only weights are NOT included. Full Tiny-SD/TAESD original weight licenses remain applicable.\n", encoding="utf-8")
    allowed = [
        "generation/unet-w8.onnx", "generation/text_encoder-compact.onnx", "generation/taesd_decoder-compact.onnx",
        "generation/tokenizer/vocab.json", "generation/tokenizer/merges.txt", "generation/settings.json", "generation/scheduler.json",
        "translation/step-fp16-storage.onnx", "translation/source.spm", "translation/vocab.json",
        "scoring/tinyclip-image.onnx", "scoring/vectors.json",
    ]
    paths = [MODELS / relative for relative in allowed] + sorted(licenses.iterdir())
    rows = []
    for path in paths:
        with path.open("rb") as handle:
            sha = hashlib.file_digest(handle, "sha256").hexdigest()
        rows.append({"path": path.relative_to(MODELS).as_posix(), "bytes": path.stat().st_size, "sha256": sha})
    manifest = {"version": 1, "files": rows}
    (MODELS / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return manifest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--report", type=Path, default=ROOT / "outputs/app-stage4/package-size.json")
    args = parser.parse_args()
    manifest = prepare_manifest()
    if args.publish is None:
        print("Runtime manifest prepared")
        return
    if args.output is None:
        parser.error("--output is required with --publish")
    for required in ["AIrtist.exe", "coreclr.dll", "onnxruntime.dll", "DirectML.dll"]:
        if not (args.publish / required).is_file():
            raise FileNotFoundError(f"Published runtime is incomplete: {required}")
    args.output.mkdir(parents=True, exist_ok=False)
    for path in args.publish.rglob("*"):
        if path.is_file() and path.suffix.lower() not in {".pdb", ".lib"}:
            destination = args.output / path.relative_to(args.publish)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, destination)
    for row in manifest["files"] + [{"path": "manifest.json"}]:
        destination = args.output / "models" / row["path"]
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(MODELS / row["path"], destination)
    (args.output / "README.txt").write_text(
        "AIrtist 오프라인 경기 개발 미리보기\n\n이 폴더 전체를 함께 보관하고 AIrtist.exe를 실행하세요.\n"
        "Windows x64용 .NET·WPF·AI 런타임과 모델을 포함합니다. 실행 중 모델 다운로드는 없습니다.\n"
        "제시어를 쓰지 않고 2~160자로 묘사한 뒤 버튼을 누르세요. Enter는 줄바꿈입니다.\n"
        "제출 승인 후 생성은 한 번입니다. 취소·실패 후에는 다음 제시어로 진행합니다.\n"
        "기본 3라운드·작성 60초·AI 처리 180초이며 시작 화면에서 변경할 수 있습니다.\n"
        "8개 단어와 점수는 검증 중인 임시 설정입니다. 정답 확률이 아닙니다.\n"
        "번역 의미와 생성·채점 품질에는 한계가 있습니다. 그림과 입력은 앱 메모리에만 보관합니다.\n"
        "여러 라운드의 점수와 최종 결과를 표시합니다. 실패·취소·마감·건너뛰기는 0점입니다.\n"
        "번역 의미 왜곡과 무관한 그림의 높은 점수가 확인되어 경쟁용 품질은 미달입니다. 멀티플레이는 아직 없습니다.\n"
        "모델과 라이브러리의 조건·고지는 models/licenses 폴더를 참고하세요.\n", encoding="utf-8")
    files = inventory(args.output)
    (args.output / "package-manifest.json").write_text(json.dumps({"files": files, "inventory_excludes_this_file": True}, indent=2), encoding="utf-8")
    all_files = [p for p in args.output.rglob("*") if p.is_file()]
    size = sum(p.stat().st_size for p in all_files)
    summary = {"path": str(args.output), "installed_bytes": size, "file_count": len(all_files), "budget_bytes": 1_000_000_000,
               "remaining_bytes": 1_000_000_000 - size, "within_budget": size <= 1_000_000_000,
               "includes": ["app", ".NET/WPF", "ORT/DirectML", "generation models", "translation model", "TinyCLIP image encoder and text vectors", "tokenizers", "licenses", "manifests"],
               "not_yet_implemented": ["EOS/multiplayer", "installer", "clean PC validation"],
               "quality_not_passed": ["translation meaning preservation", "scoring discrimination", "low-spec hardware validation"]}
    output = args.report
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))
    if size > 1_000_000_000:
        raise SystemExit("Current preview exceeds the installed-size budget")


if __name__ == "__main__":
    main()
