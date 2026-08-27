from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import cv2
import numpy as np


def read_image(path: Path) -> np.ndarray | None:
    """OpenCV imread no admite de forma fiable rutas Unicode en Windows."""
    try:
        encoded = np.fromfile(path, dtype=np.uint8)
    except OSError:
        return None
    return cv2.imdecode(encoded, cv2.IMREAD_COLOR) if encoded.size else None


def write_image(path: Path, image: np.ndarray) -> None:
    extension = path.suffix or ".png"
    success, encoded = cv2.imencode(extension, image)
    if not success:
        raise RuntimeError(f"No se pudo codificar {path}.")
    encoded.tofile(path)


def find_candidates(image: np.ndarray) -> tuple[np.ndarray, list[dict]]:
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    local = cv2.GaussianBlur(gray, (0, 0), 13)
    bright = (
        (gray >= 205)
        & (local <= 188)
        & ((gray.astype(np.int16) - local.astype(np.int16)) >= 38)
    )
    bright_mask = np.where(bright, 255, 0).astype(np.uint8)
    bright_mask = cv2.morphologyEx(
        bright_mask,
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)),
    )
    bright_grouped = cv2.dilate(
        bright_mask,
        cv2.getStructuringElement(cv2.MORPH_RECT, (17, 7)),
        iterations=1,
    )

    # Los efectos grandes suelen ser negros sobre una explosión clara. Un desenfoque
    # mucho más ancho que el trazo permite separarlos del fondo incluso cuando el
    # interior de cada letra es grueso y uniforme.
    wide_local = cv2.GaussianBlur(gray, (0, 0), 75)
    dark = (
        (gray <= 82)
        & (wide_local >= 118)
        & ((wide_local.astype(np.int16) - gray.astype(np.int16)) >= 48)
    )
    dark_mask = np.where(dark, 255, 0).astype(np.uint8)
    dark_mask = cv2.morphologyEx(
        dark_mask,
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)),
    )
    horizontal_lines = cv2.morphologyEx(
        dark_mask,
        cv2.MORPH_OPEN,
        cv2.getStructuringElement(
            cv2.MORPH_RECT,
            (max(90, int(round(image.shape[1] * 0.20))), 3),
        ),
    )
    grouping_seed = cv2.bitwise_and(
        dark_mask,
        cv2.bitwise_not(
            cv2.dilate(
                horizontal_lines,
                cv2.getStructuringElement(cv2.MORPH_RECT, (3, 9)),
            )
        ),
    )
    horizontal = max(31, int(round(image.shape[1] * 0.060)))
    vertical = max(7, int(round(image.shape[0] * 0.007)))
    dark_grouped = cv2.dilate(
        grouping_seed,
        cv2.getStructuringElement(cv2.MORPH_RECT, (horizontal, vertical)),
        iterations=1,
    )

    candidates: list[dict] = []
    append_candidates(
        candidates,
        bright_mask,
        bright_grouped,
        "bright",
        image.shape[1],
        image.shape[0],
    )
    append_candidates(
        candidates,
        dark_mask,
        dark_grouped,
        "dark",
        image.shape[1],
        image.shape[0],
    )
    candidates = merge_dark_line_candidates(
        candidates,
        image.shape[1],
        image.shape[0],
    )
    for index, candidate in enumerate(candidates):
        candidate["id"] = index
    combined = cv2.bitwise_or(bright_mask, dark_mask)
    return combined, candidates


def append_candidates(
    candidates: list[dict],
    stroke_mask: np.ndarray,
    grouped_mask: np.ndarray,
    polarity: str,
    image_width: int,
    image_height: int,
) -> None:
    count, _, stats, _ = cv2.connectedComponentsWithStats(grouped_mask, 8)
    for label in range(1, count):
        x, y, width, height, _ = [int(value) for value in stats[label]]
        aspect = width / max(1, height)
        if polarity == "bright":
            valid_geometry = (
                45 <= width <= 190
                and 18 <= height <= 75
                and 1.45 <= aspect <= 4.8
            )
            minimum_ratio, maximum_ratio = 0.08, 0.34
        else:
            valid_geometry = (
                80 <= width <= int(image_width * 0.92)
                and max(24, int(image_height * 0.065)) <= height <= int(image_height * 0.34)
                and 0.70 <= aspect <= 8.5
            )
            minimum_ratio, maximum_ratio = 0.025, 0.46
        if not valid_geometry:
            continue

        inner = stroke_mask[y:y + height, x:x + width]
        ratio = float(np.mean(inner > 0))
        if not minimum_ratio <= ratio <= maximum_ratio:
            continue
        rectangle = (x, y, x + width, y + height)
        if any(
            rectangle_overlap_over_smaller(
                rectangle,
                (
                    int(candidate["x"]),
                    int(candidate["y"]),
                    int(candidate["x"] + candidate["width"]),
                    int(candidate["y"] + candidate["height"]),
                ),
            ) >= 0.65
            for candidate in candidates
        ):
            continue
        candidates.append({
            "id": len(candidates),
            "x": x,
            "y": y,
            "width": width,
            "height": height,
            "polarity": polarity,
        })


def merge_dark_line_candidates(
    candidates: list[dict],
    image_width: int,
    image_height: int,
) -> list[dict]:
    bright = [
        dict(candidate)
        for candidate in candidates
        if candidate.get("polarity") != "dark"
    ]
    pending = sorted(
        (
            dict(candidate)
            for candidate in candidates
            if candidate.get("polarity") == "dark"
        ),
        key=lambda candidate: (candidate["y"], candidate["x"]),
    )
    merged: list[dict] = []
    while pending:
        current = pending.pop(0)
        changed = True
        while changed:
            changed = False
            current_left = int(current["x"])
            current_top = int(current["y"])
            current_right = current_left + int(current["width"])
            current_bottom = current_top + int(current["height"])
            for index, other in enumerate(pending):
                other_left = int(other["x"])
                other_top = int(other["y"])
                other_right = other_left + int(other["width"])
                other_bottom = other_top + int(other["height"])
                overlap = max(
                    0,
                    min(current_bottom, other_bottom)
                    - max(current_top, other_top),
                )
                overlap_ratio = overlap / max(
                    1,
                    min(int(current["height"]), int(other["height"])),
                )
                horizontal_gap = max(
                    0,
                    max(current_left, other_left)
                    - min(current_right, other_right),
                )
                if (
                    overlap_ratio < 0.55
                    or horizontal_gap > int(image_width * 0.45)
                ):
                    continue

                left = min(current_left, other_left)
                top = min(current_top, other_top)
                right = max(current_right, other_right)
                bottom = max(current_bottom, other_bottom)
                if (right - left) / max(1, bottom - top) > 8.5:
                    continue
                current.update({
                    "x": left,
                    "y": top,
                    "width": right - left,
                    "height": bottom - top,
                    "polarity": "dark",
                })
                pending.pop(index)
                changed = True
                break
        merged.append(current)

    return bright + merged


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
    image = read_image(input_path)
    if image is None:
        raise RuntimeError(f"No se pudo abrir {input_path}.")

    stroke_mask, candidates = find_candidates(image)
    sheet = create_contact_sheet(image, candidates)
    sheet_path = output_dir / "bright-candidates.png"
    mask_path = output_dir / "text-candidate-strokes.png"
    manifest_path = output_dir / "bright-candidates.json"
    write_image(sheet_path, sheet)
    write_image(mask_path, stroke_mask)
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
