from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


def main() -> int:
    parser = argparse.ArgumentParser(description="Codificador de formatos raster de Tinta ES")
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--format", choices=["webp", "pdf"], required=True)
    parser.add_argument("--quality", type=int, default=95)
    args = parser.parse_args()

    input_path = Path(args.input).resolve()
    output_path = Path(args.output).resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(input_path) as source:
        if args.format == "pdf":
            image = source.convert("RGB")
            image.save(output_path, format="PDF", resolution=300)
        else:
            image = source.convert("RGBA" if source.mode in {"RGBA", "LA"} else "RGB")
            image.save(
                output_path,
                format="WEBP",
                quality=max(1, min(args.quality, 100)),
                method=4,
                lossless=False,
            )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
