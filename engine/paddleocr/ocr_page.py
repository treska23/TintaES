from __future__ import annotations

import hashlib
import json
import os
import re
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

_PROTOCOL = "tintaes-paddle-resident-v1"
_HOST = "127.0.0.1"


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
        for item in manifest:
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
            # El nombre debe usar el mismo índice compacto que boxes: las entradas
            # omitidas del manifiesto no pueden desplazar la asociación del OCR.
            crop_path = temp_dir / f"region-{len(paths):04d}.png"
            # Los recortes son temporales; una compresión menor ahorra CPU y
            # conserva exactamente los píxeles que recibe el modelo.
            crop.save(crop_path, format="PNG", compress_level=1)
            paths.append(str(crop_path))
            boxes.append(box)
    return paths, boxes


def _configure_environment() -> None:
    # Debe ejecutarse antes de importar PaddleOCR/PaddleX.
    model_home = os.environ.get("TINTAES_PADDLE_MODEL_HOME")
    if model_home:
        os.environ.setdefault("PADDLE_PDX_CACHE_HOME", model_home)
        os.environ.setdefault("HF_HOME", str(Path(model_home) / "huggingface"))
    os.environ.setdefault("PADDLE_PDX_MODEL_SOURCE", "huggingface")
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")


def _create_pipeline():
    from paddleocr import PaddleOCRVL

    # En este primer paso no se cambia ninguna opción de calidad. Únicamente se
    # conserva esta misma instancia para varias páginas consecutivas.
    return PaddleOCRVL(
        pipeline_version="v1.6",
        engine=os.environ.get("TINTAES_PADDLE_ENGINE", "transformers"),
        device=os.environ.get("TINTAES_PADDLE_DEVICE", "gpu:0"),
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_layout_detection=True,
        use_queues=False,
    )


def _predict_spots(
    pipeline: object,
    image_path: Path,
    manifest_path: Path | None,
) -> list[dict[str, object]]:
    from PIL import Image

    with Image.open(image_path) as image:
        width, height = image.size

    spots: list[dict[str, object]] = []
    if manifest_path is not None:
        with tempfile.TemporaryDirectory(prefix="tintaes-paddle-") as temp_name:
            inputs, boxes = _crop_inputs(image_path, manifest_path, Path(temp_name))
            if not inputs:
                return spots
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
        return spots

    for result in pipeline.predict(str(image_path)):
        for block in _find_parsing_blocks(result.json):
            text = _normalise_text(block.get("block_content"))
            box = _normalise_box(block.get("block_bbox"), width, height)
            if text and box:
                spots.append({"text": text, "bbox": box})
    return spots


def _server_port() -> int:
    identity = "|".join(
        (
            str(Path(__file__).resolve()),
            os.environ.get("TINTAES_PADDLE_MODEL_HOME", ""),
            os.environ.get("TINTAES_PADDLE_ENGINE", "transformers"),
            os.environ.get("TINTAES_PADDLE_DEVICE", "gpu:0"),
            _PROTOCOL,
        )
    )
    digest = int(hashlib.sha256(identity.encode("utf-8")).hexdigest()[:8], 16)
    # Fuera del rango efímero predeterminado de Windows para minimizar colisiones.
    return 30000 + digest % 10000


def _server_log_path(port: int) -> Path:
    return Path(tempfile.gettempdir()) / f"tintaes-paddle-resident-{port}.log"


def _send_server_request(
    payload: dict[str, object],
    *,
    timeout: float,
) -> dict[str, object]:
    port = _server_port()
    with socket.create_connection((_HOST, port), timeout=min(timeout, 2.0)) as connection:
        connection.settimeout(timeout)
        stream = connection.makefile("rwb")
        stream.write(
            json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            + b"\n"
        )
        stream.flush()
        line = stream.readline()
        if not line:
            raise ConnectionError("El worker residente de PaddleOCR cerró la conexión.")
        response = json.loads(line.decode("utf-8"))
        if not isinstance(response, dict) or response.get("protocol") != _PROTOCOL:
            raise ConnectionError("La respuesta del worker residente no pertenece a TintaES.")
        return response


