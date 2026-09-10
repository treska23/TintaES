from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

_PROTOCOL = "tintaes-paddle-resident-v3"
_HOST = "127.0.0.1"
_TIMING_LOG = Path(tempfile.gettempdir()) / "tintaes-paddle-timing.jsonl"


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
    seen: set[str] = set()
    for block in _find_parsing_blocks(payload):
        label = str(block.get("block_label") or "").strip().lower()
        if any(token in label for token in ignored_labels):
            continue
        text = _normalise_text(block.get("block_content"))
        folded = text.casefold()
        if text and folded not in seen:
            seen.add(folded)
            parts.append(text)
    return " ".join(parts)


def _bounded_int(name: str, default: int, minimum: int, maximum: int) -> int:
    try:
        return max(minimum, min(maximum, int(os.environ.get(name, str(default)))))
    except ValueError:
        return default


def _max_new_tokens() -> int:
    # A comic crop is a speech balloon/narration box, not a whole document. 768 is
    # intentionally generous while preventing a bad crop from generating thousands
    # of repeated tokens (the generic document default can be around 4096).
    return _bounded_int("TINTAES_PADDLE_MAX_NEW_TOKENS", 768, 128, 2048)


def _page_parallelism() -> int:
    # One page normally contains several independent crops. Four server slots let
    # them be recognised concurrently without loading four copies of the model.
    return _bounded_int("TINTAES_PADDLE_PAGE_PARALLEL", 4, 1, 8)


def _crop_inputs(
    image_path: Path,
    manifest_path: Path,
    temp_dir: Path,
) -> tuple[list[str], list[list[float]]]:
    """Create lossless, index-stable crop files for Paddle."""
    from PIL import Image

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if not isinstance(manifest, list):
        raise ValueError("El manifiesto de regiones debe ser una lista JSON.")

    paths: list[str] = []
    boxes: list[list[float]] = []
    with Image.open(image_path) as opened:
        source = opened.convert("RGB")
        try:
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
                    resized = crop.resize(
                        (max(1, round(crop.width * scale)), max(1, round(crop.height * scale))),
                        Image.Resampling.LANCZOS,
                    )
                    crop.close()
                    crop = resized
                crop_path = temp_dir / f"region-{len(paths):04d}.png"
                try:
                    crop.save(crop_path, format="PNG", compress_level=0)
                finally:
                    crop.close()
                paths.append(str(crop_path))
                boxes.append(box)
        finally:
            source.close()
    return paths, boxes


def _configure_environment() -> None:
    model_home = os.environ.get("TINTAES_PADDLE_MODEL_HOME")
    if model_home:
        os.environ.setdefault("PADDLE_PDX_CACHE_HOME", model_home)
        os.environ.setdefault("HF_HOME", str(Path(model_home) / "huggingface"))
    os.environ.setdefault("PADDLE_PDX_MODEL_SOURCE", "huggingface")
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")


def _create_pipeline(
    *,
    crop_mode: bool = False,
    vlm_server_url: str | None = None,
    vlm_max_concurrency: int | None = None,
):
    from paddleocr import PaddleOCRVL

    kwargs: dict[str, object] = {
        "pipeline_version": "v1.6",
        "use_doc_orientation_classify": False,
        "use_doc_unwarping": False,
        "use_layout_detection": not crop_mode,
        "use_queues": crop_mode,
    }
    if vlm_server_url:
        # Paddle's supported production path: one shared llama.cpp model with
        # concurrent requests for the independent crops of THIS page.
        kwargs.update(
            vl_rec_backend="llama-cpp-server",
            vl_rec_server_url=vlm_server_url,
            vl_rec_max_concurrency=vlm_max_concurrency or 1,
        )
    else:
        kwargs.update(
            engine=os.environ.get("TINTAES_PADDLE_ENGINE", "transformers"),
            device=os.environ.get("TINTAES_PADDLE_DEVICE", "gpu:0"),
        )
    return PaddleOCRVL(**kwargs)


def _crop_predict_options() -> dict[str, object]:
    return {
        "prompt_label": "ocr",
        "temperature": 0.0,
        "max_new_tokens": _max_new_tokens(),
    }


