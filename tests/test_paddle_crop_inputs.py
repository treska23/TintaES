"""CPU regressions for the images and region associations sent to PaddleOCR."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock

from PIL import Image


WORKER_PATH = Path(__file__).resolve().parents[1] / "engine" / "paddleocr" / "ocr_page.py"
SPEC = importlib.util.spec_from_file_location("paddle_ocr_page_under_test", WORKER_PATH)
worker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(worker)


def legacy_crop(source: Image.Image, box: list[float]) -> Image.Image:
    """Freeze the crop/resize behavior before the PNG and indexing optimization."""
    source = source.convert("RGB")
    width, height = source.size
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
    encoded = io.BytesIO()
    crop.save(encoded, format="PNG")
    encoded.seek(0)
    with Image.open(encoded) as decoded:
        return decoded.copy()


class PaddleCropInputTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="tintaes-crop-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.image_path = self.root / "página-original.png"
        self.manifest_path = self.root / "regions.json"
        self.crops = self.root / "crops"
        self.crops.mkdir()
        self.source = Image.frombytes("RGB", (1600, 1000), bytes(range(256)) * 18750)
        self.source.save(self.image_path)
        self.addCleanup(self.source.close)

    def write_manifest(self, manifest):
        self.manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    def run_main(self, with_manifest=True):
        arguments = [str(WORKER_PATH), str(self.image_path)]
        if with_manifest:
            arguments.append(str(self.manifest_path))
        output = io.StringIO()
        # Estas pruebas congelan los píxeles y la asociación del camino de inferencia;
        # no deben iniciar un proceso residente real ni cargar modelos externos.
        with mock.patch.dict(worker.os.environ, {"TINTAES_PADDLE_RESIDENT": "0"}), \
             mock.patch.object(sys, "argv", arguments), contextlib.redirect_stdout(output):
            self.assertEqual(worker.main(), 0)
        line = output.getvalue().strip()
        self.assertTrue(line.startswith("TINTAES_RESULT="), line)
        return json.loads(line.removeprefix("TINTAES_RESULT="))

    def test_png_crops_preserve_all_pixels_and_resize_boundaries(self):
        boxes = [
            [0, 0, 1000, 1000],
            [0, 0, 480, 500],
            [100, 50, 350, 250],
            [100, 50, 150, 90],
            [-30, -20, 1100, 1200],
            [800.125, 900.25, 700, 800],
        ]
        self.write_manifest([{"bbox": box} for box in boxes])
        paths, returned_boxes = worker._crop_inputs(self.image_path, self.manifest_path, self.crops)
        self.assertEqual(returned_boxes, boxes)
        for path, box in zip(paths, boxes):
            with self.subTest(box=box), Image.open(path) as actual:
                expected = legacy_crop(self.source, box)
                self.addCleanup(expected.close)
                self.assertEqual(actual.mode, expected.mode)
                self.assertEqual(actual.size, expected.size)
                self.assertEqual(actual.tobytes(), expected.tobytes())

    def test_grayscale_and_transparent_sources_keep_rgb_pixels(self):
        box = [100, 150, 400, 450]
        self.write_manifest([{"bbox": box}])
        for mode in ("L", "RGBA"):
            with self.subTest(mode=mode):
                source = self.source.convert(mode)
                self.addCleanup(source.close)
                if mode == "RGBA":
                    source.putalpha(80)
                source.save(self.image_path)
                paths, _ = worker._crop_inputs(self.image_path, self.manifest_path, self.crops)
                expected = legacy_crop(source, box)
                self.addCleanup(expected.close)
                with Image.open(paths[0]) as actual:
                    self.assertEqual(actual.mode, "RGB")
                    self.assertEqual(actual.size, expected.size)
                    self.assertEqual(actual.tobytes(), expected.tobytes())

    def test_omitted_entries_keep_filename_and_box_indices_aligned(self):
        boxes = [[0, 0, 80, 80], [100, 0, 180, 80], [200, 0, 280, 80]]
        self.write_manifest([
            None,
            {"bbox": boxes[0]},
            {"bbox": [10, 20, 30]},
            {"bbox": boxes[1]},
            "not a region",
            {"bbox": boxes[2]},
            {"bbox": "invalid"},
            {},
        ])
        paths, returned_boxes = worker._crop_inputs(self.image_path, self.manifest_path, self.crops)
        self.assertEqual(returned_boxes, boxes)
        self.assertEqual([Path(path).name for path in paths], [
            "region-0000.png", "region-0001.png", "region-0002.png",
        ])

        def predict(inputs):
            for index in (2, 0, 1):
                yield types.SimpleNamespace(json={
                    "input_path": str(inputs[index]).replace("/", "\\"),
                    "parsing_res_list": [{"block_label": "text", "block_content": f"Text {index}"}],
                })

        pipeline = mock.Mock()
        pipeline.predict.side_effect = predict
        with mock.patch.object(worker, "_create_pipeline", return_value=pipeline):
            spots = self.run_main()
        self.assertEqual(spots, [
            {"text": f"Text {index}", "bbox": boxes[index]} for index in (2, 0, 1)
        ])

    def test_results_without_paths_keep_order_fallback(self):
        boxes = [[0, 0, 80, 80], [100, 0, 180, 80]]
        self.write_manifest([None, *({"bbox": box} for box in boxes)])
        results = [types.SimpleNamespace(json={
            "parsing_res_list": [{"block_label": "text", "block_content": f"Text {index}"}],
        }) for index in range(2)]
        pipeline = mock.Mock()
        pipeline.predict.return_value = results
        with mock.patch.object(worker, "_create_pipeline", return_value=pipeline):
            spots = self.run_main()
        self.assertEqual(spots, [
            {"text": f"Text {index}", "bbox": box} for index, box in enumerate(boxes)
        ])

    def test_empty_or_fully_omitted_manifest_does_not_import_paddle(self):
        for manifest in ([], [None, {}, {"bbox": []}, "invalid"]):
            with self.subTest(manifest=manifest):
                self.write_manifest(manifest)
                with mock.patch.dict(sys.modules, {"paddleocr": None}):
                    self.assertEqual(self.run_main(), [])

    def test_manifest_must_still_be_a_list(self):
        self.write_manifest({"bbox": [0, 0, 100, 100]})
        with self.assertRaisesRegex(ValueError, "lista JSON"):
            worker._crop_inputs(self.image_path, self.manifest_path, self.crops)

    def test_whole_page_path_and_model_settings_are_preserved(self):
        fake_paddle = types.ModuleType("paddleocr")
        pipeline = mock.Mock()
        pipeline.predict.return_value = [types.SimpleNamespace(json={"parsing_res_list": [{
            "block_content": "Whole page text",
            "block_bbox": [160, 100, 320, 200],
        }]})]
        fake_paddle.PaddleOCRVL = mock.Mock(return_value=pipeline)
        with mock.patch.dict(sys.modules, {"paddleocr": fake_paddle}), mock.patch.dict(
            worker.os.environ, {"TINTAES_PADDLE_ENGINE": "transformers", "TINTAES_PADDLE_DEVICE": "gpu:0"},
        ):
            spots = self.run_main(with_manifest=False)
        fake_paddle.PaddleOCRVL.assert_called_once_with(
            pipeline_version="v1.6",
            engine="transformers",
            device="gpu:0",
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_layout_detection=True,
            use_queues=False,
        )
        pipeline.predict.assert_called_once_with(str(self.image_path))
        self.assertEqual(spots, [{"text": "Whole page text", "bbox": [100, 100, 200, 200]}])


if __name__ == "__main__":
    unittest.main()
