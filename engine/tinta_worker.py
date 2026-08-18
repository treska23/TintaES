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
from manga_translator.ocr import dispatch as dispatch_ocr  # noqa: E402
from manga_translator.utils import Quadrilateral  # noqa: E402


PROGRESS = {
    "running_pre_translation_hooks": (2, "Preparando el motor local"),
    "detection": (12, "Localizando letras y bocadillos"),
    "ocr": (34, "Leyendo el texto original"),
    "textline_merge": (53, "Agrupando frases por bocadillo"),
    "translating": (58, "Preparando el texto para traducir"),
    "after-translating": (61, "Comprobando las zonas detectadas"),
    "mask-generation": (67, "Ajustando la máscara a las letras"),
    "inpainting": (76, "Conservando la página original"),
    "rendering": (94, "Preparando las zonas del lector"),
    "downscaling": (97, "Restaurando el tamaño original"),
}


def emit(payload: dict) -> None:
    print(json.dumps(payload, ensure_ascii=False), flush=True)


class TintaTranslator(MangaTranslator):
    """Pipeline del proyecto con una máscara que nunca pierde letras detectadas."""

    def __init__(
        self,
        params: dict,
        supplemental_regions: list[dict] | None = None,
        bright_candidates: list[dict] | None = None,
    ) -> None:
        super().__init__(params)
        self.supplemental_regions = (
            supplemental_regions if supplemental_regions is not None else []
        )
        self.bright_candidates = bright_candidates or []
        self.candidate_diagnostics: list[dict] = []
        # Evita que los logs de la librería se mezclen con el protocolo JSON.
        self._progress_hooks.clear()

    async def _report_progress(self, state: str, finished: bool = False) -> None:
        percent, message = PROGRESS.get(state, (0, state))
        emit({"type": "progress", "state": state, "percent": percent, "message": message})

    async def _run_ocr(self, config: Config, ctx):
        textlines = await super()._run_ocr(config, ctx)
        unresolved = [
            candidate
            for candidate in self.bright_candidates
            if (
                str(candidate.get("polarity", "bright")).lower() == "dark"
                or not overlaps_detected_text(candidate, textlines)
            )
            and not overlaps_supplemental_text(candidate, self.supplemental_regions)
            and (
                str(candidate.get("polarity", "bright")).lower() == "dark"
                or looks_like_text_candidate(ctx.img_rgb, candidate)
            )
        ]
        if not unresolved:
            return textlines

        quadrilaterals = [
            create_candidate_quadrilateral(ctx.img_rgb, candidate)
            for candidate in unresolved
        ]
        original_probability = config.ocr.prob
        try:
            # La pasada principal ya filtró con el umbral editorial. En estos recortes
            # de rescate necesitamos ver también hipótesis de baja confianza para poder
            # validarlas después con geometría, longitud y probabilidad por polaridad.
            config.ocr.prob = 0.0
            recognized = await dispatch_ocr(
                config.ocr.ocr,
                ctx.img_rgb,
                quadrilaterals,
                config.ocr,
                self.device,
                self.verbose,
            )
        finally:
            config.ocr.prob = original_probability
        by_identity = {
            id(quadrilateral): candidate
            for quadrilateral, candidate in zip(quadrilaterals, unresolved)
        }
        for region in recognized:
            candidate = by_identity.get(id(region))
            value = str(getattr(region, "text", "")).strip()
            letters = [character for character in value if character.isalpha()]
            probability = float(getattr(region, "prob", 0.0) or 0.0)
            minimum_probability = (
                0.18
                if candidate is not None
                and str(candidate.get("polarity", "bright")).lower() == "dark"
                else 0.42
            )
            self.candidate_diagnostics.append({
                "id": candidate.get("id") if candidate is not None else None,
                "polarity": (
                    candidate.get("polarity")
                    if candidate is not None
                    else None
                ),
                "text": value,
                "probability": round(probability, 4),
            })
            if (
                candidate is None
                or probability < minimum_probability
                or len(letters) < 3
                or len(letters) > 22
                or len(value.split()) > 3
            ):
                continue
            self.supplemental_regions.append({
                "original": value,
                "type": "sfx",
                "confidence": min(0.90, max(0.58, probability)),
                "x": int(candidate.get("x", 0)),
                "y": int(candidate.get("y", 0)),
                "width": int(candidate.get("width", 1)),
                "height": int(candidate.get("height", 1)),
            })
        return textlines

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
        supplemental = create_supplemental_mask(
            ctx.img_rgb,
            self.supplemental_regions,
            raw,
        )
        raw = cv2.bitwise_or(raw, supplemental)
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