def _ping_server(timeout: float = 0.5) -> bool:
    try:
        response = _send_server_request({"command": "ping"}, timeout=timeout)
        return response.get("ok") is True and response.get("status") == "ready"
    except (OSError, ValueError, json.JSONDecodeError):
        return False


def _read_server_log(port: int) -> str:
    path = _server_log_path(port)
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        return " ".join(lines[-8:])
    except OSError:
        return ""


def _spawn_server(port: int) -> subprocess.Popen[bytes]:
    log_path = _server_log_path(port)
    try:
        log_path.unlink(missing_ok=True)
    except OSError:
        pass

    log = log_path.open("ab")
    creationflags = 0
    if os.name == "nt":
        creationflags = (
            getattr(subprocess, "CREATE_NO_WINDOW", 0)
            | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
            | getattr(subprocess, "DETACHED_PROCESS", 0)
        )
    try:
        return subprocess.Popen(
            [sys.executable, str(Path(__file__).resolve()), "--server", str(port)],
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=subprocess.STDOUT,
            close_fds=True,
            creationflags=creationflags,
        )
    finally:
        log.close()


def _ensure_server() -> tuple[bool, float]:
    started_at = time.perf_counter()
    if _ping_server():
        return False, (time.perf_counter() - started_at) * 1000.0

    port = _server_port()
    process = _spawn_server(port)
    deadline = time.monotonic() + 14.0 * 60.0
    while time.monotonic() < deadline:
        if _ping_server(timeout=0.75):
            return True, (time.perf_counter() - started_at) * 1000.0
        if process.poll() is not None:
            detail = _read_server_log(port)
            raise RuntimeError(
                detail or f"El worker residente de PaddleOCR terminó con código {process.returncode}."
            )
        time.sleep(0.25)
    raise TimeoutError("PaddleOCR-VL no terminó de cargar el worker residente a tiempo.")


def _serve(port: int) -> int:
    _configure_environment()
    pipeline = _create_pipeline()

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            server.bind((_HOST, port))
        except OSError:
            # Otro proceso ganó la carrera de arranque; el cliente usará ese worker.
            return 0
        server.listen(8)
        server.settimeout(30.0)
        last_activity = time.monotonic()

        while True:
            try:
                connection, _ = server.accept()
            except socket.timeout:
                # Si TintaES se cierra a mitad del OCR, no dejamos el VLM ocupando
                # la GPU indefinidamente.
                if time.monotonic() - last_activity > 5.0 * 60.0:
                    return 0
                continue

            with connection:
                connection.settimeout(14.0 * 60.0)
                stream = connection.makefile("rwb")
                try:
                    line = stream.readline()
                    request = json.loads(line.decode("utf-8")) if line else {}
                    if not isinstance(request, dict):
                        raise ValueError("La petición del worker debe ser un objeto JSON.")
                    command = request.get("command")
                    if command == "ping":
                        response: dict[str, object] = {
                            "protocol": _PROTOCOL,
                            "ok": True,
                            "status": "ready",
                            "pid": os.getpid(),
                        }
                    elif command == "shutdown":
                        response = {
                            "protocol": _PROTOCOL,
                            "ok": True,
                            "status": "stopping",
                            "pid": os.getpid(),
                        }
                    elif command == "ocr":
                        image_path = Path(str(request.get("image") or "")).resolve()
                        manifest_value = request.get("manifest")
                        manifest_path = (
                            Path(str(manifest_value)).resolve() if manifest_value else None
                        )
                        if not image_path.is_file():
                            raise FileNotFoundError(f"No existe la imagen: {image_path}")
                        if manifest_path is not None and not manifest_path.is_file():
                            raise FileNotFoundError(
                                f"No existe el manifiesto de regiones: {manifest_path}"
                            )
                        request_started = time.perf_counter()
                        spots = _predict_spots(pipeline, image_path, manifest_path)
                        response = {
                            "protocol": _PROTOCOL,
                            "ok": True,
                            "spots": spots,
                            "elapsed_ms": round(
                                (time.perf_counter() - request_started) * 1000.0, 1
                            ),
                            "pid": os.getpid(),
                        }
                    else:
                        raise ValueError(f"Comando no reconocido: {command}")
                except Exception as exc:  # noqa: BLE001 - frontera del proceso local
                    response = {
                        "protocol": _PROTOCOL,
                        "ok": False,
                        "error": f"{type(exc).__name__}: {exc}",
                        "pid": os.getpid(),
                    }

                stream.write(
                    json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode(
                        "utf-8"
                    )
                    + b"\n"
                )
                stream.flush()
                last_activity = time.monotonic()
                if isinstance(request, dict) and request.get("command") == "shutdown":
                    return 0


