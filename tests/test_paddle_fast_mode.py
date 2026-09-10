"""Regression tests for the fast PaddleOCR-VL crop pipeline."""

from __future__ import annotations

import importlib.util
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock


WORKER_PATH = Path(__file__).resolve().parents[1] / "engine" / "paddleocr" / "ocr_page.py"
SPEC = importlib.util.spec_from_file_location("paddle_ocr_fast_mode_under_test", WORKER_PATH)
worker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(worker)


class PaddleFastModeTests(unittest.TestCase):
    def make_fake_paddle(self):
        fake = types.ModuleType("paddleocr")
        fake.PaddleOCRVL = mock.Mock(return_value=mock.Mock())
        return fake

    def test_ctd_crop_pipeline_skips_layout_and_uses_internal_queues(self):
        fake = self.make_fake_paddle()
        with mock.patch.dict(sys.modules, {"paddleocr": fake}), mock.patch.dict(
            worker.os.environ,
            {"TINTAES_PADDLE_ENGINE": "transformers", "TINTAES_PADDLE_DEVICE": "gpu:0"},
        ):
            worker._create_pipeline(crop_mode=True)

        fake.PaddleOCRVL.assert_called_once_with(
            pipeline_version="v1.6",
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_layout_detection=False,
            use_queues=True,
            engine="transformers",
            device="gpu:0",
        )

    def test_llama_cpp_pipeline_uses_concurrent_requests_for_one_page(self):
        fake = self.make_fake_paddle()
        with mock.patch.dict(sys.modules, {"paddleocr": fake}):
            worker._create_pipeline(
                crop_mode=True,
                vlm_server_url="http://127.0.0.1:8123/v1",
                vlm_max_concurrency=8,
            )

        fake.PaddleOCRVL.assert_called_once_with(
            pipeline_version="v1.6",
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_layout_detection=False,
            use_queues=True,
            vl_rec_backend="llama-cpp-server",
            vl_rec_server_url="http://127.0.0.1:8123/v1",
            vl_rec_max_concurrency=8,
        )

    def test_crop_generation_is_bounded_without_reducing_image_quality(self):
        with mock.patch.dict(worker.os.environ, {}, clear=False):
            worker.os.environ.pop("TINTAES_PADDLE_MAX_NEW_TOKENS", None)
            options = worker._crop_predict_options()
        self.assertEqual(options, {
            "prompt_label": "ocr",
            "temperature": 0.0,
            "max_new_tokens": 768,
        })

        with mock.patch.dict(worker.os.environ, {"TINTAES_PADDLE_MAX_NEW_TOKENS": "1200"}):
            self.assertEqual(worker._crop_predict_options()["max_new_tokens"], 1200)

    def test_page_parallelism_override_is_bounded_but_allows_more_balloon_workers(self):
        with mock.patch.dict(worker.os.environ, {"TINTAES_PADDLE_PAGE_PARALLEL": "99"}):
            self.assertEqual(worker._page_parallelism(), 16)
        with mock.patch.dict(worker.os.environ, {"TINTAES_PADDLE_PAGE_PARALLEL": "0"}):
            self.assertEqual(worker._page_parallelism(), 1)

    def test_page_parallelism_uses_available_gpu_memory_when_not_overridden(self):
        with mock.patch.dict(worker.os.environ, {}, clear=False):
            worker.os.environ.pop("TINTAES_PADDLE_PAGE_PARALLEL", None)
            for free_mib, expected in (
                (20_000, 16),
                (14_000, 12),
                (9_000, 10),
                (7_000, 8),
                (5_500, 6),
                (3_500, 4),
            ):
                with self.subTest(free_mib=free_mib), mock.patch.object(
                    worker, "_free_gpu_memory_mib", return_value=free_mib
                ):
                    self.assertEqual(worker._page_parallelism(), expected)

            with mock.patch.object(worker, "_free_gpu_memory_mib", return_value=None):
                self.assertEqual(worker._page_parallelism(), 8)

    def test_whole_page_pipeline_keeps_historical_quality_path(self):
        fake = self.make_fake_paddle()
        with mock.patch.dict(sys.modules, {"paddleocr": fake}), mock.patch.dict(
            worker.os.environ,
            {"TINTAES_PADDLE_ENGINE": "transformers", "TINTAES_PADDLE_DEVICE": "gpu:0"},
        ):
            worker._create_pipeline(crop_mode=False)

        fake.PaddleOCRVL.assert_called_once_with(
            pipeline_version="v1.6",
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_layout_detection=True,
            use_queues=False,
            engine="transformers",
            device="gpu:0",
        )

    def test_resident_protocol_version_prevents_reusing_old_worker(self):
        self.assertEqual(worker._PROTOCOL, "tintaes-paddle-resident-v4")

    def test_timing_log_is_bounded_and_does_not_affect_ocr(self):
        with tempfile.TemporaryDirectory(prefix="tintaes-paddle-timing-test-") as root:
            log = Path(root) / "timing.jsonl"
            with mock.patch.object(worker, "_TIMING_LOG", log):
                worker._append_timing_log({
                    "resident": True,
                    "backend": "llama.cpp",
                    "parallelism": 8,
                    "inference_ms": 123.4,
                })
            value = log.read_text(encoding="utf-8")
            self.assertIn('"backend":"llama.cpp"', value)
            self.assertIn('"inference_ms":123.4', value)


if __name__ == "__main__":
    unittest.main()
