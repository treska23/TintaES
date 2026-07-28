from __future__ import annotations

import argparse
import asyncio
import json
import time
from pathlib import Path

import cv2
from PIL import Image

import tinta_worker as worker


class LazyTintaTranslator(worker.TintaTranslator):
    """Mantiene el protocolo de Tinta ES, pero carga cada modelo cuando se necesita."""

    async def _report_progress(self, state: str, finished: bool = False) -> None:
        await super()._report_progress(state, finished)


async def analyze(args: argparse.Namespace) -> int:
    started = time.perf_counter()
    input_path = Path(args.input).resolve()
    output_dir = Path(args.output).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    config_path = Path(args.config).resolve()

    worker.emit({
        "type": "progress",
        "state": "loading-config",
        "percent": 1,
        "message": "Comprobando la configuración del motor",
    })

    with config_path.open("r", encoding="utf-8") as handle:
        config = worker.Config(**json.load(handle))

    params = {
        "use_gpu": not args.cpu,
        "use_gpu_limited": False,
        "verbose": False,
        "ignore_errors": False,
        "kernel_size": config.kernel_size,
        "input": [str(input_path)],
        "prep_manual": True,
        # Cero significa conservar y precargar todos los modelos para siempre. Este
        # proceso solo analiza una página, así que la carga diferida evita un gran
        # parón inicial y reduce el pico de memoria de GPU.
        "models_ttl": 60,
        "batch_size": 1,
    }

    translator = LazyTintaTranslator(params)
    with Image.open(input_path) as source:
        image = source.convert("RGB")
        ctx = await translator.translate(image, config, skip_context_save=True)

    if ctx.mask is None or getattr(ctx, "img_inpainted", None) is None:
        raise RuntimeError("El motor no pudo generar la máscara o el fondo limpio.")

    worker.emit({
        "type": "progress",
        "state": "saving-results",
        "percent": 98,
        "message": "Guardando la máscara y el fondo reconstruido",
    })

    mask_path = output_dir / "mask.png"
    clean_path = output_dir / "clean.png"
    manifest_path = output_dir / "analysis.json"
    cv2.imwrite(str(mask_path), ctx.mask)
    cv2.imwrite(str(clean_path), cv2.cvtColor(ctx.img_inpainted, cv2.COLOR_RGB2BGR))
    regions = worker.merge_split_bubbles(
        [worker.region_payload(region, ctx.img_inpainted, index) for index, region in enumerate(ctx.text_regions)]
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
    worker.emit({
        "type": "complete",
        "percent": 100,
        "message": "Fondo reconstruido",
        "manifest": str(manifest_path),
    })
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Motor orgánico local de Tinta ES con carga diferida")
    subparsers = parser.add_subparsers(dest="command", required=True)
    analyze_parser = subparsers.add_parser("analyze")
    analyze_parser.add_argument("--input", required=True)
    analyze_parser.add_argument("--output", required=True)
    analyze_parser.add_argument("--config", required=True)
    analyze_parser.add_argument("--cpu", action="store_true")
    args = parser.parse_args()

    try:
        if args.command == "analyze":
            return asyncio.run(analyze(args))
        return 2
    except Exception as exception:
        worker.emit({"type": "error", "message": str(exception)})
        raise


if __name__ == "__main__":
    raise SystemExit(main())
