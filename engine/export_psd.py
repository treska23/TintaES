from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image
import photoshopapi as psapi


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Exporta una página de TintaES a PSD editable")
    parser.add_argument("--background", required=True)
    parser.add_argument("--composite", required=True)
    parser.add_argument("--regions", required=True)
    parser.add_argument("--output", required=True)
    return parser.parse_args()


def parse_color(value: str | None) -> list[float]:
    raw = (value or "#111111").strip().lstrip("#")
    if len(raw) == 3:
        raw = "".join(ch * 2 for ch in raw)
    if len(raw) != 6:
        raw = "111111"
    try:
        red = int(raw[0:2], 16) / 255.0
        green = int(raw[2:4], 16) / 255.0
        blue = int(raw[4:6], 16) / 255.0
    except ValueError:
        red = green = blue = 17 / 255.0
    return [1.0, red, green, blue]


def postscript_font(style: dict[str, Any]) -> str:
    requested = (style.get("fontFamily") or "").strip().lower()
    mapping = {
        "arial": "ArialMT",
        "arial narrow": "ArialNarrow",
        "comic sans ms": "ComicSansMS",
        "georgia": "Georgia",
        "impact": "Impact",
        "consolas": "Consolas",
        "segoe print": "SegoePrint",
    }
    return mapping.get(requested, "ArialMT")


def get_font_size(region: dict[str, Any], page_height: int, box_height: float) -> float:
    manual_base = float(region.get("manualBaseFontSize") or 0)
    if manual_base > 0:
        base = manual_base
    else:
        style = region.get("style") or {}
        normalized = float(style.get("fontSize") or 0)
        if normalized > 0:
            base = normalized / 1000.0 * page_height
        else:
            text = str(region.get("translation") or region.get("original") or "")
            line_count = max(1, len(text.splitlines()))
            base = max(8.0, box_height / (line_count * 1.45))
    scale = float(region.get("manualFontScale") or 1.0)
    return max(4.0, min(500.0, base * scale))


def _read_u16_be(data: bytes, offset: int) -> int:
    if offset < 0 or offset + 2 > len(data):
        raise ValueError("PSD truncado al leer un entero de 16 bits")
    return int.from_bytes(data[offset : offset + 2], "big", signed=False)


def _read_u32_be(data: bytes, offset: int) -> int:
    if offset < 0 or offset + 4 > len(data):
        raise ValueError("PSD truncado al leer un entero de 32 bits")
    return int.from_bytes(data[offset : offset + 4], "big", signed=False)


def _locate_composite_image_section(data: bytes) -> tuple[int, int, int, int, int, int]:
    """Devuelve offset de Image Data y metadatos básicos del PSD v1.

    La estructura PSD es: cabecera fija de 26 bytes, Color Mode Data,
    Image Resources, Layer and Mask Information e Image Data. Las tres
    secciones intermedias empiezan por una longitud big-endian de 32 bits.
    """
    if len(data) < 26 or data[0:4] != b"8BPS":
        raise ValueError("El archivo generado no tiene una cabecera PSD válida")

    version = _read_u16_be(data, 4)
    if version != 1:
        raise ValueError(f"Solo se puede completar un PSD estándar v1; versión encontrada: {version}")

    channels = _read_u16_be(data, 12)
    height = _read_u32_be(data, 14)
    width = _read_u32_be(data, 18)
    depth = _read_u16_be(data, 22)
    color_mode = _read_u16_be(data, 24)

    offset = 26
    for section_name in ("Color Mode Data", "Image Resources", "Layer and Mask Information"):
        length = _read_u32_be(data, offset)
        offset += 4
        end = offset + length
        if end > len(data):
            raise ValueError(f"La sección {section_name} del PSD está truncada")
        offset = end

    return offset, channels, width, height, depth, color_mode


