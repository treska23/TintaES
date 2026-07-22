import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFile } from "node:child_process";
import { analyzeComic, DEFAULT_BASE_URL, listModels } from "./ollama.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const publicDirectory = path.resolve(__dirname, "../public");
const MAX_BODY_BYTES = 36 * 1024 * 1024;
const MIME_TYPES = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
};

function sendJson(response, status, data) {
  response.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
  });
  response.end(JSON.stringify(data));
}

async function readJson(request) {
  const chunks = [];
  let total = 0;
  for await (const chunk of request) {
    total += chunk.length;
    if (total > MAX_BODY_BYTES) throw new Error("La imagen supera el limite de 27 MB.");
    chunks.push(chunk);
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    throw new Error("La solicitud no contiene JSON valido.");
  }
}

function chooseDefaultModel(models, preferredModel) {
  if (preferredModel && models.some((model) => model.name === preferredModel)) return preferredModel;
  return models.find((model) => /qwen3\.5/i.test(model.name))?.name
    ?? models.find((model) => /(vision|llava|qwen.*vl|gemma3)/i.test(model.name))?.name
    ?? models[0]?.name
    ?? null;
}

async function serveStatic(requestUrl, response) {
  const pathname = decodeURIComponent(new URL(requestUrl, "http://localhost").pathname);
  const relativePath = pathname === "/" ? "index.html" : pathname.replace(/^\/+/, "");
  const filePath = path.resolve(publicDirectory, relativePath);
  if (!filePath.startsWith(`${publicDirectory}${path.sep}`) && filePath !== path.join(publicDirectory, "index.html")) {
    sendJson(response, 403, { error: "Ruta no permitida." });
    return;
  }

  try {
    const info = await stat(filePath);
    if (!info.isFile()) throw new Error("not file");
    const content = await readFile(filePath);
    response.writeHead(200, {
      "Content-Type": MIME_TYPES[path.extname(filePath).toLowerCase()] ?? "application/octet-stream",
      "Cache-Control": "no-cache",
    });
    response.end(content);
  } catch {
    sendJson(response, 404, { error: "No encontrado." });
  }
}

export function createAppServer({
  ollamaBaseUrl = process.env.OLLAMA_BASE_URL || DEFAULT_BASE_URL,
  preferredModel = process.env.COMIC_MODEL || "qwen3.5:9b",
} = {}) {
  return createServer(async (request, response) => {
    try {
      const pathname = new URL(request.url, "http://localhost").pathname;

      if (request.method === "GET" && pathname === "/api/health") {
        try {
          const models = await listModels(ollamaBaseUrl);
          sendJson(response, 200, {
            ready: models.length > 0,
            models,
            defaultModel: chooseDefaultModel(models, preferredModel),
          });
        } catch (error) {
          sendJson(response, 503, { ready: false, models: [], defaultModel: null, error: error.message });
        }
        return;
      }

      if (request.method === "POST" && pathname === "/api/analyze") {
        const body = await readJson(request);
        const imageBase64 = typeof body.imageBase64 === "string" ? body.imageBase64 : "";
        const model = typeof body.model === "string" ? body.model.trim() : "";
        if (!imageBase64 || imageBase64.length < 20) {
          sendJson(response, 400, { error: "Falta una imagen valida." });
          return;
        }
        if (!model) {
          sendJson(response, 400, { error: "Selecciona un modelo local." });
          return;
        }
        const result = await analyzeComic({ imageBase64, model, baseUrl: ollamaBaseUrl });
        sendJson(response, 200, result);
        return;
      }

      if (request.method === "GET" || request.method === "HEAD") {
        await serveStatic(request.url, response);
        return;
      }

      sendJson(response, 405, { error: "Metodo no permitido." });
    } catch (error) {
      sendJson(response, 500, { error: error.message || "Error inesperado." });
    }
  });
}

function openBrowser(url) {
  if (process.platform === "win32") {
    execFile("cmd", ["/c", "start", "", url], { windowsHide: true });
  } else if (process.platform === "darwin") {
    execFile("open", [url]);
  } else {
    execFile("xdg-open", [url]);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const host = process.env.HOST || "127.0.0.1";
  const port = Number(process.env.PORT) || 4317;
  const server = createAppServer();
  server.listen(port, host, () => {
    const url = `http://${host}:${port}`;
    console.log(`Tinta ES esta listo en ${url}`);
    console.log("Pulsa Ctrl+C para cerrar.");
    if (process.argv.includes("--open")) openBrowser(url);
  });
}
