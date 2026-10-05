"""Reconstruct source/target vocabularies from the pinned SentencePiece models."""
import json
from pathlib import Path
import sentencepiece as spm
from transformers import MarianTokenizer

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "models/opus-mt-tc-big-ko-en/fa26583a41d95346933f26b4cb8f9b700da0d445"
DERIVED = ROOT / "models/opus-mt-tc-big-ko-en/converted"


def tokenizer():
    DERIVED.mkdir(parents=True, exist_ok=True)
    config = json.loads((SOURCE / "config.json").read_text(encoding="utf-8"))
    for side, filename in [("source", "vocab.json"), ("target", "target_vocab.json")]:
        processor = spm.SentencePieceProcessor(model_file=str(SOURCE / f"{side}.spm"))
        vocab = {processor.id_to_piece(i): i for i in range(processor.get_piece_size())}
        if len(vocab) != config["vocab_size"] - 1 or processor.eos_id() != config["eos_token_id"]:
            raise ValueError("Unexpected source/target vocabulary")
        vocab["<pad>"] = config["pad_token_id"]
        # MarianTokenizer reads JSON with the Windows locale encoding by default.
        (DERIVED / filename).write_text(json.dumps(vocab, ensure_ascii=True, indent=2), encoding="utf-8")
    return MarianTokenizer(source_spm=str(SOURCE / "source.spm"), target_spm=str(SOURCE / "target.spm"),
                           vocab=str(DERIVED / "vocab.json"), target_vocab_file=str(DERIVED / "target_vocab.json"),
                           separate_vocabs=True, source_lang="ko", target_lang="en")
