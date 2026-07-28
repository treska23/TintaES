from __future__ import annotations

import re
from typing import Any

import cv2
import numpy as np


def _text_quality(value: str, probability: float = 0.0) -> bool:
    text = re.sub(r"\s+", " ", str(value or "")).strip()
    if not text or len(text) > 420:
        return False

    letters = [character for character in text if character.isalpha()]
    visible = [character for character in text if not character.isspace()]
    if not visible:
        return False

    letter_ratio = len(letters) / len(visible)
    if letter_ratio < 0.42:
        return False

    # Los falsos positivos de dos letras (por ejemplo «Em») estaban generando zonas
    # limpias sin una traducción útil. Conservamos únicamente interjecciones reales.
    compact = "".join(letters).casefold()
    accepted_short = {"i", "a", "ok", "no", "go", "up", "ow", "oh", "ha", "hi"}
    if len(letters) < 3 and compact not in accepted_short:
        return False

    # Una lectura corta necesita más confianza que una frase completa.
    if len(letters) < 5 and probability < 0.62 and compact not in accepted_short:
        return False

    if re.fullmatch(r"(.)\1{2,}", compact):
        return False
    return True


def _expanded_rectangle(rect: tuple[int, int, int, int], width: int, height: int) -> tuple[int, int, int, int]:
    left, top, right, bottom = rect
    rect_width = max(1, right - left)
    rect_height = max(1, bottom - top)
    pad_x = max(7, int(round(rect_width * 0.035)))
    pad_y = max(6, int(round(rect_height * 0.14)))
    return (
        max(0, left - pad_x),
        max(0, top - pad_y),
        min(width, right + pad_x),
        min(height, bottom + pad_y),
    )


def apply(worker: Any) -> None:
    """Instala controles de calidad sin modificar manga-image-translator."""

    async def guarded_run_ocr(self, config, ctx):
        # Saltamos el override anterior, que limitaba el rescate a 22 letras y tres palabras.
        textlines = await super(worker.TintaTranslator, self)._run_ocr(config, ctx)
        unresolved = [
            candidate
            for candidate in self.bright_candidates
            if not worker.overlaps_detected_text(candidate, textlines)
            and not worker.overlaps_supplemental_text(candidate, self.supplemental_regions)
            and worker.looks_like_text_candidate(ctx.img_rgb, candidate)
        ]
        if not unresolved:
            return textlines

        quadrilaterals = [
            worker.create_candidate_quadrilateral(ctx.img_rgb, candidate)
            for candidate in unresolved
        ]
        recognized = await worker.dispatch_ocr(
            config.ocr.ocr,
            ctx.img_rgb,
            quadrilaterals,
            config.ocr,
            self.device,
            self.verbose,
        )
        by_identity = {
            id(quadrilateral): candidate
            for quadrilateral, candidate in zip(quadrilaterals, unresolved)
        }
        for region in recognized:
            candidate = by_identity.get(id(region))
            value = str(getattr(region, "text", "")).strip()
            probability = float(getattr(region, "prob", 0.0) or 0.0)
            if candidate is None or probability < 0.34 or not _text_quality(value, probability):
                continue
            self.supplemental_regions.append({
                "original": value,
                "type": "caption" if len(value.split()) > 3 else "sfx",
                "confidence": min(0.94, max(0.58, probability)),
                "x": int(candidate.get("x", 0)),
                "y": int(candidate.get("y", 0)),
                "width": int(candidate.get("width", 1)),
                "height": int(candidate.get("height", 1)),
            })
        return textlines

    async def guarded_mask_refinement(self, config, ctx):
        refined = await super(worker.TintaTranslator, self)._run_mask_refinement(config, ctx)
        if ctx.mask_raw is None:
            return refined

        raw = ctx.mask_raw
        if raw.ndim == 3:
            raw = cv2.cvtColor(raw, cv2.COLOR_BGR2GRAY)
        raw = np.where(raw > 20, 255, 0).astype(np.uint8)
        height, width = raw.shape

        # CTD puede detectar manchas que OCR no puede leer. Antes se borraban igualmente.
        # La puerta limita el borrado a regiones con texto utilizable y a rescates auxiliares.
        gate = np.zeros_like(raw)
        for region in getattr(ctx, "text_regions", []) or []:
            value = (getattr(region, "text_raw", None) or getattr(region, "text", "") or "").strip()
            probability = float(getattr(region, "prob", 0.0) or 0.0)
            if not _text_quality(value, probability):
                continue
            rect = tuple(int(item) for item in region.xyxy)
            left, top, right, bottom = _expanded_rectangle(rect, width, height)
            gate[top:bottom, left:right] = 255

        accepted_supplemental: list[dict] = []
        for source in self.supplemental_regions:
            value = str(source.get("original", "")).strip()
            probability = float(source.get("confidence", 0.72) or 0.72)
            if not _text_quality(value, probability):
                continue
            accepted_supplemental.append(source)
            rect = worker.candidate_rectangle(source)
            left, top, right, bottom = _expanded_rectangle(rect, width, height)
            gate[top:bottom, left:right] = 255

        raw = cv2.bitwise_and(raw, gate)
        raw = cv2.dilate(
            raw,
            cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (11, 11)),
            iterations=1,
        )
        supplemental = worker.create_supplemental_mask(
            ctx.img_rgb,
            accepted_supplemental,
            raw,
        )
        # Las letras blancas pequeñas suelen llevar antialiasing y contorno oscuro; dos píxeles
        # adicionales evitan que el inglés asome bajo la traducción.
        supplemental = cv2.dilate(
            supplemental,
            cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)),
            iterations=1,
        )
        combined = cv2.bitwise_or(raw, supplemental)
        if refined is not None:
            refined_binary = np.where(refined > 20, 255, 0).astype(np.uint8)
            combined = cv2.bitwise_or(combined, cv2.bitwise_and(refined_binary, gate))
        return combined

    original_merge = worker.merge_split_bubbles

    def guarded_merge_split_bubbles(regions: list[dict]) -> list[dict]:
        valid = [
            region
            for region in regions
            if _text_quality(
                str(region.get("original", "")),
                float(region.get("confidence", 0.0) or 0.0),
            )
        ]
        merged = original_merge(valid)
        for index, region in enumerate(merged):
            region["order"] = index
        return merged

    worker.TintaTranslator._run_ocr = guarded_run_ocr
    worker.TintaTranslator._run_mask_refinement = guarded_mask_refinement
    worker.merge_split_bubbles = guarded_merge_split_bubbles
