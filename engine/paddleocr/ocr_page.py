from __future__ import annotations

import json
import os
import re
import sys
import tempfile
from pathlib import Path


def _normalise_text(value: object) -> str:
    text = str(value or "")
    text = re.sub(r"!\[[^\]]*\]\([^)]*\)", " ", text)
    text = re.sub(r"<[^>]+>", " ", text)
    text = text.replace("```", " ").replace("\r", " ").replace("\n", " ")
    return re.sub(r"\s+", " ", text).strip()


def _find_parsing_blocks(value: object) -> list[dict[str, object]]:
    if isinstance(value, dict):
        blocks = value.get("parsing_res_list")
        if isinstance(blocks, list):
            return [item for item in blocks if isinstance(item, dict)]
        for child in value.values():
            found = _find_parsing_blocks(child)
            if found:
                return found
    elif isinstance(value, list):
        for child in value:
            found = _find_parsing_blocks(child)
            if found:
                return found
    return []


def _find_input_path(value: object) -> str | None:
    if isinstance(value, dict):
        candidate = value.get("input_path")
        if isinstance(candidate, str) and candidate:
            return candidate
        for child in value.values():
            found = _find_input_path(child)
            if found:
                return found
    elif isinstance(value, list):
        for child in value:
            found = _find_input_path(child)
            if found:
                return found
    return None


def _normalise_box(value: object, width: int, height: int) -> list[float] | None:
    if not isinstance(value, (list, tuple)):
        return None
    numbers: list[float] = []

    def collect(item: object) -> None:
        if isinstance(item, (int, float)):
            numbers.append(float(item))
        elif isinstance(item, (list, tuple)):
            for child in item:
                collect(child)

    collect(value)
    if len(numbers) < 4:
        return None
    if len(numbers) == 4:
        xs = [numbers[0], numbers[2]]
        ys = [numbers[1], numbers[3]]
    else:
        xs = numbers[0::2]
        ys = numbers[1::2]
    left = max(0.0, min(1000.0, min(xs) * 1000.0 / max(1, width)))
    top = max(0.0, min(1000.0, min(ys) * 1000.0 / max(1, height)))
    right = max(0.0, min(1000.0, max(xs) * 1000.0 / max(1, width)))
    bottom = max(0.0, min(1000.0, max(ys) * 1000.0 / max(1, height)))
    if right - left < 1.0 or bottom - top < 1.0:
        return None
    return [round(left, 3), round(top, 3), round(right, 3), round(bottom, 3)]


def _content_from_result(payload: object) -> str:
    ignored_labels = {"image", "figure", "table", "formula", "chart", "seal"}
    parts: list[str] = []
    for block in _find_parsing_blocks(payload):
        label = str(block.get("block_label") or "").strip().lower()
        if any(token in label for token in ignored_labels):
            continue
        text = _normalise_text(block.get("block_content"))
        if text and text.casefold() not in {part.casefold() for part in parts}:
            parts.append(text)
    return " ".join(parts)


def _crop_inputs(
    image_path: Path,
    manifest_path: Path,
    temp_dir: Path,
) -> tuple[list[str], list[list[float]]]:
    from PIL import Image

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if not isinstance(manifest, list):
        raise ValueError("El manifiesto de regiones debe ser una lista JSON.")

    paths: list[str] = []
    boxes: list[list[float]] = []
    with Image.open(image_path) as source:
        source = source.convert("RGB")
        width, height = source.size
        for index, item in enumerate(manifest):
            if not isinstance(item, dict):
                continue
            value = item.get("bbox")
            if not isinstance(value, list) or len(value) != 4:
                continue
            box = [float(number) for number in value]
            left = max(0, min(width - 1, round(box[0] * width / 1000.0)))
            top = max(0, min(height - 1, round(box[1] * height / 1000.0)))
            right = max(left + 1, min(width, round(box[2] * width / 1000.0)))
            bottom = max(top + 1, min(height, round(box[3] * height / 1000.0)))
            crop = source.crop((left, top, right, bottom))
            longest = max(crop.size)
            if longest < 768:
                scale = min(3.0, 768.0 / max(1, longest))
                crop = crop.resize(
                    (max(1, round(crop.width * scale)), max(1, round(crop.height * scale))),
                    Image.Resampling.LANCZOS,
                )
            crop_path = temp_dir / f"region-{index:04d}.png"
            crop.save(crop_path, format="PNG")
            paths.append(str(crop_path))
            boxes.append(box)
    return paths, boxes


def main() -> int:
    if len(sys.argv) not in (2, 3):
        print("Uso: ocr_page.py <imagen> [regiones.json]", file=sys.stderr)
        return 2

    image_path = Path(sys.argv[1]).resolve()
    if not image_path.is_file():
        print(f"No existe la imagen: {image_path}", file=sys.stderr)
        return 2

    # Estas variables deben establecerse antes de importar PaddleOCR/PaddleX.
    model_home = os.environ.get("TINTAES_PADDLE_MODEL_HOME")
    if model_home:
        os.environ.setdefault("PADDLE_PDX_CACHE_HOME", model_home)
        os.environ.setdefault("HF_HOME", str(Path(model_home) / "huggingface"))
    os.environ.setdefault("PADDLE_PDX_MODEL_SOURCE", "huggingface")
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")

    from PIL import Image
    from paddleocr import PaddleOCRVL

    with Image.open(image_path) as image:
        width, height = image.size

    device = os.environ.get("TINTAES_PADDLE_DEVICE", "gpu:0")
    engine = os.environ.get("TINTAES_PADDLE_ENGINE", "transformers")
    pipeline = PaddleOCRVL(
        pipeline_version="v1.6",
        engine=engine,
        device=device,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_layout_detection=True,
        use_queues=False,
    )

    spots: list[dict[str, object]] = []
    if len(sys.argv) == 3:
        manifest_path = Path(sys.argv[2]).resolve()
        with tempfile.TemporaryDirectory(prefix="tintaes-paddle-") as temp_name:
            inputs, boxes = _crop_inputs(image_path, manifest_path, Path(temp_name))
            for fallback_index, result in enumerate(pipeline.predict(inputs)):
                payload = result.json
                input_path = _find_input_path(payload) or ""
                match = re.search(r"region-(\d+)\.png$", input_path.replace("\\", "/"))
                index = int(match.group(1)) if match else fallback_index
                if index >= len(boxes):
                    continue
                text = _content_from_result(payload)
                if text:
                    spots.append({"text": text, "bbox": boxes[index]})
    else:
        for result in pipeline.predict(str(image_path)):
            for block in _find_parsing_blocks(result.json):
                text = _normalise_text(block.get("block_content"))
                box = _normalise_box(block.get("block_bbox"), width, height)
                if text and box:
                    spots.append({"text": text, "bbox": box})

    print("TINTAES_RESULT=" + json.dumps(spots, ensure_ascii=False, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
