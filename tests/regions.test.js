import test from "node:test";
import assert from "node:assert/strict";
import { normalizeAnalysis } from "../src/regions.js";

test("normaliza y ordena las zonas devueltas por el modelo", () => {
  const result = normalizeAnalysis({
    source_language: "japonés",
    regions: [
      {
        order: 2,
        original: "  SAYONARA  ",
        translation: "  Adiós  ",
        type: "dialogue",
        confidence: 1.8,
        text_box: { x: 920, y: 950, width: 300, height: 400 },
        render_box: { left: 900, top: 900, right: 1200, bottom: 1200 },
        text_polygon: [],
        rotation: 270,
        style: {
          font_category: "comic",
          font_weight: 763,
          italic: false,
          uppercase: false,
          text_color: "#123",
          outline_color: "incorrecto",
          outline_width: 20,
          alignment: "center",
          background_color: "#fff",
          letter_spacing: 12,
          shadow: false,
        },
      },
      {
        order: 1,
        original: "Hola",
        translation: "Hola",
        type: "unknown",
        bbox: { x: 20, y: 30, width: 100, height: 80 },
      },
    ],
  });

  assert.equal(result.sourceLanguage, "japonés");
  assert.equal(result.regions.length, 2);
  assert.equal(result.regions[0].original, "Hola");
  assert.equal(result.regions[0].type, "other");

  const clamped = result.regions[1];
  assert.equal(clamped.translation, "Adiós");
  assert.deepEqual(clamped.textBox, { x: 920, y: 950, width: 80, height: 50 });
  assert.equal(clamped.renderBox.width, 100);
  assert.equal(clamped.confidence, 1);
  assert.equal(clamped.rotation, 180);
  assert.equal(clamped.style.fontWeight, 800);
  assert.equal(clamped.style.textColor, "#112233");
  assert.equal(clamped.style.outlineColor, null);
  assert.equal(clamped.style.outlineWidth, 8);
  assert.equal(clamped.style.backgroundColor, "#FFFFFF");
});

test("descarta regiones vacías y crea un polígono de respaldo", () => {
  const result = normalizeAnalysis({
    regions: [
      { original: "", translation: "" },
      {
        original: "BOOM",
        translation: "¡BUM!",
        text_box: { x: 100, y: 200, width: 300, height: 150 },
      },
    ],
  });

  assert.equal(result.regions.length, 1);
  assert.deepEqual(result.regions[0].polygon, [
    { x: 100, y: 200 },
    { x: 400, y: 200 },
    { x: 400, y: 350 },
    { x: 100, y: 350 },
  ]);
});
