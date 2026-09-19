"""Prepare offline runtime inputs and independent Python references for the C# app."""
import argparse
import json
from pathlib import Path
import shutil

import numpy as np
import onnxruntime as ort
import torch

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "models/app-stage3"
RESULTS = ROOT / "outputs/app-stage3"
ORIGINAL = ROOT / "models/tiny-sd/cad0bd7495fa6c4bcca01b19a723dc91627fe84f"
TRANSLATION = ROOT / "models/opus-mt-ko-en/e42d1f41b66194e6d10512f8a27bebc1f4f5097e"
torch.set_num_threads(6)
torch.set_grad_enabled(False)


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")


def translation():
    from transformers import MarianMTModel, MarianTokenizer
    import onnx
    from onnx import numpy_helper, helper, TensorProto
    folder = OUT / "translation"
    folder.mkdir(parents=True, exist_ok=True)
    tokenizer = MarianTokenizer.from_pretrained(TRANSLATION, local_files_only=True)
    model = MarianMTModel.from_pretrained(TRANSLATION, local_files_only=True, attn_implementation="eager").eval()

    class Step(torch.nn.Module):
        def __init__(self):
            super().__init__()
            self.model = model

        def forward(self, input_ids, attention_mask, decoder_input_ids):
            return self.model(input_ids=input_ids, attention_mask=attention_mask,
                              decoder_input_ids=decoder_input_ids, use_cache=False, return_dict=False)[0][:, -1, :]

    fp32 = ROOT / "models/opus-mt-ko-en/step-fp32.onnx"
    compact = folder / "step-fp16-storage.onnx"
    inputs = tokenizer("아삭아삭하고 빨간 과일", return_tensors="pt")
    if not fp32.exists():
        torch.onnx.export(Step().eval(), (inputs.input_ids, inputs.attention_mask, torch.tensor([[65000, 10, 15]])), str(fp32),
                          input_names=["input_ids", "attention_mask", "decoder_input_ids"], output_names=["logits"],
                          dynamic_axes={"input_ids": {1: "source_length"}, "attention_mask": {1: "source_length"}, "decoder_input_ids": {1: "target_length"}},
                          opset_version=17, dynamo=False)
    if not compact.exists():
        graph = onnx.load(fp32)
        weights = {t.name: numpy_helper.to_array(t) for t in graph.graph.initializer}
        shared = weights["model.model.shared.weight"]
        prefixes, initializers = [], []
        for tensor in graph.graph.initializer:
            values = weights[tensor.name]
            if values.shape == shared.T.shape and np.array_equal(values, shared.T):
                prefixes.append(helper.make_node("Transpose", ["model.model.shared.weight"], [tensor.name], perm=[1, 0]))
            elif values.dtype == np.float32 and values.size > 1:
                name = tensor.name + ".fp16"
                initializers.append(numpy_helper.from_array(values.astype(np.float16), name))
                prefixes.append(helper.make_node("Cast", [name], [tensor.name], to=TensorProto.FLOAT))
            else:
                initializers.append(tensor)
        nodes = list(graph.graph.node)
        del graph.graph.node[:]
        del graph.graph.initializer[:]
        graph.graph.initializer.extend(initializers)
        graph.graph.node.extend(prefixes + nodes)
        onnx.checker.check_model(graph)
        onnx.save(graph, compact)
    for name in ["source.spm", "vocab.json", "config.json", "README.md"]:
        shutil.copy2(TRANSLATION / name, folder / name)
    options = ort.SessionOptions()
    options.intra_op_num_threads = 6
    session = ort.InferenceSession(str(compact), sess_options=options, providers=["CPUExecutionProvider"])
    descriptions = [c["ko"] for c in json.loads((ROOT / "experiments/tiny_sd/prompts.json").read_text(encoding="utf-8"))["cases"]]
    rows = []
    for description in descriptions:
        inputs = tokenizer(description, return_tensors="pt")
        reference = model.generate(**inputs, num_beams=1, max_new_tokens=64)
        output = [model.config.decoder_start_token_id]
        for _ in range(64):
            values = {"input_ids": inputs.input_ids.numpy(), "attention_mask": inputs.attention_mask.numpy(), "decoder_input_ids": np.array([output], dtype=np.int64)}
            logits = session.run(None, values)[0][0]
            logits[model.config.pad_token_id] = -np.inf
            token = int(logits.argmax())
            output.append(token)
            if token == model.config.eos_token_id:
                break
        if output[-1] != model.config.eos_token_id:
            raise RuntimeError("Translation token limit reached")
        row = {"source": description, "input_ids": inputs.input_ids[0].tolist(), "output_ids": output,
               "fp32": tokenizer.decode(reference[0], skip_special_tokens=True), "compact": tokenizer.decode(output, skip_special_tokens=True)}
        rows.append(row)
        print(json.dumps(row, ensure_ascii=True), flush=True)
    save(RESULTS / "translation-reference.json", rows)
    print("translation bytes", compact.stat().st_size, flush=True)


