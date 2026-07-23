from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Exporta una página de TintaES a PSD editable")
    parser.add_argument("--background", required=True)
    parser.add_argument("--composite", required=True)
    parser.add_argument("--regions", required=True)
    parser.add_argument("--output", required=True)
    return parser.parse_args()


def run_node_exporter(args: argparse.Namespace) -> int:
    try:
        from nodejs_wheel import node, npm
    except Exception as exc:
        raise RuntimeError(
            "Falta el runtime Node local para el exportador PSD. "
            "TintaES debe instalar el paquete 'nodejs-wheel' en su entorno Python."
        ) from exc

    engine_dir = Path(__file__).resolve().parent
    tool_dir = engine_dir / "psd_node"
    exporter = tool_dir / "export_psd_ag.cjs"
    if not exporter.exists():
        raise FileNotFoundError(f"No se encuentra el exportador PSD: {exporter}")

    tool_dir.mkdir(parents=True, exist_ok=True)
    node_modules = tool_dir / "node_modules"
    ag_psd_module = node_modules / "ag-psd"
    pngjs_module = node_modules / "pngjs"

    if not ag_psd_module.exists() or not pngjs_module.exists():
        previous_cwd = Path.cwd()
        try:
            os.chdir(tool_dir)
            completed = npm(
                ["install", "ag-psd", "pngjs", "--no-audit", "--no-fund"],
                return_completed_process=True,
            )
        finally:
            os.chdir(previous_cwd)

        if completed.returncode != 0:
            stderr = (completed.stderr or "").strip()
            stdout = (completed.stdout or "").strip()
            detail = stderr or stdout or f"npm terminó con código {completed.returncode}"
            raise RuntimeError(f"No se pudo preparar ag-psd: {detail}")

    completed = node(
        [
            str(exporter),
            "--background",
            str(Path(args.background).resolve()),
            "--composite",
            str(Path(args.composite).resolve()),
            "--regions",
            str(Path(args.regions).resolve()),
            "--output",
            str(Path(args.output).resolve()),
        ],
        return_completed_process=True,
    )

    if completed.returncode != 0:
        stderr = (completed.stderr or "").strip()
        stdout = (completed.stdout or "").strip()
        detail = stderr or stdout or f"Node terminó con código {completed.returncode}"
        raise RuntimeError(detail)

    return 0


def main() -> None:
    args = parse_args()
    try:
        raise SystemExit(run_node_exporter(args))
    except SystemExit:
        raise
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(1) from exc


if __name__ == "__main__":
    main()
