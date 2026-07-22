const REGION_TYPES = new Set([
  "dialogue",
  "thought",
  "narration",
  "caption",
  "sfx",
  "sign",
  "other",
]);

const FONT_CATEGORIES = new Set([
  "comic",
  "handwritten",
  "sans",
  "condensed",
  "serif",
  "display",
  "monospace",
]);

const ALIGNMENTS = new Set(["left", "center", "right"]);

function numberInRange(value, fallback, min, max) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? Math.min(max, Math.max(min, parsed)) : fallback;
}

function cleanText(value, fallback = "") {
  return typeof value === "string" ? value.trim() : fallback;
}

function color(value, fallback) {
  if (typeof value !== "string") return fallback;
  const candidate = value.trim().toUpperCase();
  if (/^#[0-9A-F]{6}$/.test(candidate)) return candidate;
  if (/^#[0-9A-F]{3}$/.test(candidate)) {
    return `#${candidate[1]}${candidate[1]}${candidate[2]}${candidate[2]}${candidate[3]}${candidate[3]}`;
  }
  return fallback;
}

function nullableColor(value) {
  if (value === null || value === undefined || value === "") return null;
  return color(value, null);
}

function normalizeBox(raw, fallback = { x: 100, y: 100, width: 300, height: 160 }) {
  if (!raw || typeof raw !== "object") return { ...fallback };

  const left = raw.x ?? raw.left;
  const top = raw.y ?? raw.top;
  const inferredWidth = raw.width ?? (Number.isFinite(Number(raw.right)) ? Number(raw.right) - Number(left) : undefined);
  const inferredHeight = raw.height ?? (Number.isFinite(Number(raw.bottom)) ? Number(raw.bottom) - Number(top) : undefined);
  const x = numberInRange(left, fallback.x, 0, 995);
  const y = numberInRange(top, fallback.y, 0, 995);
  const width = numberInRange(inferredWidth, fallback.width, 5, 1000 - x);
  const height = numberInRange(inferredHeight, fallback.height, 5, 1000 - y);

  return { x, y, width, height };
}

function normalizePolygon(raw, box) {
  if (!Array.isArray(raw) || raw.length < 3) {
    return [
      { x: box.x, y: box.y },
      { x: box.x + box.width, y: box.y },
      { x: box.x + box.width, y: box.y + box.height },
      { x: box.x, y: box.y + box.height },
    ];
  }

  return raw.slice(0, 12).map((point) => ({
    x: numberInRange(point?.x, box.x, 0, 1000),
    y: numberInRange(point?.y, box.y, 0, 1000),
  }));
}

function normalizeStyle(raw = {}) {
  const fontCategory = cleanText(raw.font_category ?? raw.fontCategory, "comic").toLowerCase();
  const alignment = cleanText(raw.alignment, "center").toLowerCase();
  return {
    fontCategory: FONT_CATEGORIES.has(fontCategory) ? fontCategory : "comic",
    fontWeight: Math.round(numberInRange(raw.font_weight ?? raw.fontWeight, 700, 400, 900) / 100) * 100,
    italic: Boolean(raw.italic),
    uppercase: Boolean(raw.uppercase),
    textColor: color(raw.text_color ?? raw.textColor, "#111111"),
    outlineColor: nullableColor(raw.outline_color ?? raw.outlineColor),
    outlineWidth: numberInRange(raw.outline_width ?? raw.outlineWidth, 0, 0, 8),
    alignment: ALIGNMENTS.has(alignment) ? alignment : "center",
    backgroundColor: nullableColor(raw.background_color ?? raw.backgroundColor),
    letterSpacing: numberInRange(raw.letter_spacing ?? raw.letterSpacing, 0, -1, 8),
    shadow: Boolean(raw.shadow),
  };
}

export function normalizeAnalysis(raw) {
  const regions = Array.isArray(raw?.regions) ? raw.regions : [];
  const normalized = regions
    .slice(0, 150)
    .map((region, index) => {
      const textBox = normalizeBox(region.text_box ?? region.textBox ?? region.bbox);
      const renderBox = normalizeBox(region.render_box ?? region.renderBox, textBox);
      const original = cleanText(region.original);
      const translation = cleanText(region.translation, original);

      if (!original && !translation) return null;

      const type = cleanText(region.type, "other").toLowerCase();
      return {
        id: `region-${index + 1}`,
        order: Math.round(numberInRange(region.order, index + 1, 1, 999)),
        original,
        translation,
        type: REGION_TYPES.has(type) ? type : "other",
        confidence: numberInRange(region.confidence, 0.75, 0, 1),
        textBox,
        renderBox,
        polygon: normalizePolygon(region.text_polygon ?? region.textPolygon, textBox),
        rotation: numberInRange(region.rotation, 0, -180, 180),
        vertical: Boolean(region.vertical),
        style: normalizeStyle(region.style),
      };
    })
    .filter(Boolean)
    .sort((a, b) => a.order - b.order);

  return {
    sourceLanguage: cleanText(raw?.source_language ?? raw?.sourceLanguage, "desconocido"),
    regions: normalized,
  };
}

export const analysisSchema = {
  type: "object",
  additionalProperties: false,
  properties: {
    source_language: { type: "string" },
    regions: {
      type: "array",
      items: {
        type: "object",
        additionalProperties: false,
        properties: {
          order: { type: "integer" },
          original: { type: "string" },
          translation: { type: "string" },
          type: {
            type: "string",
            enum: ["dialogue", "thought", "narration", "caption", "sfx", "sign", "other"],
          },
          confidence: { type: "number" },
          text_box: { $ref: "#/$defs/box" },
          render_box: { $ref: "#/$defs/box" },
          text_polygon: {
            type: "array",
            items: { $ref: "#/$defs/point" },
          },
          rotation: { type: "number" },
          vertical: { type: "boolean" },
          style: {
            type: "object",
            additionalProperties: false,
            properties: {
              font_category: {
                type: "string",
                enum: ["comic", "handwritten", "sans", "condensed", "serif", "display", "monospace"],
              },
              font_weight: { type: "integer" },
              italic: { type: "boolean" },
              uppercase: { type: "boolean" },
              text_color: { type: "string" },
              outline_color: { type: ["string", "null"] },
              outline_width: { type: "number" },
              alignment: { type: "string", enum: ["left", "center", "right"] },
              background_color: { type: ["string", "null"] },
              letter_spacing: { type: "number" },
              shadow: { type: "boolean" },
            },
            required: [
              "font_category",
              "font_weight",
              "italic",
              "uppercase",
              "text_color",
              "outline_color",
              "outline_width",
              "alignment",
              "background_color",
              "letter_spacing",
              "shadow"
            ],
          },
        },
        required: [
          "order",
          "original",
          "translation",
          "type",
          "confidence",
          "text_box",
          "render_box",
          "text_polygon",
          "rotation",
          "vertical",
          "style"
        ],
      },
    },
  },
  required: ["source_language", "regions"],
  $defs: {
    box: {
      type: "object",
      additionalProperties: false,
      properties: {
        x: { type: "number" },
        y: { type: "number" },
        width: { type: "number" },
        height: { type: "number" },
      },
      required: ["x", "y", "width", "height"],
    },
    point: {
      type: "object",
      additionalProperties: false,
      properties: {
        x: { type: "number" },
        y: { type: "number" },
      },
      required: ["x", "y"],
    },
  },
};