def candidate_rectangle(candidate: dict) -> tuple[int, int, int, int]:
    left = int(candidate.get("x", 0))
    top = int(candidate.get("y", 0))
    return (
        left,
        top,
        left + int(candidate.get("width", 1)),
        top + int(candidate.get("height", 1)),
    )


def rectangle_overlap_over_smaller(
    first: tuple[int, int, int, int],
    second: tuple[int, int, int, int],
) -> float:
    left = max(first[0], second[0])
    top = max(first[1], second[1])
    right = min(first[2], second[2])
    bottom = min(first[3], second[3])
    intersection = max(0, right - left) * max(0, bottom - top)
    first_area = max(1, (first[2] - first[0]) * (first[3] - first[1]))
    second_area = max(1, (second[2] - second[0]) * (second[3] - second[1]))
    return intersection / min(first_area, second_area)


def overlaps_detected_text(candidate: dict, textlines: list) -> bool:
    candidate_rect = candidate_rectangle(candidate)
    return any(
        rectangle_overlap_over_smaller(
            candidate_rect,
            tuple(int(value) for value in textline.xyxy),
        ) >= 0.28
        for textline in textlines
    )


def overlaps_supplemental_text(candidate: dict, regions: list[dict]) -> bool:
    candidate_rect = candidate_rectangle(candidate)
    return any(
        rectangle_overlap_over_smaller(
            candidate_rect,
            candidate_rectangle(region),
        ) >= 0.42
        for region in regions
    )


def create_candidate_quadrilateral(
    image_rgb: np.ndarray,
    candidate: dict,
) -> Quadrilateral:
    height, width = image_rgb.shape[:2]
    left, top, right, bottom = clamp_rect(candidate_rectangle(candidate), width, height)
    crop = image_rgb[top:bottom, left:right]
    gray = cv2.cvtColor(crop, cv2.COLOR_RGB2GRAY)
    strokes = candidate_stroke_mask(gray, candidate)
    points = cv2.findNonZero(strokes)
    if points is not None and len(points) >= 8:
        box = cv2.boxPoints(cv2.minAreaRect(points)).astype(np.float32)
        box[:, 0] += left
        box[:, 1] += top
    else:
        box = np.asarray(
            [[left, top], [right, top], [right, bottom], [left, bottom]],
            dtype=np.float32,
        )
    return Quadrilateral(box, "", 0.7)


def looks_like_text_candidate(image_rgb: np.ndarray, candidate: dict) -> bool:
    height, width = image_rgb.shape[:2]
    left, top, right, bottom = clamp_rect(candidate_rectangle(candidate), width, height)
    candidate_width = right - left
    candidate_height = bottom - top
    minimum_aspect = (
        0.70
        if str(candidate.get("polarity", "bright")).lower() == "dark"
        else 1.75
    )
    if candidate_width / max(1, candidate_height) < minimum_aspect:
        return False

    crop = image_rgb[top:bottom, left:right]
    gray = cv2.cvtColor(crop, cv2.COLOR_RGB2GRAY)
    strokes = candidate_stroke_mask(gray, candidate)
    count, _, stats, _ = cv2.connectedComponentsWithStats(strokes, 8)
    if count <= 1:
        return False
    areas = stats[1:, cv2.CC_STAT_AREA]
    components = int(np.count_nonzero(areas >= 5))
    largest_ratio = float(np.max(areas)) / max(1, candidate_width * candidate_height)
    maximum_component_ratio = (
        0.45
        if str(candidate.get("polarity", "bright")).lower() == "dark"
        else 0.15
    )
    return components >= 2 and largest_ratio <= maximum_component_ratio