def _shutdown_server() -> int:
    try:
        response = _send_server_request({"command": "shutdown"}, timeout=5.0)
        return 0 if response.get("ok") is True else 1
    except (OSError, ValueError, json.JSONDecodeError):
        # No tener worker vivo también significa que la VRAM ya está liberada.
        return 0


def _run_one_shot(image_path: Path, manifest_path: Path | None) -> int:
    _configure_environment()
    pipeline = _create_pipeline()
    spots = _predict_spots(pipeline, image_path, manifest_path)
    print(
        "TINTAES_RESULT="
        + json.dumps(spots, ensure_ascii=False, separators=(",", ":"))
    )
    return 0


def main() -> int:
    if len(sys.argv) == 3 and sys.argv[1] == "--server":
        try:
            return _serve(int(sys.argv[2]))
        except Exception as exc:  # noqa: BLE001 - diagnóstico del proceso servidor
            print(f"PaddleOCR resident server: {type(exc).__name__}: {exc}", file=sys.stderr)
            return 1

    if len(sys.argv) == 2 and sys.argv[1] == "--shutdown":
        return _shutdown_server()

    if len(sys.argv) not in (2, 3):
        print("Uso: ocr_page.py <imagen> [regiones.json]", file=sys.stderr)
        return 2

    image_path = Path(sys.argv[1]).resolve()
    if not image_path.is_file():
        print(f"No existe la imagen: {image_path}", file=sys.stderr)
        return 2
    manifest_path = Path(sys.argv[2]).resolve() if len(sys.argv) == 3 else None
    if manifest_path is not None and not manifest_path.is_file():
        print(f"No existe el manifiesto de regiones: {manifest_path}", file=sys.stderr)
        return 2

    resident_value = os.environ.get("TINTAES_PADDLE_RESIDENT", "1").strip().lower()
    if resident_value in {"0", "false", "off"}:
        return _run_one_shot(image_path, manifest_path)

    try:
        cold_start, startup_ms = _ensure_server()
        request_started = time.perf_counter()
        response = _send_server_request(
            {
                "command": "ocr",
                "image": str(image_path),
                "manifest": str(manifest_path) if manifest_path is not None else None,
            },
            timeout=14.0 * 60.0,
        )
        request_ms = (time.perf_counter() - request_started) * 1000.0
        if response.get("ok") is not True:
            print(str(response.get("error") or "PaddleOCR residente falló."), file=sys.stderr)
            return 1
        spots = response.get("spots")
        if not isinstance(spots, list):
            print("PaddleOCR residente devolvió un resultado inválido.", file=sys.stderr)
            return 1
        # Línea diagnóstica que el host puede conservar sin alterar el protocolo actual.
        print(
            "TINTAES_TIMING="
            + json.dumps(
                {
                    "cold_start": cold_start,
                    "startup_ms": round(startup_ms, 1),
                    "request_ms": round(request_ms, 1),
                    "inference_ms": response.get("elapsed_ms"),
                    "pid": response.get("pid"),
                },
                separators=(",", ":"),
            )
        )
        print(
            "TINTAES_RESULT="
            + json.dumps(spots, ensure_ascii=False, separators=(",", ":"))
        )
        return 0
    except (OSError, ValueError, json.JSONDecodeError, RuntimeError, TimeoutError) as exc:
        # Si ni siquiera se pudo establecer el worker residente, conservamos el
        # camino anterior de una sola ejecución para no convertir una optimización
        # de rendimiento en una regresión funcional.
        print(
            f"PaddleOCR residente no disponible; usando ejecución aislada: {exc}",
            file=sys.stderr,
        )
        return _run_one_shot(image_path, manifest_path)


if __name__ == "__main__":
    raise SystemExit(main())