def _predict_spots(
    pipeline: object,
    image_path: Path,
    manifest_path: Path | None,
    timings: dict[str, float] | None = None,
) -> list[dict[str, object]]:
    from PIL import Image

    with Image.open(image_path) as image:
        width, height = image.size

    spots: list[dict[str, object]] = []
    if manifest_path is not None:
        prep_started = time.perf_counter()
        with tempfile.TemporaryDirectory(prefix="tintaes-paddle-") as temp_name:
            inputs, boxes = _crop_inputs(image_path, manifest_path, Path(temp_name))
            if timings is not None:
                timings["crop_ms"] = (time.perf_counter() - prep_started) * 1000.0
                timings["crop_count"] = float(len(inputs))
            if not inputs:
                return spots

            infer_started = time.perf_counter()
            # Force materialisation so timing includes the complete page inference
            # even if a Paddle version returns a lazy iterable.
            results = list(pipeline.predict(inputs, **_crop_predict_options()))
            if timings is not None:
                inference_ms = (time.perf_counter() - infer_started) * 1000.0
                timings["inference_ms"] = inference_ms
                timings["per_crop_ms"] = inference_ms / max(1, len(inputs))

            used_indices: set[int] = set()
            for fallback_index, result in enumerate(results):
                payload = result.json
                input_path = _find_input_path(payload) or ""
                match = re.search(r"region-(\d+)\.png$", input_path.replace("\\", "/"))
                index = int(match.group(1)) if match else fallback_index
                if index >= len(boxes) or index in used_indices:
                    continue
                used_indices.add(index)
                text = _content_from_result(payload)
                if text:
                    spots.append({"text": text, "bbox": boxes[index]})
        return spots

    infer_started = time.perf_counter()
    results = list(pipeline.predict(str(image_path)))
    if timings is not None:
        timings["inference_ms"] = (time.perf_counter() - infer_started) * 1000.0
    for result in results:
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
            os.environ.get("TINTAES_PADDLE_DEVICE", "gpu:0"),
            _PROTOCOL,
        )
    )
    digest = int(hashlib.sha256(identity.encode("utf-8")).hexdigest()[:8], 16)
    return 30000 + digest % 10000


def _server_log_path(port: int) -> Path:
    return Path(tempfile.gettempdir()) / f"tintaes-paddle-resident-{port}.log"


def _llama_log_path(port: int) -> Path:
    return Path(tempfile.gettempdir()) / f"tintaes-paddle-llama-{port}.log"


def _append_timing_log(values: dict[str, object]) -> None:
    try:
        if _TIMING_LOG.exists() and _TIMING_LOG.stat().st_size > 512 * 1024:
            _TIMING_LOG.unlink(missing_ok=True)
        entry = {"ts": time.time(), **values}
        with _TIMING_LOG.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(entry, ensure_ascii=False, separators=(",", ":")) + "\n")
    except OSError:
        pass


def _model_root() -> Path | None:
    value = os.environ.get("TINTAES_PADDLE_MODEL_HOME")
    return Path(value).resolve() if value else None


def _llama_model_paths() -> tuple[Path, Path] | None:
    explicit_model = os.environ.get("TINTAES_PADDLE_LLAMA_MODEL")
    explicit_mmproj = os.environ.get("TINTAES_PADDLE_LLAMA_MMPROJ")
    if explicit_model and explicit_mmproj:
        model = Path(explicit_model).expanduser().resolve()
        mmproj = Path(explicit_mmproj).expanduser().resolve()
    else:
        model_root = _model_root()
        if model_root is None:
            return None
        llama_root = model_root / "llama"
        model = llama_root / "PaddleOCR-VL-1.6-GGUF.gguf"
        mmproj = llama_root / "PaddleOCR-VL-1.6-GGUF-mmproj.gguf"
    return (model, mmproj) if model.is_file() and mmproj.is_file() else None


def _find_llama_server() -> Path | None:
    explicit = os.environ.get("TINTAES_PADDLE_LLAMA_SERVER")
    if explicit:
        path = Path(explicit).expanduser()
        if path.is_file():
            return path.resolve()

    model_root = _model_root()
    if model_root is not None:
        path_file = model_root.parent / "llama-server.path"
        try:
            candidate = Path(path_file.read_text(encoding="utf-8").strip().strip('"'))
            if candidate.is_file():
                return candidate.resolve()
        except OSError:
            pass

    command = shutil.which("llama-server") or shutil.which("llama-server.exe")
    if command:
        return Path(command).resolve()

    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        if local:
            link = Path(local) / "Microsoft" / "WinGet" / "Links" / "llama-server.exe"
            if link.is_file():
                return link.resolve()
    return None


def _free_local_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind((_HOST, 0))
        return int(probe.getsockname()[1])


def _stop_process(process: subprocess.Popen[bytes] | None) -> None:
    if process is None or process.poll() is not None:
        return
    try:
        process.terminate()
        process.wait(timeout=5)
    except (OSError, subprocess.TimeoutExpired):
        try:
            process.kill()
            process.wait(timeout=5)
        except (OSError, subprocess.TimeoutExpired):
            pass