def inject_merged_composite(psd_path: Path, composite_path: Path) -> None:
    """Añade una imagen compuesta RAW válida sin tocar la información de capas.

    PhotoshopAPI prioriza archivos pequeños y sus PSD creados desde cero pueden
    no contener una imagen merged/composite utilizable por lectores de terceros.
    Conservamos intactas todas las capas y reemplazamos únicamente la última
    sección Image Data del PSD por una composición RGB/RGBA sin compresión.
    """
    data = psd_path.read_bytes()
    image_offset, channels, width, height, depth, color_mode = _locate_composite_image_section(data)

    if depth != 8:
        raise ValueError(f"La compatibilidad de imagen compuesta requiere 8 bits por canal; encontrados: {depth}")
    if color_mode != 3:
        raise ValueError(f"La compatibilidad de imagen compuesta requiere RGB; modo encontrado: {color_mode}")
    if channels < 3:
        raise ValueError(f"Un PSD RGB debe tener al menos 3 canales; encontrados: {channels}")

    with Image.open(composite_path) as source:
        composite = source.convert("RGBA" if channels >= 4 else "RGB")
        if composite.size != (width, height):
            raise ValueError(
                f"La composición mide {composite.width}x{composite.height}, "
                f"pero el PSD mide {width}x{height}"
            )
        pixels = np.asarray(composite, dtype=np.uint8)

    planes: list[bytes] = []
    planes.append(np.ascontiguousarray(pixels[:, :, 0]).tobytes())
    planes.append(np.ascontiguousarray(pixels[:, :, 1]).tobytes())
    planes.append(np.ascontiguousarray(pixels[:, :, 2]).tobytes())

    if channels >= 4:
        alpha = pixels[:, :, 3] if pixels.shape[2] >= 4 else np.full((height, width), 255, dtype=np.uint8)
        planes.append(np.ascontiguousarray(alpha).tobytes())

    # Los canales adicionales no son necesarios para TintaES, pero si la librería
    # hubiese creado alguno conservamos una sección de tamaño válido rellenándolo.
    while len(planes) < channels:
        planes.append(bytes(width * height))

    # Compression = 0 (RAW), seguido de todos los planos de canal en orden.
    image_data = b"\x00\x00" + b"".join(planes[:channels])
    psd_path.write_bytes(data[:image_offset] + image_data)


def main() -> None:
    args = parse_args()
    background_path = Path(args.background)
    composite_path = Path(args.composite)
    regions_path = Path(args.regions)
    output_path = Path(args.output)

    with Image.open(background_path) as source:
        image = source.convert("RGB")
        width, height = image.size
        rgb = np.asarray(image, dtype=np.uint8).transpose(2, 0, 1).copy()

    # utf-8-sig acepta JSON UTF-8 tanto con BOM como sin BOM. Esto hace al exportador
    # tolerante a archivos temporales creados por versiones anteriores de TintaES.
    with regions_path.open("r", encoding="utf-8-sig") as stream:
        regions: list[dict[str, Any]] = json.load(stream)

    color_mode = psapi.enum.ColorMode.rgb
    document = psapi.LayeredFile_8bit(color_mode, width, height)
    background_layer = psapi.ImageLayer_8bit(
        rgb,
        layer_name="Fondo limpio",
        width=width,
        height=height,
        color_mode=color_mode,
    )
    document.add_layer(background_layer)

    for index, region in enumerate(regions, start=1):
        if not region.get("isEnabled", True):
            continue
        text = str(region.get("translation") or region.get("original") or "").strip()
        if not text:
            continue

        style = region.get("style") or {}
        if style.get("uppercase"):
            text = text.upper()

        box = region.get("renderBox") or {}
        normalized_x = float(box.get("x") or 0) + float(region.get("textOffsetX") or 0)
        normalized_y = float(box.get("y") or 0) + float(region.get("textOffsetY") or 0)
        normalized_w = max(5.0, float(box.get("width") or 100))
        normalized_h = max(5.0, float(box.get("height") or 50))

        left = normalized_x / 1000.0 * width
        top = normalized_y / 1000.0 * height
        box_width = normalized_w / 1000.0 * width
        box_height = normalized_h / 1000.0 * height
        font_size = get_font_size(region, height, box_height)

        layer = psapi.TextLayer_8bit(
            layer_name=f"Texto {index:02d} - {region.get('type', 'dialogue')}",
            text=text,
            font=postscript_font(style),
            font_size=font_size,
            fill_color=parse_color(style.get("textColor")),
            position_x=left,
            position_y=top,
            box_width=box_width,
            box_height=box_height,
        )

        try:
            alignment = str(style.get("alignment") or "center").lower()
            justification = {
                "left": psapi.enum.Justification.Left,
                "right": psapi.enum.Justification.Right,
                "center": psapi.enum.Justification.Center,
            }.get(alignment, psapi.enum.Justification.Center)
            layer.paragraph_all().set_justification(justification)
        except Exception:
            pass

        try:
            if int(style.get("fontWeight") or 400) >= 650:
                layer.style_all().set_faux_bold(True)
            if style.get("italic"):
                layer.style_all().set_faux_italic(True)
        except Exception:
            pass

        document.add_layer(layer)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    document.write(os.fspath(output_path))
    inject_merged_composite(output_path, composite_path)


if __name__ == "__main__":
    main()
