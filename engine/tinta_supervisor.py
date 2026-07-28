from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
import time
from collections import deque
from pathlib import Path


HEARTBEAT_SECONDS = 15
MAX_SILENCE_SECONDS = 12 * 60
MAX_RUNTIME_SECONDS = 30 * 60

_emit_lock = threading.Lock()


def emit(payload: dict) -> None:
    line = json.dumps(payload, ensure_ascii=False)
    with _emit_lock:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()


def forward_stderr(line: str) -> None:
    with _emit_lock:
        sys.stderr.write(line)
        sys.stderr.flush()


def format_elapsed(seconds: int) -> str:
    minutes, remaining = divmod(max(0, seconds), 60)
    if minutes:
        return f"{minutes} min {remaining:02d} s"
    return f"{remaining} s"


def parse_message(line: str) -> dict | None:
    start = line.find("{")
    if start < 0:
        return None
    try:
        value = json.loads(line[start:])
    except json.JSONDecodeError:
        return None
    return value if isinstance(value, dict) else None


def terminate_tree(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return
    try:
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        else:
            process.kill()
    except OSError:
        pass


def main() -> int:
    engine_dir = Path(__file__).resolve().parent
    child_script = engine_dir / "tinta_worker_lazy.py"
    if not child_script.exists():
        emit({"type": "error", "message": f"No se encuentra {child_script.name}."})
        return 2

    state_lock = threading.Lock()
    state = {
        "percent": 1,
        "message": "Cargando las librerías del motor local",
        "last_child_output": time.monotonic(),
    }
    started = time.monotonic()
    stop_event = threading.Event()
    stderr_tail: deque[str] = deque(maxlen=80)

    emit({
        "type": "progress",
        "state": "starting-supervisor",
        "percent": 1,
        "message": state["message"],
    })

    command = [sys.executable, str(child_script), *sys.argv[1:]]
    process = subprocess.Popen(
        command,
        cwd=str(engine_dir.parent),
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )

    def read_stdout() -> None:
        assert process.stdout is not None
        for line in process.stdout:
            with state_lock:
                state["last_child_output"] = time.monotonic()
                message = parse_message(line)
                if message and message.get("type") == "progress":
                    percent = message.get("percent")
                    text = message.get("message")
                    if isinstance(percent, int) and percent > 0:
                        state["percent"] = min(100, percent)
                    if isinstance(text, str) and text.strip():
                        state["message"] = text.strip()
            with _emit_lock:
                sys.stdout.write(line)
                sys.stdout.flush()

    def read_stderr() -> None:
        assert process.stderr is not None
        for line in process.stderr:
            stderr_tail.append(line.rstrip())
            with state_lock:
                state["last_child_output"] = time.monotonic()
            forward_stderr(line)

    def heartbeat() -> None:
        while not stop_event.wait(HEARTBEAT_SECONDS):
            now = time.monotonic()
            with state_lock:
                percent = int(state["percent"])
                message = str(state["message"])
            emit({
                "type": "progress",
                "state": "heartbeat",
                "percent": percent,
                "message": f"{message} · {format_elapsed(int(now - started))}",
            })

    stdout_thread = threading.Thread(target=read_stdout, name="tinta-stdout", daemon=True)
    stderr_thread = threading.Thread(target=read_stderr, name="tinta-stderr", daemon=True)
    heartbeat_thread = threading.Thread(target=heartbeat, name="tinta-heartbeat", daemon=True)
    stdout_thread.start()
    stderr_thread.start()
    heartbeat_thread.start()

    timed_out_message: str | None = None
    try:
        while process.poll() is None:
            time.sleep(1)
            now = time.monotonic()
            with state_lock:
                last_output = float(state["last_child_output"])
                current_message = str(state["message"])

            if now - started > MAX_RUNTIME_SECONDS:
                timed_out_message = (
                    "El análisis superó 30 minutos y se detuvo para evitar que la aplicación quedara bloqueada."
                )
                break
            if now - last_output > MAX_SILENCE_SECONDS:
                timed_out_message = (
                    f"El motor no produjo ninguna actividad durante 12 minutos mientras estaba en: {current_message}."
                )
                break

        if timed_out_message:
            terminate_tree(process)
            detail = "\n".join(stderr_tail).strip()
            if detail:
                timed_out_message += f" Último detalle del motor: {detail[-1200:]}"
            emit({"type": "error", "message": timed_out_message})
            return 124

        return_code = process.wait()
        return return_code
    finally:
        stop_event.set()
        if process.poll() is None:
            terminate_tree(process)
        stdout_thread.join(timeout=3)
        stderr_thread.join(timeout=3)
        heartbeat_thread.join(timeout=1)


if __name__ == "__main__":
    raise SystemExit(main())