def candidate_stroke_mask(gray: np.ndarray, candidate: dict) -> np.ndarray:
    if str(candidate.get("polarity", "bright")).lower() == "dark":
        local = cv2.GaussianBlur(gray, (0, 0), 75)
        return np.where(
            (gray <= 82)
            & (local >= 118)
            & ((local.astype(np.int16) - gray.astype(np.int16)) >= 48),
            255,
            0,
        ).astype(np.uint8)

    local = cv2.GaussianBlur(gray, (0, 0), 13)
    return np.where(
        (gray >= 205)
        & (local <= 188)
        & ((gray.astype(np.int16) - local.astype(np.int16)) >= 38),
        255,
        0,
    ).astype(np.uint8)


def create_supplemental_mask(
    image_rgb: np.ndarray,
    regions: list[dict],
    raw_mask: np.ndarray,
) -> np.ndarray:
    """Convierte rescates OCR en trazos; nunca en placas rectangulares."""

    height, width = image_rgb.shape[:2]
    result = np.zeros((height, width), dtype=np.uint8)
    if not regions:
        return result

    gray = cv2.cvtColor(image_rgb, cv2.COLOR_RGB2GRAY)
    local = cv2.GaussianBlur(gray, (0, 0), 13)
    bright_strokes = np.where(
        (gray >= 205)
        & (local <= 188)
        & ((gray.astype(np.int16) - local.astype(np.int16)) >= 38),
        255,
        0,
    ).astype(np.uint8)
    raw_binary = np.where(raw_mask > 20, 255, 0).astype(np.uint8)

    for region in regions:
        x = int(region.get("x", 0))
        y = int(region.get("y", 0))
        region_width = int(region.get("width", 0))
        region_height = int(region.get("height", 0))
        if region_width <= 1 or region_height <= 1:
            continue
        left, top, right, bottom = clamp_rect(
            (
                x - 4,
                y - 4,
                x + region_width + 4,
                y + region_height + 4,
            ),
            width,
            height,
        )

        raw_crop = raw_binary[top:bottom, left:right]
        stroke_crop = np.zeros_like(raw_crop)
        raw_ratio = float(np.mean(raw_crop > 0)) if raw_crop.size else 0.0
        region_type = str(region.get("type", "")).lower()
        crop = gray[top:bottom, left:right]
        if region_type == "sign" and crop.size:
            background = float(np.percentile(crop, 78))
            dark_crop = np.where(crop <= background - 24, 255, 0).astype(np.uint8)
            dark_ratio = float(np.mean(dark_crop > 0))
            if 0.004 <= dark_ratio <= 0.42:
                stroke_crop = cv2.bitwise_or(raw_crop, dark_crop)
            elif 0.002 <= raw_ratio <= 0.45:
                stroke_crop = raw_crop
        elif 0.002 <= raw_ratio <= 0.45:
            stroke_crop = raw_crop
        else:
            bright_crop = bright_strokes[top:bottom, left:right]
            bright_ratio = float(np.mean(bright_crop > 0)) if bright_crop.size else 0.0
            if 0.004 <= bright_ratio <= 0.38:
                stroke_crop = bright_crop
            else:
                if crop.size == 0:
                    continue
                border = np.concatenate([
                    crop[:2, :].reshape(-1),
                    crop[-2:, :].reshape(-1),
                    crop[:, :2].reshape(-1),
                    crop[:, -2:].reshape(-1),
                ])
                background = float(np.median(border))
                if background >= 145:
                    stroke_crop = np.where(crop <= background - 35, 255, 0).astype(np.uint8)
                else:
                    stroke_crop = np.where(crop >= background + 45, 255, 0).astype(np.uint8)
                fallback_ratio = float(np.mean(stroke_crop > 0))
                if fallback_ratio < 0.004 or fallback_ratio > 0.38:
                    continue

        stroke_crop = cv2.morphologyEx(
            stroke_crop,
            cv2.MORPH_OPEN,
            cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2, 2)),
        )
        stroke_crop = cv2.dilate(
            stroke_crop,
            cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7)),
            iterations=1,
        )
        result[top:bottom, left:right] = cv2.bitwise_or(
            result[top:bottom, left:right],
            stroke_crop,
        )
    return result


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


LETTERING_SHEARS = np.arange(-0.35, 0.351, 0.025, dtype=np.float32)


