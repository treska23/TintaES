import { analysisSchema, normalizeAnalysis } from "./regions.js";

const DEFAULT_BASE_URL = "http://127.0.0.1:11434";

function promptFor(targetLanguage) {
  return `Analiza esta pagina de comic con precision editorial. Detecta TODO el texto visible que forme parte de la obra y traduce cada fragmento a ${targetLanguage}.

Reglas obligatorias:
- Devuelve una region por bocadillo, cartucho, pensamiento, letrero o efecto sonoro. Conserva el orden natural de lectura.
- "original" debe ser una transcripcion fiel. "translation" debe sonar natural en espanol de Espana, conservar voz, tono, tratamiento y puntuacion, y ser lo bastante concisa para caber.
- Adapta las onomatopeyas cuando exista una forma habitual en espanol. No inventes texto ilegible; reduce confidence.
- Todas las coordenadas usan una escala de 0 a 1000, con origen arriba a la izquierda.
- text_box y text_polygon deben rodear MUY AJUSTADAMENTE solo la tinta de las letras que hay que borrar, no el bocadillo completo.
- render_box debe ser la mayor zona interior segura donde se puede componer la traduccion sin tocar bordes, rabillos, personajes ni dibujos. En texto sin contenedor, usa una zona equivalente al original.
- Describe el aspecto original: familia tipografica aproximada, peso, cursiva, mayusculas, color, contorno, alineacion, fondo, espaciado, sombra, rotacion y disposicion vertical.
- Usa colores hexadecimales #RRGGBB. background_color es el color que hay inmediatamente detras de las letras; usa null si es una textura o no se puede estimar.
- No incluyas explicaciones ni Markdown; responde solo con el JSON solicitado.`;
}

async function ollamaRequest(baseUrl, endpoint, options = {}) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), options.timeoutMs ?? 300_000);
  try {
    const response = await fetch(`${baseUrl}${endpoint}`, {
      ...options,
      signal: controller.signal,
      headers: {
        "Content-Type": "application/json",
        ...(options.headers ?? {}),
      },
    });
    const text = await response.text();
    let data;
    try {
      data = text ? JSON.parse(text) : {};
    } catch {
      data = { error: text || `Respuesta HTTP ${response.status}` };
    }
    if (!response.ok) {
      throw new Error(data.error || `Ollama respondio con HTTP ${response.status}`);
    }
    return data;
  } catch (error) {
    if (error.name === "AbortError") {
      throw new Error("Ollama ha tardado demasiado en analizar la pagina.");
    }
    if (error.cause?.code === "ECONNREFUSED") {
      throw new Error("No se puede conectar con Ollama. Abre Ollama y vuelve a intentarlo.");
    }
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

export async function listModels(baseUrl = DEFAULT_BASE_URL) {
  const data = await ollamaRequest(baseUrl, "/api/tags", { method: "GET", timeoutMs: 10_000 });
  return (data.models ?? []).map((model) => ({
    name: model.name,
    size: model.size,
    modifiedAt: model.modified_at,
  }));
}

export async function analyzeComic({
  imageBase64,
  model,
  targetLanguage = "espanol de Espana",
  baseUrl = DEFAULT_BASE_URL,
}) {
  const data = await ollamaRequest(baseUrl, "/api/chat", {
    method: "POST",
    body: JSON.stringify({
      model,
      stream: false,
      think: false,
      format: analysisSchema,
      messages: [
        {
          role: "user",
          content: promptFor(targetLanguage),
          images: [imageBase64],
        },
      ],
      options: {
        temperature: 0.1,
        seed: 42,
        num_ctx: 32768,
      },
    }),
  });

  const content = data?.message?.content;
  if (!content) throw new Error("Ollama no devolvio ningun analisis.");

  let parsed;
  try {
    parsed = JSON.parse(content);
  } catch {
    throw new Error("El modelo no devolvio un resultado estructurado valido. Intentalo de nuevo.");
  }

  return {
    ...normalizeAnalysis(parsed),
    model,
    durationMs: data.total_duration ? Math.round(data.total_duration / 1_000_000) : null,
  };
}

export { DEFAULT_BASE_URL };
