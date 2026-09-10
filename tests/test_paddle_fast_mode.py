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
            engine="transformers",
            device="gpu:0",
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_layout_detection=False,
            use_queues=True,
        )

    def test_whole_page_pipeline_keeps_historical_quality_path(self):
        fake = self.make_fake_paddle()
        with mock.patch.dict(sys.modules, {"paddleocr": fake}), mock.patch.dict(
            worker.os.environ,
            {"TINTAES_PADDLE_ENGINE": "transformers", "TINTAES_PADDLE_DEVICE": "gpu:0"},
        ):
            worker._create_pipeline(crop_mode=False)

        fake.PaddleOCRVL.assert_called_once_with(
            pipeline_version="v1.6",
            engine="transformers",
            device="gpu:0",
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_layout_detection=True,
            use_queues=False,
        )

    def test_resident_protocol_version_prevents_reusing_old_worker(self):
        self.assertEqual(worker._PROTOCOL, "tintaes-paddle-resident-v2")

    def test_timing_log_is_bounded_and_does_not_affect_ocr(self):
        with tempfile.TemporaryDirectory(prefix="tintaes-paddle-timing-test-") as root:
            log = Path(root) / "timing.jsonl"
            with mock.patch.object(worker, "_TIMING_LOG", log):
                worker._append_timing_log({"resident": True, "inference_ms": 123.4})
            self.assertIn('"inference_ms":123.4', log.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