def lettering_foreground_mask(crop_rgb: np.ndarray) -> tuple[np.ndarray, float]:
    """AÃ­sla los trazos dentro de una lÃ­nea OCR sin confundirlos con el bocadillo."""

    gray = cv2.cvtColor(crop_rgb, cv2.COLOR_RGB2GRAY)
    blurred = cv2.GaussianBlur(gray, (3, 3), 0)
    border_size = max(1, min(3, min(gray.shape) // 8))
    border = np.concatenate([
        gray[:border_size, :].reshape(-1),
        gray[-border_size:, :].reshape(-1),
        gray[:, :border_size].reshape(-1),
        gray[:, -border_size:].reshape(-1),
    ])
    background = float(np.median(border))
    threshold_mode = cv2.THRESH_BINARY_INV if background >= 128 else cv2.THRESH_BINARY
    _, raw = cv2.threshold(
        blurred,
        0,
        255,
        threshold_mode | cv2.THRESH_OTSU,
    )

    count, labels, stats, _ = cv2.connectedComponentsWithStats(raw, 8)
    filtered = np.zeros_like(raw)
    crop_area = max(1, raw.size)
    minimum_height = max(2, int(round(raw.shape[0] * 0.10)))
    for label in range(1, count):
        component_height = int(stats[label, cv2.CC_STAT_HEIGHT])
        component_area = int(stats[label, cv2.CC_STAT_AREA])
        if (
            component_height >= minimum_height
            and 3 <= component_area <= crop_area * 0.38
        ):
            filtered[labels == label] = 255
    return filtered, background


def lettering_line_rectangles(
    lines,
    text_rect: tuple[int, int, int, int],
    page_width: int,
    page_height: int,
) -> list[tuple[int, int, int, int]]:
    rectangles: list[tuple[int, int, int, int]] = []
    source_lines = [] if lines is None else lines
    for line in source_lines:
        points = np.asarray(line, dtype=np.float32).reshape(-1, 2)
        if len(points) < 3:
            continue
        x, y, width, height = cv2.boundingRect(points.astype(np.int32))
        if width >= 3 and height >= 3:
            rectangles.append(clamp_rect(
                (x - 2, y - 2, x + width + 2, y + height + 2),
                page_width,
                page_height,
            ))
    if not rectangles:
        left, top, right, bottom = text_rect
        rectangles.append(clamp_rect(
            (left - 2, top - 2, right + 2, bottom + 2),
            page_width,
            page_height,
        ))
    return rectangles


def accumulate_lettering_shear(
    mask: np.ndarray,
    accumulated_scores: np.ndarray,
) -> float:
    """Mide cuÃ¡nto hay que enderezar los trazos para alinear sus astas verticales."""

    count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    accumulated_weight = 0.0
    for label in range(1, count):
        x = int(stats[label, cv2.CC_STAT_LEFT])
        y = int(stats[label, cv2.CC_STAT_TOP])
        width = int(stats[label, cv2.CC_STAT_WIDTH])
        height = int(stats[label, cv2.CC_STAT_HEIGHT])
        area = int(stats[label, cv2.CC_STAT_AREA])
        if (
            height < 8
            or width < 2
            or width > height * 1.8
            or area < 10
        ):
            continue

        component = (labels[y:y + height, x:x + width] == label).astype(np.uint8)
        padding = int(height * 0.4) + 3
        component = cv2.copyMakeBorder(
            component,
            0,
            0,
            padding,
            padding,
            cv2.BORDER_CONSTANT,
        )
        component_scores: list[float] = []
        for shear in LETTERING_SHEARS:
            transform = np.float32([
                [1, -float(shear), float(shear) * (height - 1)],
                [0, 1, 0],
            ])
            straightened = cv2.warpAffine(
                component,
                transform,
                (component.shape[1], height),
                flags=cv2.INTER_NEAREST,
            )
            projection = straightened.sum(axis=0).astype(np.float64)
            component_scores.append(
                float(np.sum(projection * projection)) / max(1, area * area)
            )

        scores = np.asarray(component_scores, dtype=np.float64)
        maximum = float(np.max(scores))
        if maximum <= 0:
            continue
        accumulated_scores += scores / maximum * height
        accumulated_weight += height
    return accumulated_weight


def lettering_style_payload(
    image_rgb: np.ndarray,
    lines,
    text_rect: tuple[int, int, int, int],
    uppercase: bool,
) -> dict:
    """Infere peso, inclinaciÃ³n y color antes de que LaMa borre la rotulaciÃ³n."""

    page_height, page_width = image_rgb.shape[:2]
    line_rectangles = lettering_line_rectangles(
        lines,
        text_rect,
        page_width,
        page_height,
    )
    stroke_ratios: list[float] = []
    foreground_colours: list[np.ndarray] = []
    shear_scores = np.zeros(len(LETTERING_SHEARS), dtype=np.float64)
    shear_weight = 0.0

    for left, top, right, bottom in line_rectangles:
        crop_rgb = image_rgb[top:bottom, left:right]
        if crop_rgb.size == 0:
            continue
        foreground, _ = lettering_foreground_mask(crop_rgb)
        ink_ratio = float(np.mean(foreground > 0))
        if ink_ratio < 0.003 or ink_ratio > 0.45:
            continue

        distance = cv2.distanceTransform(foreground, cv2.DIST_L2, 3)
        positive = distance[distance > 0]
        line_height = max(1, bottom - top - 4)
        if positive.size:
            stroke_ratios.append(
                float(2 * np.percentile(positive, 75) / line_height)
            )
        core = distance >= max(1.0, float(np.percentile(positive, 45))) \
            if positive.size else foreground > 0
        if np.any(core):
            foreground_colours.append(crop_rgb[core])
        shear_weight += accumulate_lettering_shear(foreground, shear_scores)

    if stroke_ratios:
        stroke_ratio = float(np.median(stroke_ratios))
        if stroke_ratio >= 0.090:
            font_weight = 850
        elif stroke_ratio >= 0.077:
            font_weight = 800
        elif stroke_ratio >= 0.064:
            font_weight = 650
        elif stroke_ratio >= 0.050:
            font_weight = 550
        else:
            font_weight = 450
    else:
        font_weight = 700 if uppercase else 600

    italic = False
    if shear_weight > 0:
        normalized_scores = shear_scores / shear_weight
        best_index = int(np.argmax(normalized_scores))
        straight_index = int(np.argmin(np.abs(LETTERING_SHEARS)))
        straight_score = float(normalized_scores[straight_index])
        improvement = (
            float(normalized_scores[best_index]) / straight_score - 1
            if straight_score > 0
            else 0
        )
        italic = (
            abs(float(LETTERING_SHEARS[best_index])) >= 0.10
            and improvement >= 0.055
        )

    text_colour = None
    if foreground_colours:
        pixels = np.concatenate(foreground_colours, axis=0)
        median = np.median(pixels, axis=0).round().astype(np.uint8)
        text_colour = f"#{median[0]:02X}{median[1]:02X}{median[2]:02X}"

    return {
        "fontWeight": font_weight,
        "fontWidthRatio": 1.0,
        "italic": italic,
        "textColor": text_colour,
    }


def region_payload(
    region,
    clean_rgb: np.ndarray,
    original_rgb: np.ndarray,
    index: int,
) -> dict:
    page_height, page_width = clean_rgb.shape[:2]
    xyxy = tuple(int(value) for value in region.xyxy.tolist())
    text_rect = clamp_rect(xyxy, page_width, page_height)
    render_rect, bubble_confidence, bubble_rect, safe_polygon = find_safe_render_box(clean_rgb, text_rect)
    original = (getattr(region, "text_raw", None) or region.text or "").strip()
    letters = [character for character in original if character.isalpha()]
    uppercase = bool(letters) and all(character.isupper() for character in letters)
    short_sfx = uppercase and len(letters) <= 18 and len(region.lines) <= 2 and bubble_confidence < 0.45
    probability = float(getattr(region, "prob", 0.75) or 0.75)
    payload = {
        "order": index,
        "original": original,
        "ocrAlternatives": [],
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
    payload.update(lettering_style_payload(
        original_rgb,
        region.lines,
        text_rect,
        uppercase,
    ))
    return payload


def supplemental_region_payload(
    source: dict,
    clean_rgb: np.ndarray,
    original_rgb: np.ndarray,
    index: int,
) -> dict:
    page_height, page_width = clean_rgb.shape[:2]
    text_rect = clamp_rect(
        (
            int(source.get("x", 0)),
            int(source.get("y", 0)),
            int(source.get("x", 0)) + int(source.get("width", 1)),
            int(source.get("y", 0)) + int(source.get("height", 1)),
        ),
        page_width,
        page_height,
    )
    region_type = str(source.get("type", "dialogue")).lower()
    if region_type not in {"dialogue", "thought", "caption", "narration", "sign", "sfx"}:
        region_type = "dialogue"

    if region_type in {"dialogue", "thought"}:
        render_rect, bubble_confidence, bubble_rect, safe_polygon = find_safe_render_box(
            clean_rgb,
            text_rect,
        )
    else:
        render_rect = text_rect
        bubble_confidence = 0.0
        bubble_rect = text_rect
        left, top, right, bottom = text_rect
        safe_polygon = [[left, top], [right, top], [right, bottom], [left, bottom]]

    original = str(source.get("original", "")).strip()
    letters = [character for character in original if character.isalpha()]
    uppercase = bool(letters) and all(character.isupper() for character in letters)
    payload = {
        "order": index,
        "original": original,
        "ocrAlternatives": [],
        "confidence": max(0.0, min(1.0, float(source.get("confidence", 0.72)))),
        "bubbleConfidence": bubble_confidence,
        "bubbleBox": rect_payload(bubble_rect),
        "type": region_type,
        "textBox": rect_payload(text_rect),
        "renderBox": rect_payload(render_rect),
        "shapePolygon": safe_polygon,
        "rotation": 0.0,
        "uppercase": uppercase,
        "lines": [],
    }
    style = lettering_style_payload(
        original_rgb,
        [],
        text_rect,
        uppercase,
    )
    if not style.get("textColor") and region_type == "sfx":
        style["textColor"] = "#F7F4E8"
    payload.update(style)
    return payload


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


def attach_ocr_alternatives(
    primary_regions: list[dict],
    supplemental_regions: list[dict],
) -> None:
    """Asocia lecturas coincidentes sin sustituir ni concatenar bocadillos."""

    for primary in primary_regions:
        alternatives: list[str] = []
        primary_box = primary["textBox"]
        for source in supplemental_regions:
            source_box = {
                "x": int(source.get("x", 0)),
                "y": int(source.get("y", 0)),
                "width": int(source.get("width", 1)),
                "height": int(source.get("height", 1)),
            }
            if overlap_over_smaller(primary_box, source_box) < 0.42:
                continue

            readings = [source.get("original", "")]
            readings.extend(source.get("ocrAlternatives", []) or [])
            for reading in readings:
                value = str(reading).strip()
                if (
                    value
                    and value.casefold() != primary["original"].casefold()
                    and value.casefold() not in {item.casefold() for item in alternatives}
                ):
                    alternatives.append(value)
        primary["ocrAlternatives"] = alternatives[:8]


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
        merge_index = -1
        if region["type"] == "dialogue" and region["bubbleConfidence"] >= 0.65:
            for index in range(len(merged) - 1, -1, -1):
                previous = merged[index]
                if (
                    previous["type"] == "dialogue"
                    and previous["bubbleConfidence"] >= 0.65
                    and overlap_over_smaller(previous["bubbleBox"], region["bubbleBox"]) >= 0.55
                    and horizontal_overlap_ratio(previous["textBox"], region["textBox"]) >= 0.45
                    and vertical_gap(previous["textBox"], region["textBox"])
                    <= max(
                        40,
                        min(previous["textBox"]["height"], region["textBox"]["height"]) * 1.25,
                    )
                ):
                    merge_index = index
                    break

        if merge_index >= 0:
            previous = merged[merge_index]
            previous["original"] = f'{previous["original"].rstrip()} {region["original"].lstrip()}'.strip()
            previous["ocrAlternatives"] = list(dict.fromkeys(
                previous.get("ocrAlternatives", []) + region.get("ocrAlternatives", [])
            ))[:4]
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
            previous["italic"] = previous.get("italic", False) or region.get("italic", False)
            previous["fontWeight"] = max(
                int(previous.get("fontWeight", 700)),
                int(region.get("fontWeight", 700)),
            )
            previous["fontWidthRatio"] = round(
                (
                    float(previous.get("fontWidthRatio", 1.0))
                    + float(region.get("fontWidthRatio", 1.0))
                ) / 2,
                3,
            )
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
    supplemental_regions: list[dict] = []
    bright_candidates: list[dict] = []
    if args.supplemental:
        supplemental_path = Path(args.supplemental).resolve()
        if supplemental_path.exists():
            supplemental_payload = json.loads(supplemental_path.read_text(encoding="utf-8-sig"))
            supplemental_regions = list(supplemental_payload.get("regions", []))
            bright_candidates = list(supplemental_payload.get("brightCandidates", []))

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
    translator = TintaTranslator(params, supplemental_regions, bright_candidates)
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
    primary_regions = [
        region_payload(region, ctx.img_inpainted, ctx.img_rgb, index)
        for index, region in enumerate(ctx.text_regions)
    ]
    attach_ocr_alternatives(primary_regions, supplemental_regions)
    supplemental_payloads: list[dict] = []
    for source in supplemental_regions:
        candidate = supplemental_region_payload(
            source,
            ctx.img_inpainted,
            ctx.img_rgb,
            len(primary_regions) + len(supplemental_payloads),
        )
        if not candidate["original"]:
            continue
        if any(
            overlap_over_smaller(candidate["textBox"], primary["textBox"]) >= 0.42
            for primary in primary_regions
        ):
            continue
        if any(
            overlap_over_smaller(candidate["textBox"], existing["textBox"]) >= 0.55
            for existing in supplemental_payloads
        ):
            continue
        supplemental_payloads.append(candidate)

    ordered_regions = sorted(
        primary_regions + supplemental_payloads,
        key=lambda region: (
            region["textBox"]["y"] // 45,
            region["textBox"]["x"],
            region["textBox"]["y"],
        ),
    )
    regions = merge_split_bubbles(ordered_regions)
    manifest = {
        "sourceLanguage": "en",
        "width": int(ctx.img_inpainted.shape[1]),
        "height": int(ctx.img_inpainted.shape[0]),
        "cleanImage": str(clean_path),
        "maskImage": str(mask_path),
        "regions": regions,
        "candidateDiagnostics": translator.candidate_diagnostics,
        "elapsedSeconds": round(time.perf_counter() - started, 3),
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    emit({"type": "complete", "percent": 100, "message": "Bocadillos preparados", "manifest": str(manifest_path)})
    return 0


async def serve() -> int:
    """Mantiene importados PyTorch y los modelos entre páginas."""

    emit({"type": "ready", "percent": 0, "message": "Motor local preparado"})
    while True:
        line = await asyncio.to_thread(sys.stdin.readline)
        if not line:
            return 0
        try:
            request = json.loads(line.lstrip("\ufeff"))
            args = argparse.Namespace(
                input=str(request["input"]),
                output=str(request["output"]),
                supplemental=request.get("supplemental"),
                config=str(request.get(
                    "config",
                    ENGINE_DIR / "organic-engine-config.json",
                )),
                cpu=bool(request.get("cpu", False)),
            )
            await analyze(args)
        except Exception as exception:
            emit({"type": "error", "message": str(exception)})


def main() -> int:
    parser = argparse.ArgumentParser(description="Motor orgánico local de Tinta ES")
    subparsers = parser.add_subparsers(dest="command", required=True)
    analyze_parser = subparsers.add_parser("analyze")
    analyze_parser.add_argument("--input", required=True)
    analyze_parser.add_argument("--output", required=True)
    analyze_parser.add_argument("--supplemental")
    analyze_parser.add_argument("--config", default=str(ENGINE_DIR / "organic-engine-config.json"))
    analyze_parser.add_argument("--cpu", action="store_true")
    subparsers.add_parser("serve")
    args = parser.parse_args()

    try:
        if args.command == "analyze":
            return asyncio.run(analyze(args))
        if args.command == "serve":
            return asyncio.run(serve())
        return 2
    except Exception as exception:
        emit({"type": "error", "message": str(exception)})
        raise


if __name__ == "__main__":
    raise SystemExit(main())
