import { readFile } from "node:fs/promises";
import path from "node:path";
import { analyzeComic } from "../src/ollama.js";

const imagePath = process.argv[2];
if (!imagePath) {
  console.error("Uso: node scripts/smoke-ollama.mjs <imagen>");
  process.exit(2);
}

const model = process.env.COMIC_MODEL || "qwen3.5:9b";
const bytes = await readFile(path.resolve(imagePath));
const result = await analyzeComic({
  imageBase64: bytes.toString("base64"),
  model,
});

console.log(JSON.stringify({
  model: result.model,
  sourceLanguage: result.sourceLanguage,
  durationMs: result.durationMs,
  regions: result.regions.map((region) => ({
    original: region.original,
    translation: region.translation,
    type: region.type,
    confidence: region.confidence,
  })),
}, null, 2));