def _wait_for_llama(process: subprocess.Popen[bytes], base_url: str, timeout: float) -> bool:
    deadline = time.monotonic() + timeout
    health_url = base_url.rstrip("/") + "/health"
    while time.monotonic() < deadline:
        if process.poll() is not None:
            return False
        try:
            with urllib.request.urlopen(health_url, timeout=1.0) as response:
                if 200 <= response.status < 300:
                    return True
        except (OSError, urllib.error.URLError, urllib.error.HTTPError):
            pass
        time.sleep(0.25)
    return False


def _start_llama_server() -> tuple[subprocess.Popen[bytes], str, int] | None:
    disabled = os.environ.get("TINTAES_PADDLE_LLAMA", "1").strip().lower()
    if disabled in {"0", "false", "off"}:
        return None

    executable = _find_llama_server()
    models = _llama_model_paths()
    if executable is None or models is None:
        return None

    model, mmproj = models
    parallelism = _page_parallelism()
    port = _free_local_port()
    log_path = _llama_log_path(port)
    try:
        log_path.unlink(missing_ok=True)
    except OSError:
        pass
    log = log_path.open("ab")
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0
    try:
        process = subprocess.Popen(
            [
                str(executable),
                "-m", str(model),
                "--mmproj", str(mmproj),
                "--host", _HOST,
                "--port", str(port),
                "--temp", "0",
                "--parallel", str(parallelism),
                "-ngl", "99",
                "--no-webui",
            ],
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=subprocess.STDOUT,
            close_fds=True,
            creationflags=creationflags,
        )
    finally:
        log.close()

    base_url = f"http://{_HOST}:{port}"
    if not _wait_for_llama(process, base_url, timeout=180.0):
        _stop_process(process)
        return None
    return process, base_url + "/v1", parallelism


def _send_server_request(payload: dict[str, object], *, timeout: float) -> dict[str, object]:
    port = _server_port()
    with socket.create_connection((_HOST, port), timeout=min(timeout, 2.0)) as connection:
        connection.settimeout(timeout)
        stream = connection.makefile("rwb")
        stream.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n")
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
            raise RuntimeError(detail or f"El worker residente de PaddleOCR terminó con código {process.returncode}.")
        time.sleep(0.25)
    raise TimeoutError("PaddleOCR-VL no terminó de cargar el worker residente a tiempo.")


