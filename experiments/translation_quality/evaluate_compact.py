"""Independent ONNX greedy references, using only descriptions as model inputs."""
import argparse
import json
import re
import time
import numpy as np
import onnxruntime as ort
from candidate import DERIVED, ROOT, tokenizer
from evaluate_source import cases


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("split", choices=["development", "holdout"])
    parser.add_argument("--precision", choices=["mixed", "fp32"], default="mixed")
    args = parser.parse_args()
    tokens = tokenizer()
    options = ort.SessionOptions()
    options.intra_op_num_threads = 6
    started = time.perf_counter()
    session = ort.InferenceSession(str(DERIVED / f"step-{args.precision}.onnx"), sess_options=options, providers=["CPUExecutionProvider"])
    load_seconds = time.perf_counter() - started
    rows = []
    for case in cases(args.split):
        source = case["source"]
        inputs = tokens(source, return_tensors="np")
        output = [32000]
        started = time.perf_counter()
        if not re.search(r"[가-힣ㄱ-ㅎㅏ-ㅣ]", source):
            translated = source
        else:
            for step in range(64):
                logits = session.run(None, {"input_ids": inputs.input_ids, "attention_mask": inputs.attention_mask,
                                   "decoder_input_ids": np.array([output], dtype=np.int64)})[0][0]
                if not np.isfinite(logits).all():
                    raise ValueError("Non-finite logits")
                logits[32000] = -np.inf
                token = int(logits.argmax())
                output.append(token)
                if token == 2:
                    break
            if output[-1] != 2:
                raise ValueError(f"Token limit: {source}")
            translated = tokens.decode(output, skip_special_tokens=True)
        row = {"source": source, "input_ids": inputs.input_ids[0].tolist(), "output_ids": output,
               "compact": translated, "seconds": time.perf_counter() - started}
        rows.append(row)
        label = "compact" if args.precision == "mixed" else "onnx-fp32"
        (ROOT / f"outputs/translation-quality/{label}-{args.split}.json").write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"source": source, "compact": translated, "seconds": row["seconds"]}, ensure_ascii=True), flush=True)
    print(json.dumps({"load_seconds": load_seconds, "cases": len(rows)}), flush=True)


if __name__ == "__main__":
    main()
