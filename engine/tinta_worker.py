from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
from pathlib import Path

import cv2
import numpy as np
from PIL import Image


ENGINE_DIR = Path(__file__).resolve().parent
MANGA_TRANSLATOR_DIR = ENGINE_DIR / "manga-image-translator"
sys.path.insert(0, str(MANGA_TRANSLATOR_DIR))

from manga_translator.config import Config  # noqa: E402
from manga_translator.manga_translator import MangaTranslator  # noqa: E402


PROGRESS = {
    "running_pre_translation_hooks": (2, "Preparando el motor local"),
    "detection": (12, "Localizando letras y bocadillos"),
    "ocr": (34, "Leyendo el texto original"),
    "textline_merge": (53, "Agrupando frases por bocadillo"),
    "translating": (58, "Preparando el texto para traducir"),
    "after-translating": (61, "Comprobando las zonas detectadas"),
    "mask-generation": (67, "Ajustando la máscara a las letras"),
    "inpainting": (76, "Reconstruyendo el fondo con LaMa"),
    "rendering": (94, "Preparando el resultado editable"),
    "downscaling": (97, "Restaurando el tamaño original"),
}


def emit(payload: dict) -> None:
    print(json.dumps(payload, ensure_ascii=False), flush=True)


class TintaTranslator(MangaTranslator):
    """Pipeline del proyecto con una máscara que nunca pierde letras detectadas."""

    def __init__(self, params: dict) -> None:
        super().__init__(params)
        # Evita que los logs de la librería se mezclen con el protocolo JSON.
        self._progress_hooks.clear()

    async def _report_progress(self, state: str, finished: bool = False) -> None:
        percent, message = PROGRESS.get(state, (0, state))
        emit({"type": "progress", "state": state, "percent": percent, "message": message})

    async def _run_mask_refinement(self, config: Config, ctx):
        refined = await super()._run_mask_refinement(config, ctx)
        if ctx.mask_raw is None:
            return refined

        raw = ctx.mask_raw
        if raw.ndim == 3:
            raw = cv2.cvtColor(raw, cv2.COLOR_BGR2GRAY)
        raw = np.where(raw > 20, 255, 0).astype(np.uint8)
        # El refinador puede descartar una línea aun cuando CTD la haya localizado.
        # El CTD dibuja el núcleo de cada letra. Un radio de 4 px incluye contorno y
        # antialiasing, pero sigue conservando la forma de los glifos: no crea placas.
        raw = cv2.dilate(
            raw,
            cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9)),
            iterations=1,
        )
        if refined is None:
            return raw
        return cv2.bitwise_or(refined, raw)


def clamp_rect(rect: tuple[int, int, int, int], width: int, height: int) -> tuple[int, int, int, int]:
    left, top, right, bottom = rect
    left = max(0, min(left, width - 1))
    top = max(0, min(top, height - 1))
    right = max(left + 1, min(right, width))
    bottom = max(top + 1, min(bottom, height))
    return left, top, right, bottom


def stripe_ratio(mask: np.ndarray, left: int, top: int, right: int, bottom: int) -> float:
    area = mask[top:bottom, left:right]
    return float(np.mean(area > 0)) if area.size else 0.0


def largest_inner_rectangle(
    mask: np.ndarray,
    centre_x: int,
    centre_y: int,
) -> tuple[int, int, int, int] | None:
    """Mayor rectángulo contenido en la silueta y atravesando el centro del texto."""

    height, width = mask.shape
    heights = np.zeros(width, dtype=np.int32)
    best_area = 0
    best: tuple[int, int, int, int] | None = None
    for row in range(height):
        heights = np.where(mask[row] > 0, heights + 1, 0)
        stack: list[int] = []
        for column in range(width + 1):
            current = int(heights[column]) if column < width else 0
            while stack and int(heights[stack[-1]]) > current:
                index = stack.pop()
                rect_height = int(heights[index])
                left = stack[-1] + 1 if stack else 0
                right = column
                top = row - rect_height + 1
                bottom = row + 1
                area = (right - left) * rect_height
                if (
                    area > best_area
                    and left <= centre_x < right
                    and top <= centre_y < bottom
                ):
                    best_area = area
                    best = (left, top, right, bottom)
            stack.append(column)
    return best


