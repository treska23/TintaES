from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import cv2
import numpy as np


def find_candidates(image: np.ndarray) -> tuple[np.ndarray, list[dict]]:
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    local = cv2.GaussianBlur(gray, (0, 0), 13)
    bright = (
        (gray >= 205)
        & (local <= 188)
        & ((gray.astype(np.int16) - local.astype(np.int16)) >= 38)
    )
    mask = np.where(bright, 255, 0).astype(np.uint8)
    mask = cv2.morphologyEx(
        mask,
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)),
    )
    grouped = cv2.dilate(
        mask,
        cv2.getStructuringElement(cv2.MORPH_RECT, (17, 7)),
        iterations=1,
    )
    count, _, stats, _ = cv2.connectedComponentsWithStats(grouped, 8)
    candidates: list[dict] = []
    for label in range(1, count):
        x, y, width, height, _ = [int(value) for value in stats[label]]
        if not (
            45 <= width <= 190
            and 18 <= height <= 75
            and 1.45 <= width / max(1, height) <= 4.8
        ):
            continue

        inner = mask[y:y + height, x:x + width]
        ratio = float(np.mean(inner > 0))
        if not 0.08 <= ratio <= 0.34:
            continue
        candidates.append({
            "id": len(candidates),
            "x": x,
            "y": y,
            "width": width,
            "height": height,
        })
    return mask, candidates


def create_contact_sheet(image: np.ndarray, candidates: list[dict]) -> np.ndarray:
    cell_width = 260
    cell_height = 130
    columns = max(1, int(round(math.sqrt(max(1, len(candidates)) / 2))))
    rows = max(1, math.ceil(len(candidates) / columns))
    sheet = np.full((rows * cell_height, columns * cell_width, 3), 255, dtype=np.uint8)

    for position, candidate in enumerate(candidates):
        column = position % columns
        row = position // columns
        x = candidate["x"]
        y = candidate["y"]
        width = candidate["width"]
        height = candidate["height"]
        cv2.putText(
            sheet,
            f'ID {candidate["id"]}',
            (column * cell_width + 8, row * cell_height + 22),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.55,
            (0, 0, 0),
            1,
            cv2.LINE_AA,
        )

        crop = image[
            max(0, y - 5):min(image.shape[0], y + height + 5),
            max(0, x - 5):min(image.shape[1], x + width + 5),
        ]
        scale = min(205 / max(1, crop.shape[1]), 76 / max(1, crop.shape[0]))
        resized = cv2.resize(
            crop,
            (
                max(1, int(round(crop.shape[1] * scale))),
                max(1, int(round(crop.shape[0] * scale))),
            ),
            interpolation=cv2.INTER_CUBIC,
        )
        paste_x = column * cell_width + (cell_width - resized.shape[1]) // 2
        paste_y = (
            row * cell_height
            + 32
            + (cell_height - 32 - resized.shape[0]) // 2
        )
        sheet[
            paste_y:paste_y + resized.shape[0],
            paste_x:paste_x + resized.shape[1],
        ] = resized
    return sheet


def main() -> int:
    parser = argparse.ArgumentParser(description="Detecta posibles restos claros de rotulación.")
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    input_path = Path(args.input).resolve()
    output_dir = Path(args.output).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    image = cv2.imread(str(input_path))
    if image is None:
        raise RuntimeError(f"No se pudo abrir {input_path}.")

    _, candidates = find_candidates(image)
    sheet = create_contact_sheet(image, candidates)
    sheet_path = output_dir / "bright-candidates.png"
    manifest_path = output_dir / "bright-candidates.json"
    cv2.imwrite(str(sheet_path), sheet)
    manifest_path.write_text(
        json.dumps({
            "width": int(image.shape[1]),
            "height": int(image.shape[0]),
            "sheet": str(sheet_path),
            "candidates": candidates,
        }, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(str(manifest_path), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