def _serve(port: int) -> int:
    _configure_environment()
    llama_process: subprocess.Popen[bytes] | None = None
    backend = "transformers"
    parallelism = 1
    load_started = time.perf_counter()

    acceleration = _start_llama_server()
    if acceleration is not None:
        llama_process, server_url, parallelism = acceleration
        try:
            pipeline = _create_pipeline(
                crop_mode=True,
                vlm_server_url=server_url,
                vlm_max_concurrency=parallelism,
            )
            backend = "llama.cpp"
        except Exception:
            _stop_process(llama_process)
            llama_process = None
            parallelism = 1
            pipeline = _create_pipeline(crop_mode=True)
    else:
        pipeline = _create_pipeline(crop_mode=True)
    model_load_ms = (time.perf_counter() - load_started) * 1000.0

    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
            server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                server.bind((_HOST, port))
            except OSError:
                return 0
            server.listen(8)
            server.settimeout(30.0)
            last_activity = time.monotonic()

            while True:
                try:
                    connection, _ = server.accept()
                except socket.timeout:
                    if time.monotonic() - last_activity > 5.0 * 60.0:
                        return 0
                    continue

                request: dict[str, object] = {}
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
                                "model_load_ms": round(model_load_ms, 1),
                                "backend": backend,
                                "parallelism": parallelism,
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
                            if not manifest_value:
                                raise ValueError("El worker residente rápido requiere regiones CTD.")
                            manifest_path = Path(str(manifest_value)).resolve()
                            if not image_path.is_file():
                                raise FileNotFoundError(f"No existe la imagen: {image_path}")
                            if not manifest_path.is_file():
                                raise FileNotFoundError(f"No existe el manifiesto de regiones: {manifest_path}")

                            request_started = time.perf_counter()
                            timings: dict[str, float] = {}
                            fallback_reason: str | None = None
                            try:
                                spots = _predict_spots(pipeline, image_path, manifest_path, timings)
                            except Exception as accelerated_error:
                                if backend != "llama.cpp":
                                    raise
                                # Acceleration is optional. If the local HTTP backend
                                # fails, retry THIS SAME page once through the proven
                                # Transformers path instead of losing the OCR result.
                                fallback_reason = f"{type(accelerated_error).__name__}: {accelerated_error}"
                                _stop_process(llama_process)
                                llama_process = None
                                backend = "transformers"
                                parallelism = 1
                                pipeline = _create_pipeline(crop_mode=True)
                                timings = {}
                                spots = _predict_spots(pipeline, image_path, manifest_path, timings)

                            total_ms = (time.perf_counter() - request_started) * 1000.0
                            response = {
                                "protocol": _PROTOCOL,
                                "ok": True,
                                "spots": spots,
                                "elapsed_ms": round(total_ms, 1),
                                "crop_ms": round(timings.get("crop_ms", 0.0), 1),
                                "inference_ms": round(timings.get("inference_ms", 0.0), 1),
                                "per_crop_ms": round(timings.get("per_crop_ms", 0.0), 1),
                                "crop_count": int(timings.get("crop_count", 0.0)),
                                "model_load_ms": round(model_load_ms, 1),
                                "backend": backend,
                                "parallelism": parallelism,
                                "max_new_tokens": _max_new_tokens(),
                                "fallback_reason": fallback_reason,
                                "pid": os.getpid(),
                            }
                        else:
                            raise ValueError(f"Comando no reconocido: {command}")
                    except Exception as exc:  # noqa: BLE001 - process boundary
                        response = {
                            "protocol": _PROTOCOL,
                            "ok": False,
                            "error": f"{type(exc).__name__}: {exc}",
                            "pid": os.getpid(),
                        }

                    stream.write(json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n")
                    stream.flush()
                    last_activity = time.monotonic()
                    if request.get("command") == "shutdown":
                        return 0
    finally:
        _stop_process(llama_process)


def _shutdown_server() -> int:
    try:
        response = _send_server_request({"command": "shutdown"}, timeout=5.0)
        return 0 if response.get("ok") is True else 1
    except (OSError, ValueError, json.JSONDecodeError):
        return 0


def _run_one_shot(image_path: Path, manifest_path: Path | None) -> int:
    _configure_environment()
    load_started = time.perf_counter()
    pipeline = _create_pipeline(crop_mode=manifest_path is not None)
    model_load_ms = (time.perf_counter() - load_started) * 1000.0
    timings: dict[str, float] = {}
    started = time.perf_counter()
    spots = _predict_spots(pipeline, image_path, manifest_path, timings)
    total_ms = (time.perf_counter() - started) * 1000.0
    _append_timing_log({
        "resident": False,
        "backend": "transformers",
        "parallelism": 1,
        "model_load_ms": round(model_load_ms, 1),
        "crop_ms": round(timings.get("crop_ms", 0.0), 1),
        "inference_ms": round(timings.get("inference_ms", 0.0), 1),
        "per_crop_ms": round(timings.get("per_crop_ms", 0.0), 1),
        "total_ms": round(total_ms, 1),
        "crop_count": int(timings.get("crop_count", 0.0)),
        "max_new_tokens": _max_new_tokens(),
    })
    print("TINTAES_RESULT=" + json.dumps(spots, ensure_ascii=False, separators=(",", ":")))
    return 0


def main() -> int:
    if len(sys.argv) == 3 and sys.argv[1] == "--server":
        try:
            return _serve(int(sys.argv[2]))
        except Exception as exc:  # noqa: BLE001
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
    if resident_value in {"0", "false", "off"} or manifest_path is None:
        return _run_one_shot(image_path, manifest_path)

    try:
        cold_start, startup_ms = _ensure_server()
        request_started = time.perf_counter()
        response = _send_server_request(
            {"command": "ocr", "image": str(image_path), "manifest": str(manifest_path)},
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
        timing = {
            "resident": True,
            "cold_start": cold_start,
            "startup_ms": round(startup_ms, 1),
            "model_load_ms": response.get("model_load_ms"),
            "request_ms": round(request_ms, 1),
            "crop_ms": response.get("crop_ms"),
            "inference_ms": response.get("inference_ms"),
            "per_crop_ms": response.get("per_crop_ms"),
            "total_ms": response.get("elapsed_ms"),
            "crop_count": response.get("crop_count"),
            "backend": response.get("backend"),
            "parallelism": response.get("parallelism"),
            "max_new_tokens": response.get("max_new_tokens"),
            "fallback_reason": response.get("fallback_reason"),
            "pid": response.get("pid"),
        }
        _append_timing_log(timing)
        print("TINTAES_TIMING=" + json.dumps(timing, separators=(",", ":")))
        print("TINTAES_RESULT=" + json.dumps(spots, ensure_ascii=False, separators=(",", ":")))
        return 0
    except (OSError, ValueError, json.JSONDecodeError, RuntimeError, TimeoutError) as exc:
        print(f"PaddleOCR residente no disponible; usando ejecución aislada: {exc}", file=sys.stderr)
        return _run_one_shot(image_path, manifest_path)


if __name__ == "__main__":
    raise SystemExit(main())