def find_safe_render_box(
    clean_rgb: np.ndarray,
    text_rect: tuple[int, int, int, int],
) -> tuple[
    tuple[int, int, int, int],
    float,
    tuple[int, int, int, int],
    list[list[int]],
]:
    """Busca el interior uniforme conectado del bocadillo sin taparlo con un rectángulo."""

    page_height, page_width = clean_rgb.shape[:2]
    x1, y1, x2, y2 = clamp_rect(text_rect, page_width, page_height)
    fallback = (x1, y1, x2, y2)
    fallback_polygon = [[x1, y1], [x2, y1], [x2, y2], [x1, y2]]
    text_width = x2 - x1
    text_height = y2 - y1
    pad_x = max(36, int(text_width * 1.35))
    pad_y = max(28, int(text_height * 1.15))
    crop_left, crop_top, crop_right, crop_bottom = clamp_rect(
        (x1 - pad_x, y1 - pad_y, x2 + pad_x, y2 + pad_y),
        page_width,
        page_height,
    )
    crop = clean_rgb[crop_top:crop_bottom, crop_left:crop_right]
    lab = cv2.cvtColor(crop, cv2.COLOR_RGB2LAB).astype(np.float32)

    local_x1, local_y1 = x1 - crop_left, y1 - crop_top
    local_x2, local_y2 = x2 - crop_left, y2 - crop_top
    inset_x = max(1, text_width // 5)
    inset_y = max(1, text_height // 5)
    sample = lab[
        max(0, local_y1 + inset_y):max(local_y1 + inset_y + 1, local_y2 - inset_y),
        max(0, local_x1 + inset_x):max(local_x1 + inset_x + 1, local_x2 - inset_x),
    ]
    if sample.size == 0:
        return fallback, 0.0, fallback, fallback_polygon

    target = np.median(sample.reshape(-1, 3), axis=0)
    delta = np.linalg.norm(lab - target, axis=2)
    uniform = (delta < 22).astype(np.uint8)
    uniform = cv2.morphologyEx(
        uniform,
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)),
        iterations=1,
    )

    labels_count, labels, stats, _ = cv2.connectedComponentsWithStats(uniform, connectivity=8)
    if labels_count <= 1:
        return fallback, 0.0, fallback, fallback_polygon

    centre_x = max(0, min((local_x1 + local_x2) // 2, labels.shape[1] - 1))
    centre_y = max(0, min((local_y1 + local_y2) // 2, labels.shape[0] - 1))
    label = int(labels[centre_y, centre_x])
    if label == 0:
        text_slice = labels[local_y1:local_y2, local_x1:local_x2]
        values, counts = np.unique(text_slice[text_slice > 0], return_counts=True)
        if len(values) == 0:
            return fallback, 0.0, fallback, fallback_polygon
        label = int(values[int(np.argmax(counts))])

    component = (labels == label).astype(np.uint8)
    component_area = int(stats[label, cv2.CC_STAT_AREA])
    component_left = int(stats[label, cv2.CC_STAT_LEFT]) + crop_left
    component_top = int(stats[label, cv2.CC_STAT_TOP]) + crop_top
    component_right = component_left + int(stats[label, cv2.CC_STAT_WIDTH])
    component_bottom = component_top + int(stats[label, cv2.CC_STAT_HEIGHT])
    bubble_rect = clamp_rect(
        (component_left, component_top, component_right, component_bottom),
        page_width,
        page_height,
    )
    text_area = max(1, text_width * text_height)
    overlap = stripe_ratio(component, local_x1, local_y1, local_x2, local_y2)
    if overlap < 0.62 or component_area < text_area * 0.8:
        return fallback, 0.0, bubble_rect, fallback_polygon

    margin = max(3, int(min(text_width, text_height) * 0.035))
    safe = cv2.erode(
        component,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (margin * 2 + 1, margin * 2 + 1)),
        iterations=1,
    )

    rectangle = largest_inner_rectangle(safe, centre_x, centre_y)
    if rectangle is None:
        return fallback, 0.0, bubble_rect, fallback_polygon
    left, top, right, bottom = rectangle
    if (right - left) < text_width * 0.95 or (bottom - top) < text_height * 0.95:
        return fallback, 0.0, bubble_rect, fallback_polygon

    # Una cola abierta o un bocadillo pegado al margen puede conectar con el papel
    # exterior. La zona útil nunca debe alejarse más de medio bloque del texto real.
    limit_x = max(10, int(text_width * 0.35))
    limit_y = max(8, int(text_height * 0.25))
    vicinity_left = max(0, x1 - limit_x - crop_left)
    vicinity_top = max(0, y1 - limit_y - crop_top)
    vicinity_right = min(safe.shape[1], x2 + limit_x - crop_left)
    vicinity_bottom = min(safe.shape[0], y2 + limit_y - crop_top)
    bounded_safe = np.zeros_like(safe)
    bounded_safe[vicinity_top:vicinity_bottom, vicinity_left:vicinity_right] = safe[
        vicinity_top:vicinity_bottom,
        vicinity_left:vicinity_right,
    ]

    contours, _ = cv2.findContours(bounded_safe, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return fallback, 0.0, bubble_rect, fallback_polygon
    containing = [
        contour
        for contour in contours
        if cv2.pointPolygonTest(contour, (float(centre_x), float(centre_y)), False) >= 0
    ]
    contour = max(containing or contours, key=cv2.contourArea)
    if cv2.contourArea(contour) < text_area * 0.72:
        return fallback, 0.0, bubble_rect, fallback_polygon

    perimeter = cv2.arcLength(contour, True)
    simplified = cv2.approxPolyDP(contour, max(1.5, perimeter * 0.012), True)
    points = simplified.reshape(-1, 2)
    if len(points) < 3:
        points = contour.reshape(-1, 2)
    safe_polygon = [
        [int(point[0]) + crop_left, int(point[1]) + crop_top]
        for point in points
    ]
    polygon_array = np.asarray(safe_polygon, dtype=np.int32)
    polygon_left, polygon_top, polygon_width, polygon_height = cv2.boundingRect(polygon_array)
    render = clamp_rect(
        (
            polygon_left,
            polygon_top,
            polygon_left + polygon_width,
            polygon_top + polygon_height,
        ),
        page_width,
        page_height,
    )
    confidence = min(1.0, max(0.0, overlap * min(1.0, component_area / text_area / 2.0)))
    return render, confidence, bubble_rect, safe_polygon


def rect_payload(rect: tuple[int, int, int, int]) -> dict:
    left, top, right, bottom = rect
    return {"x": left, "y": top, "width": right - left, "height": bottom - top}


def region_payload(region, clean_rgb: np.ndarray, index: int) -> dict:
    page_height, page_width = clean_rgb.shape[:2]
    xyxy = tuple(int(value) for value in region.xyxy.tolist())
    text_rect = clamp_rect(xyxy, page_width, page_height)
    render_rect, bubble_confidence, bubble_rect, safe_polygon = find_safe_render_box(clean_rgb, text_rect)
    original = (getattr(region, "text_raw", None) or region.text or "").strip()
    letters = [character for character in original if character.isalpha()]
    uppercase = bool(letters) and all(character.isupper() for character in letters)
    short_sfx = uppercase and len(letters) <= 18 and len(region.lines) <= 2 and bubble_confidence < 0.45
    probability = float(getattr(region, "prob", 0.75) or 0.75)
    return {
        "order": index,
        "original": original,
        "confidence": max(0.0, min(1.0, probability)),
        "bubbleConfidence": bubble_confidence,
        "bubbleBox": rect_payload(bubble_rect),
        "type": "sfx" if short_sfx else "dialogue",
        "textBox": rect_payload(text_rect),
        "renderBox": rect_payload(render_rect),
        "shapePolygon": safe_polygon,
        "rotation": float(getattr(region, "angle", 0.0) or 0.0),
        "uppercase": uppercase,
        "lines": [np.asarray(line, dtype=int).reshape(-1, 2).tolist() for line in region.lines],
    }


def rectangle_union(first: dict, second: dict) -> dict:
    left = min(first["x"], second["x"])
    top = min(first["y"], second["y"])
    right = max(first["x"] + first["width"], second["x"] + second["width"])
    bottom = max(first["y"] + first["height"], second["y"] + second["height"])
    return {"x": left, "y": top, "width": right - left, "height": bottom - top}


def polygon_hull(first: list[list[int]], second: list[list[int]]) -> list[list[int]]:
    points = np.asarray(first + second, dtype=np.int32).reshape(-1, 1, 2)
    if len(points) < 3:
        return first + second
    return cv2.convexHull(points).reshape(-1, 2).astype(int).tolist()


def overlap_over_smaller(first: dict, second: dict) -> float:
    left = max(first["x"], second["x"])
    top = max(first["y"], second["y"])
    right = min(first["x"] + first["width"], second["x"] + second["width"])
    bottom = min(first["y"] + first["height"], second["y"] + second["height"])
    intersection = max(0, right - left) * max(0, bottom - top)
    smaller = min(
        first["width"] * first["height"],
        second["width"] * second["height"],
    )
    return intersection / max(1, smaller)


def horizontal_overlap_ratio(first: dict, second: dict) -> float:
    left = max(first["x"], second["x"])
    right = min(first["x"] + first["width"], second["x"] + second["width"])
    return max(0, right - left) / max(1, min(first["width"], second["width"]))


def vertical_gap(first: dict, second: dict) -> float:
    first_bottom = first["y"] + first["height"]
    second_bottom = second["y"] + second["height"]
    if second["y"] >= first_bottom:
        return second["y"] - first_bottom
    if first["y"] >= second_bottom:
        return first["y"] - second_bottom
    return 0.0


def merge_split_bubbles(regions: list[dict]) -> list[dict]:
    """Reúne bloques consecutivos que el OCR separó dentro del mismo bocadillo."""

    merged: list[dict] = []
    for region in regions:
        if (
            merged
            and region["type"] == "dialogue"
            and merged[-1]["type"] == "dialogue"
            and region["bubbleConfidence"] >= 0.65
            and merged[-1]["bubbleConfidence"] >= 0.65
            and overlap_over_smaller(merged[-1]["bubbleBox"], region["bubbleBox"]) >= 0.55
            and horizontal_overlap_ratio(merged[-1]["textBox"], region["textBox"]) >= 0.45
            and vertical_gap(merged[-1]["textBox"], region["textBox"])
            <= max(40, min(merged[-1]["textBox"]["height"], region["textBox"]["height"]) * 1.25)
        ):
            previous = merged[-1]
            previous["original"] = f'{previous["original"].rstrip()} {region["original"].lstrip()}'.strip()
            previous["confidence"] = min(previous["confidence"], region["confidence"])
            previous["bubbleConfidence"] = min(previous["bubbleConfidence"], region["bubbleConfidence"])
            previous["textBox"] = rectangle_union(previous["textBox"], region["textBox"])
            previous["shapePolygon"] = polygon_hull(
                previous["shapePolygon"],
                region["shapePolygon"],
            )
            polygon = np.asarray(previous["shapePolygon"], dtype=np.int32)
            left, top, width, height = cv2.boundingRect(polygon)
            previous["renderBox"] = {
                "x": int(left),
                "y": int(top),
                "width": int(width),
                "height": int(height),
            }
            previous["bubbleBox"] = rectangle_union(previous["bubbleBox"], region["bubbleBox"])
            previous["uppercase"] = previous["uppercase"] and region["uppercase"]
            previous["lines"].extend(region["lines"])
        else:
            merged.append(region)
    for index, region in enumerate(merged):
        region["order"] = index
    return merged


async def analyze(args: argparse.Namespace) -> int:
    started = time.perf_counter()
    input_path = Path(args.input).resolve()
    output_dir = Path(args.output).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    config_path = Path(args.config).resolve()

    with config_path.open("r", encoding="utf-8") as handle:
        config = Config(**json.load(handle))

    params = {
        "use_gpu": not args.cpu,
        "use_gpu_limited": False,
        "verbose": False,
        "ignore_errors": False,
        "kernel_size": config.kernel_size,
        "input": [str(input_path)],
        "prep_manual": True,
        "models_ttl": 0,
        "batch_size": 1,
    }
    translator = TintaTranslator(params)
    with Image.open(input_path) as source:
        image = source.convert("RGB")
        ctx = await translator.translate(image, config, skip_context_save=True)

    if ctx.mask is None or getattr(ctx, "img_inpainted", None) is None:
        raise RuntimeError("El motor no pudo generar la máscara o el fondo limpio.")

    mask_path = output_dir / "mask.png"
    clean_path = output_dir / "clean.png"
    manifest_path = output_dir / "analysis.json"
    cv2.imwrite(str(mask_path), ctx.mask)
    cv2.imwrite(str(clean_path), cv2.cvtColor(ctx.img_inpainted, cv2.COLOR_RGB2BGR))
    regions = merge_split_bubbles(
        [region_payload(region, ctx.img_inpainted, index) for index, region in enumerate(ctx.text_regions)]
    )
    manifest = {
        "sourceLanguage": "en",
        "width": int(ctx.img_inpainted.shape[1]),
        "height": int(ctx.img_inpainted.shape[0]),
        "cleanImage": str(clean_path),
        "maskImage": str(mask_path),
        "regions": regions,
        "elapsedSeconds": round(time.perf_counter() - started, 3),
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    emit({"type": "complete", "percent": 100, "message": "Fondo reconstruido", "manifest": str(manifest_path)})
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Motor orgánico local de Tinta ES")
    subparsers = parser.add_subparsers(dest="command", required=True)
    analyze_parser = subparsers.add_parser("analyze")
    analyze_parser.add_argument("--input", required=True)
    analyze_parser.add_argument("--output", required=True)
    analyze_parser.add_argument("--config", default=str(ENGINE_DIR / "organic-engine-config.json"))
    analyze_parser.add_argument("--cpu", action="store_true")
    args = parser.parse_args()

    try:
        if args.command == "analyze":
            return asyncio.run(analyze(args))
        return 2
    except Exception as exception:
        emit({"type": "error", "message": str(exception)})
        raise


if __name__ == "__main__":
    raise SystemExit(main())
