import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { once } from "node:events";
import { createAppServer } from "../src/server.js";

async function listen(server) {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return `http://127.0.0.1:${server.address().port}`;
}

function fakeOllama() {
  return createServer(async (request, response) => {
    response.setHeader("Content-Type", "application/json");
    if (request.url === "/api/tags") {
      response.end(JSON.stringify({
        models: [
          { name: "qwen3:8b", size: 10, modified_at: "2026-01-01" },
          { name: "qwen3.5:9b", size: 20, modified_at: "2026-02-01" },
        ],
      }));
      return;
    }
    if (request.url === "/api/chat") {
      for await (const _chunk of request) { /* consume body */ }
      response.end(JSON.stringify({
        total_duration: 1_500_000_000,
        message: {
          content: JSON.stringify({
            source_language: "inglés",
            regions: [{
              order: 1,
              original: "Hello!",
              translation: "¡Hola!",
              type: "dialogue",
              confidence: 0.98,
              text_box: { x: 100, y: 100, width: 200, height: 80 },
              render_box: { x: 80, y: 70, width: 250, height: 140 },
              text_polygon: [
                { x: 100, y: 100 }, { x: 300, y: 100 },
                { x: 300, y: 180 }, { x: 100, y: 180 },
              ],
              rotation: 0,
              vertical: false,
              style: {
                font_category: "comic",
                font_weight: 700,
                italic: false,
                uppercase: false,
                text_color: "#111111",
                outline_color: null,
                outline_width: 0,
                alignment: "center",
                background_color: "#FFFFFF",
                letter_spacing: 0,
                shadow: false,
              },
            }],
          }),
        },
      }));
      return;
    }
    response.statusCode = 404;
    response.end(JSON.stringify({ error: "not found" }));
  });
}

test("sirve la aplicación y el estado de Ollama", async (t) => {
  const ollama = fakeOllama();
  const ollamaUrl = await listen(ollama);
  t.after(() => ollama.close());
  const app = createAppServer({ ollamaBaseUrl: ollamaUrl, preferredModel: "qwen3.5:9b" });
  const appUrl = await listen(app);
  t.after(() => app.close());

  const page = await fetch(`${appUrl}/`);
  assert.equal(page.status, 200);
  assert.match(await page.text(), /TINTA/);

  const health = await fetch(`${appUrl}/api/health`).then((response) => response.json());
  assert.equal(health.ready, true);
  assert.equal(health.defaultModel, "qwen3.5:9b");
  assert.equal(health.models.length, 2);
});

test("envía la imagen a Ollama y normaliza el análisis", async (t) => {
  const ollama = fakeOllama();
  const ollamaUrl = await listen(ollama);
  t.after(() => ollama.close());
  const app = createAppServer({ ollamaBaseUrl: ollamaUrl });
  const appUrl = await listen(app);
  t.after(() => app.close());

  const response = await fetch(`${appUrl}/api/analyze`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      model: "qwen3.5:9b",
      imageBase64: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAAB",
    }),
  });
  const result = await response.json();

  assert.equal(response.status, 200);
  assert.equal(result.sourceLanguage, "inglés");
  assert.equal(result.model, "qwen3.5:9b");
  assert.equal(result.durationMs, 1500);
  assert.equal(result.regions[0].translation, "¡Hola!");
});