def scoring():
    from prepare_scoring import main
    main()


def generation():
    from diffusers import DPMSolverMultistepScheduler
    from transformers import CLIPTokenizer
    folder = OUT / "generation"
    folder.mkdir(parents=True, exist_ok=True)
    for name in ["unet-w8.onnx", "text_encoder-compact.onnx", "taesd_decoder-compact.onnx"]:
        shutil.copy2(ROOT / "models/tiny-sd-onnx-stage2" / name, folder / name)
    shutil.copytree(ORIGINAL / "tokenizer", folder / "tokenizer", dirs_exist_ok=True)
    scheduler = DPMSolverMultistepScheduler.from_pretrained(ORIGINAL / "scheduler", local_files_only=True)
    scheduler.set_timesteps(25)
    # Store coefficients from the pinned Diffusers scheduler. C# applies the same
    # first/second-order linear recurrence, without porting a neural network.
    steps = []
    for i, timestep in enumerate(scheduler.timesteps):
        sigma_s = scheduler.sigmas[i]
        alpha_s, sigma_s_norm = scheduler._sigma_to_alpha_sigma_t(sigma_s)
        coefficients = []
        for sample, current_x0, previous_x0 in [(1., 0., 0.), (0., 1., 0.), (0., 0., 1.)]:
            scheduler._step_index = i
            if i == 0 or i == 24:
                value = scheduler.dpm_solver_first_order_update(torch.tensor([current_x0]), sample=torch.tensor([sample]))
            else:
                value = scheduler.multistep_dpm_solver_second_order_update([torch.tensor([previous_x0]), torch.tensor([current_x0])], sample=torch.tensor([sample]))
            coefficients.append(float(value.item()))
        steps.append({"timestep": int(timestep), "alpha": float(alpha_s), "sigma": float(sigma_s_norm), "sample": coefficients[0], "current": coefficients[1], "previous": coefficients[2]})
    save(folder / "scheduler.json", {"source": "diffusers 0.35.1 DPMSolverMultistepScheduler", "algorithm": "dpmsolver++/order2/midpoint", "steps": steps})
    tokenizer = CLIPTokenizer.from_pretrained(ORIGINAL / "tokenizer", local_files_only=True)
    prompts = json.loads((ROOT / "experiments/tiny_sd/prompts.json").read_text(encoding="utf-8"))
    texts = [""] + [c["en"] + ", " + prompts["style"] for c in prompts["cases"]]
    save(RESULTS / "clip-tokenizer-reference.json", [{"text": text, "ids": tokenizer(text, padding="max_length", max_length=77).input_ids} for text in texts])
    # Synthetic values check every scheduler update independently of a GPU run.
    scheduler.set_timesteps(25)
    sample = torch.randn((1, 4, 64, 64), generator=torch.Generator().manual_seed(42))
    initial = torch.randn((1, 4, 64, 64), generator=torch.Generator().manual_seed(20260913))
    initial.numpy().tofile(RESULTS / "reference-initial-latents.f32")
    checks = []
    for i, timestep in enumerate(scheduler.timesteps):
        noise = torch.randn(sample.shape, generator=torch.Generator().manual_seed(100 + i))
        before = sample.flatten()[:8].tolist()
        sample = scheduler.step(noise, timestep, sample).prev_sample
        checks.append({"before": before, "noise": noise.flatten()[:8].tolist(), "after": sample.flatten()[:8].tolist()})
    save(RESULTS / "scheduler-reference.json", checks)
    save(folder / "settings.json", {"width": 512, "height": 512, "steps": 25, "guidance": 7.0, "style": prompts["style"], "token_limit": 77})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("component", choices=["translation", "scoring", "generation"])
    args = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    RESULTS.mkdir(parents=True, exist_ok=True)
    globals()[args.component]()


if __name__ == "__main__":
    main()
