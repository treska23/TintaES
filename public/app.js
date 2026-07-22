const $ = (selector) => document.querySelector(selector);

const elements = {
  welcomeView: $("#welcomeView"),
  editorView: $("#editorView"),
  fileInput: $("#fileInput"),
  replaceInput: $("#replaceInput"),
  dropStage: $("#dropStage"),
  modelSelect: $("#modelSelect"),
  refreshModels: $("#refreshModels"),
  statusDot: $("#statusDot"),
  statusText: $("#statusText"),
  analyzeButton: $("#analyzeButton"),
  downloadButton: $("#downloadButton"),
  addRegionButton: $("#addRegionButton"),
  compareRange: $("#compareRange"),
  resultCanvas: $("#resultCanvas"),
  originalCanvas: $("#originalCanvas"),
  overlayCanvas: $("#overlayCanvas"),
  processingOverlay: $("#processingOverlay"),
  pageName: $("#pageName"),
  pageDimensions: $("#pageDimensions"),
  regionCount: $("#regionCount"),
  sourceLanguage: $("#sourceLanguage"),
  emptyInspector: $("#emptyInspector"),
  inspectorContent: $("#inspectorContent"),
  regionList: $("#regionList"),
  regionEditor: $("#regionEditor"),
  selectedRegionTitle: $("#selectedRegionTitle"),
  regionEnabled: $("#regionEnabled"),
  originalText: $("#originalText"),
  translationText: $("#translationText"),
  regionType: $("#regionType"),
  cleanupMode: $("#cleanupMode"),
  fontCategory: $("#fontCategory"),
  textAlignment: $("#textAlignment"),
  fontScale: $("#fontScale"),
  fontScaleOutput: $("#fontScaleOutput"),
  textColor: $("#textColor"),
  outlineColor: $("#outlineColor"),
  hasOutline: $("#hasOutline"),
  fontBold: $("#fontBold"),
  fontItalic: $("#fontItalic"),
  fontUppercase: $("#fontUppercase"),
  verticalLayout: $("#verticalLayout"),
  fontInput: $("#fontInput"),
  fontUploadLabel: $("#fontUploadLabel"),
  deleteRegionButton: $("#deleteRegionButton"),
  toast: $("#toast"),
};

const state = {
  image: null,
  imageName: "pagina",
  sourceDataUrl: null,
  regions: [],
  sourceLanguage: "—",
  selectedId: null,
  processing: false,
  adding: false,
  pointerAction: null,
  customFont: null,
  syncingForm: false,
  toastTimer: null,
};

const typeLabels = {
  dialogue: "Diálogo",
  thought: "Pensamiento",
  narration: "Narración",
  caption: "Cartucho",
  sfx: "SFX",
  sign: "Letrero",
  other: "Otro",
};

const fontStacks = {
  comic: '"Comic Sans MS", "Arial Rounded MT Bold", sans-serif',
  handwritten: '"Segoe Print", "Comic Sans MS", cursive',
  sans: 'Arial, "Segoe UI", sans-serif',
  condensed: '"Arial Narrow", "Roboto Condensed", Impact, sans-serif',
  serif: 'Georgia, "Times New Roman", serif',
  display: 'Impact, "Arial Black", sans-serif',
  monospace: 'Consolas, "Courier New", monospace',
  custom: '"TintaPersonalizada", sans-serif',
};

function showToast(message, kind = "info") {
  clearTimeout(state.toastTimer);
  elements.toast.textContent = message;
  elements.toast.className = `toast show${kind === "error" ? " error" : ""}`;
  state.toastTimer = setTimeout(() => {
    elements.toast.className = "toast";
  }, 4200);
}

function selectedRegion() {
  return state.regions.find((region) => region.id === state.selectedId) ?? null;
}

function setOllamaStatus(status, message) {
  elements.statusDot.className = `status-dot ${status}`.trim();
  elements.statusText.textContent = message;
}

async function refreshModels() {
  setOllamaStatus("", "Conectando con Ollama…");
  elements.refreshModels.disabled = true;
  try {
    const response = await fetch("/api/health", { cache: "no-store" });
    const data = await response.json();
    if (!response.ok || !data.ready) throw new Error(data.error || "No hay modelos instalados.");

    elements.modelSelect.innerHTML = "";
    for (const model of data.models) {
      const option = document.createElement("option");
      option.value = model.name;
      option.textContent = model.name;
      option.selected = model.name === data.defaultModel;
      elements.modelSelect.append(option);
    }
    setOllamaStatus("ready", `Ollama listo · ${data.defaultModel}`);
  } catch (error) {
    elements.modelSelect.innerHTML = '<option value="">Ollama no disponible</option>';
    setOllamaStatus("error", "Ollama no está disponible");
    showToast(error.message, "error");
  } finally {
    elements.refreshModels.disabled = false;
  }
}

function fileToDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result);
    reader.onerror = () => reject(new Error("No se pudo leer la imagen."));
    reader.readAsDataURL(file);
  });
}

function loadImage(dataUrl) {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error("El archivo no contiene una imagen válida."));
    image.src = dataUrl;
  });
}

function workingSize(image) {
  const maxDimension = 5200;
  const maxPixels = 22_000_000;
  const scale = Math.min(
    1,
    maxDimension / Math.max(image.naturalWidth, image.naturalHeight),
    Math.sqrt(maxPixels / (image.naturalWidth * image.naturalHeight)),
  );
  return {
    width: Math.max(1, Math.round(image.naturalWidth * scale)),
    height: Math.max(1, Math.round(image.naturalHeight * scale)),
    scale,
  };
}

async function acceptImage(file) {
  if (!file || !/^image\/(png|jpeg|webp)$/i.test(file.type)) {
    showToast("Elige una imagen PNG, JPG o WEBP.", "error");
    return;
  }
  if (file.size > 27 * 1024 * 1024) {
    showToast("La imagen supera el límite de 27 MB.", "error");
    return;
  }

  try {
    const dataUrl = await fileToDataUrl(file);
    const image = await loadImage(dataUrl);
    state.image = image;
    state.sourceDataUrl = dataUrl;
    state.imageName = file.name.replace(/\.[^.]+$/, "") || "pagina";
    state.regions = [];
    state.sourceLanguage = "—";
    state.selectedId = null;
    state.adding = false;

    const size = workingSize(image);
    for (const canvas of [elements.resultCanvas, elements.originalCanvas, elements.overlayCanvas]) {
      canvas.width = size.width;
      canvas.height = size.height;
    }
    const originalContext = elements.originalCanvas.getContext("2d");
    originalContext.clearRect(0, 0, size.width, size.height);
    originalContext.drawImage(image, 0, 0, size.width, size.height);

    elements.pageName.textContent = file.name;
    elements.pageDimensions.textContent = `${image.naturalWidth} × ${image.naturalHeight} px${size.scale < 1 ? " · edición optimizada" : ""}`;
    elements.compareRange.value = "0";
    updateComparison();
    elements.welcomeView.hidden = true;
    elements.editorView.hidden = false;
    updateInspector();
    renderAll();
  } catch (error) {
    showToast(error.message, "error");
  }
}

function analysisImageBase64() {
  const maxDimension = 2300;
  const scale = Math.min(1, maxDimension / Math.max(state.image.naturalWidth, state.image.naturalHeight));
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(state.image.naturalWidth * scale);
  canvas.height = Math.round(state.image.naturalHeight * scale);
  const context = canvas.getContext("2d", { alpha: false });
  context.fillStyle = "#FFFFFF";
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.drawImage(state.image, 0, 0, canvas.width, canvas.height);
  return canvas.toDataURL("image/jpeg", 0.93).split(",")[1];
}

function enrichRegion(region, index) {
  return {
    ...region,
    id: region.id || `region-${Date.now()}-${index}`,
    enabled: true,
    cleanup: "auto",
    fontScale: 1,
    verticalLayout: region.type === "sfx" && region.vertical,
    manual: false,
  };
}

async function analyzePage() {
  if (!state.image || state.processing) return;
  if (!elements.modelSelect.value) {
    showToast("Ollama necesita un modelo local con visión.", "error");
    return;
  }

  state.processing = true;
  elements.processingOverlay.hidden = false;
  elements.analyzeButton.disabled = true;
  elements.downloadButton.disabled = true;
  try {
    const response = await fetch("/api/analyze", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        imageBase64: analysisImageBase64(),
        model: elements.modelSelect.value,
        targetLanguage: "es",
      }),
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || "No se pudo analizar la página.");

    state.regions = data.regions.map(enrichRegion);
    state.sourceLanguage = data.sourceLanguage || "desconocido";
    state.selectedId = state.regions[0]?.id ?? null;
    updateInspector();
    renderAll();

    if (state.regions.length) {
      const time = data.durationMs ? ` en ${(data.durationMs / 1000).toFixed(1)} s` : "";
      showToast(`${state.regions.length} texto${state.regions.length === 1 ? "" : "s"} detectado${state.regions.length === 1 ? "" : "s"}${time}.`);
    } else {
      showToast("El modelo no encontró texto. Puedes añadir una zona manualmente.");
    }
  } catch (error) {
    showToast(error.message, "error");
  } finally {
    state.processing = false;
    elements.processingOverlay.hidden = true;
    elements.analyzeButton.disabled = false;
    elements.downloadButton.disabled = !state.image;
  }
}

function normalizedBoxToPixels(box, canvas = elements.resultCanvas) {
  return {
    x: box.x * canvas.width / 1000,
    y: box.y * canvas.height / 1000,
    width: box.width * canvas.width / 1000,
    height: box.height * canvas.height / 1000,
  };
}

function pixelsBoxToNormalized(box, canvas = elements.resultCanvas) {
  const x = Math.max(0, Math.min(995, box.x * 1000 / canvas.width));
  const y = Math.max(0, Math.min(995, box.y * 1000 / canvas.height));
  return {
    x,
    y,
    width: Math.max(5, Math.min(1000 - x, box.width * 1000 / canvas.width)),
    height: Math.max(5, Math.min(1000 - y, box.height * 1000 / canvas.height)),
  };
}

function parseHex(value) {
  const match = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(value || "");
  return match ? [parseInt(match[1], 16), parseInt(match[2], 16), parseInt(match[3], 16)] : null;
}

function rgbToHex(rgb) {
  return `#${rgb.map((value) => Math.max(0, Math.min(255, Math.round(value))).toString(16).padStart(2, "0")).join("")}`.toUpperCase();
}

function sampleBackground(context, box) {
  const margin = Math.max(3, Math.round(Math.min(box.width, box.height) * 0.12));
  const x = Math.max(0, Math.floor(box.x - margin));
  const y = Math.max(0, Math.floor(box.y - margin));
  const right = Math.min(context.canvas.width, Math.ceil(box.x + box.width + margin));
  const bottom = Math.min(context.canvas.height, Math.ceil(box.y + box.height + margin));
  const width = Math.max(1, right - x);
  const height = Math.max(1, bottom - y);
  const pixels = context.getImageData(x, y, width, height).data;
  const samples = [];
  const step = Math.max(1, Math.floor(Math.min(width, height) / 28));

  const pushPixel = (px, py) => {
    const index = (py * width + px) * 4;
    if (pixels[index + 3] > 200) samples.push([pixels[index], pixels[index + 1], pixels[index + 2]]);
  };
  for (let px = 0; px < width; px += step) {
    pushPixel(px, 0);
    pushPixel(px, height - 1);
  }
  for (let py = 0; py < height; py += step) {
    pushPixel(0, py);
    pushPixel(width - 1, py);
  }
  if (!samples.length) return { color: [255, 255, 255], variance: 0 };

  const sorted = [0, 1, 2].map((channel) => samples.map((sample) => sample[channel]).sort((a, b) => a - b));
  const median = sorted.map((values) => values[Math.floor(values.length / 2)]);
  const variance = samples.reduce((sum, sample) => (
    sum + sample.reduce((channelSum, value, channel) => channelSum + (value - median[channel]) ** 2, 0) / 3
  ), 0) / samples.length;
  return { color: median, variance };
}

function roundedRectPath(context, box, padding = 0) {
  const x = box.x - padding;
  const y = box.y - padding;
  const width = box.width + padding * 2;
  const height = box.height + padding * 2;
  const radius = Math.min(width, height) * 0.14;
  context.beginPath();
  context.roundRect(x, y, width, height, radius);
}

function solidCleanup(context, box, fillColor) {
  const padding = Math.max(2, Math.min(box.width, box.height) * 0.055);
  context.save();
  roundedRectPath(context, box, padding);
  context.fillStyle = fillColor;
  context.fill();
  context.restore();
}

function textureCleanup(context, box) {
  const padding = Math.max(2, Math.round(Math.min(box.width, box.height) * 0.055));
  const left = Math.max(1, Math.floor(box.x - padding));
  const top = Math.max(1, Math.floor(box.y - padding));
  const right = Math.min(context.canvas.width - 2, Math.ceil(box.x + box.width + padding));
  const bottom = Math.min(context.canvas.height - 2, Math.ceil(box.y + box.height + padding));
  const width = right - left + 1;
  const height = bottom - top + 1;
  if (width < 3 || height < 3) return;

  const source = context.getImageData(left - 1, top - 1, width + 2, height + 2);
  const output = context.createImageData(width, height);
  const sourceWidth = width + 2;
  const sourceIndex = (x, y) => (y * sourceWidth + x) * 4;
  const outputIndex = (x, y) => (y * width + x) * 4;

  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      const distances = [y + 1, height - y, x + 1, width - x];
      const samplePositions = [
        [x + 1, 0],
        [x + 1, height + 1],
        [0, y + 1],
        [width + 1, y + 1],
      ];
      const weights = distances.map((distance) => 1 / Math.max(1, distance) ** 1.7);
      const totalWeight = weights.reduce((sum, weight) => sum + weight, 0);
      const destination = outputIndex(x, y);
      for (let channel = 0; channel < 3; channel += 1) {
        let value = 0;
        for (let sample = 0; sample < 4; sample += 1) {
          const [sampleX, sampleY] = samplePositions[sample];
          value += source.data[sourceIndex(sampleX, sampleY) + channel] * weights[sample];
        }
        output.data[destination + channel] = value / totalWeight;
      }
      output.data[destination + 3] = 255;
    }
  }
  context.putImageData(output, left, top);
}

function cleanupRegion(context, region) {
  if (!region.enabled || region.cleanup === "none") return;
  const box = normalizedBoxToPixels(region.textBox);
  const sampled = sampleBackground(context, box);
  const predicted = parseHex(region.style.backgroundColor);
  const useSolid = region.cleanup === "solid"
    || (region.cleanup === "auto" && (sampled.variance < 950 || predicted));

  if (useSolid) {
    const color = sampled.variance < 950 ? sampled.color : predicted ?? sampled.color;
    solidCleanup(context, box, rgbToHex(color));
  } else {
    textureCleanup(context, box);
  }
}

function applyFont(context, region, size) {
  const style = region.style;
  const category = region.fontCategoryOverride || style.fontCategory;
  const family = fontStacks[category] || fontStacks.comic;
  const weight = style.fontWeight >= 650 ? style.fontWeight : 400;
  context.font = `${style.italic ? "italic " : ""}${weight} ${size}px ${family}`;
  if ("letterSpacing" in context) {
    const spacing = Number(style.letterSpacing || 0) * elements.resultCanvas.width / 1000;
    context.letterSpacing = `${spacing}px`;
  }
}

function splitLongWord(context, word, maxWidth) {
  const pieces = [];
  let current = "";
  for (const character of Array.from(word)) {
    const candidate = current + character;
    if (current && context.measureText(candidate).width > maxWidth) {
      pieces.push(current);
      current = character;
    } else {
      current = candidate;
    }
  }
  if (current) pieces.push(current);
  return pieces;
}

function wrapText(context, text, maxWidth) {
  const lines = [];
  for (const paragraph of text.split(/\r?\n/)) {
    const words = paragraph.trim().split(/\s+/).filter(Boolean);
    if (!words.length) {
      lines.push("");
      continue;
    }
    let line = "";
    for (const rawWord of words) {
      const parts = context.measureText(rawWord).width > maxWidth
        ? splitLongWord(context, rawWord, maxWidth)
        : [rawWord];
      for (const word of parts) {
        const candidate = line ? `${line} ${word}` : word;
        if (line && context.measureText(candidate).width > maxWidth) {
          lines.push(line);
          line = word;
        } else {
          line = candidate;
        }
      }
    }
    if (line) lines.push(line);
  }
  return lines;
}

function fitText(context, region, text, width, height) {
  if (region.verticalLayout) {
    const characters = Array.from(text.replace(/\s+/g, ""));
    const size = Math.max(5, Math.min(width * 0.74, height / Math.max(1, characters.length) * 0.88) * region.fontScale);
    applyFont(context, region, size);
    return { size, lines: characters, lineHeight: size * 1.05 };
  }

  let low = 5;
  let high = Math.max(6, Math.min(height * 0.9, width * 0.55, elements.resultCanvas.width * 0.14));
  let best = { size: low, lines: [text], lineHeight: low * 1.08 };
  for (let iteration = 0; iteration < 15; iteration += 1) {
    const size = (low + high) / 2;
    applyFont(context, region, size);
    const lines = wrapText(context, text, width);
    const lineHeight = size * 1.08;
    const fits = lines.length * lineHeight <= height
      && lines.every((line) => context.measureText(line).width <= width + 0.5);
    if (fits) {
      best = { size, lines, lineHeight };
      low = size;
    } else {
      high = size;
    }
  }
  const scaledSize = Math.max(4, best.size * region.fontScale);
  applyFont(context, region, scaledSize);
  const scaledLines = wrapText(context, text, width);
  return { size: scaledSize, lines: scaledLines, lineHeight: scaledSize * 1.08 };
}

function drawTextRegion(context, region) {
  if (!region.enabled || !region.translation.trim()) return;
  const box = normalizedBoxToPixels(region.renderBox);
  const text = region.style.uppercase ? region.translation.toLocaleUpperCase("es") : region.translation;
  const padding = Math.max(2, Math.min(box.width, box.height) * 0.045);
  const usableWidth = Math.max(4, box.width - padding * 2);
  const usableHeight = Math.max(4, box.height - padding * 2);

  context.save();
  context.translate(box.x + box.width / 2, box.y + box.height / 2);
  context.rotate((region.rotation || 0) * Math.PI / 180);
  const fitted = fitText(context, region, text, usableWidth, usableHeight);
  const style = region.style;
  context.textAlign = style.alignment;
  context.textBaseline = "alphabetic";
  context.fillStyle = style.textColor || "#111111";
  context.lineJoin = "round";
  context.miterLimit = 2;
  context.shadowColor = style.shadow ? "rgba(0,0,0,.42)" : "transparent";
  context.shadowBlur = style.shadow ? fitted.size * 0.08 : 0;
  context.shadowOffsetX = style.shadow ? fitted.size * 0.05 : 0;
  context.shadowOffsetY = style.shadow ? fitted.size * 0.06 : 0;

  const totalHeight = fitted.lines.length * fitted.lineHeight;
  const startY = -totalHeight / 2 + fitted.size * 0.84;
  const x = style.alignment === "left"
    ? -usableWidth / 2
    : style.alignment === "right"
      ? usableWidth / 2
      : 0;
  const outlineWidth = Math.max(0, Number(style.outlineWidth || 0)) * elements.resultCanvas.width / 1000;
  if (style.outlineColor && outlineWidth > 0) {
    context.strokeStyle = style.outlineColor;
    context.lineWidth = Math.max(1, outlineWidth * 2);
  }

  fitted.lines.forEach((line, index) => {
    const y = startY + index * fitted.lineHeight;
    if (style.outlineColor && outlineWidth > 0) context.strokeText(line, x, y, usableWidth);
    context.fillText(line, x, y, usableWidth);
  });
  context.restore();
}

function renderResult() {
  if (!state.image) return;
  const context = elements.resultCanvas.getContext("2d", { alpha: false });
  context.save();
  context.setTransform(1, 0, 0, 1, 0, 0);
  context.clearRect(0, 0, context.canvas.width, context.canvas.height);
  context.fillStyle = "#FFFFFF";
  context.fillRect(0, 0, context.canvas.width, context.canvas.height);
  context.drawImage(state.image, 0, 0, context.canvas.width, context.canvas.height);
  for (const region of state.regions) cleanupRegion(context, region);
  for (const region of state.regions) drawTextRegion(context, region);
  context.restore();
}

function overlayDisplayScale() {
  const bounds = elements.overlayCanvas.getBoundingClientRect();
  return bounds.width ? bounds.width / elements.overlayCanvas.width : 1;
}

function drawOverlay() {
  const context = elements.overlayCanvas.getContext("2d");
  context.clearRect(0, 0, context.canvas.width, context.canvas.height);
  if (!state.regions.length) return;
  const scale = overlayDisplayScale();
  const lineWidth = 1.7 / Math.max(0.01, scale);
  const selected = selectedRegion();

  for (const region of state.regions) {
    if (!region.enabled) continue;
    const box = normalizedBoxToPixels(region.renderBox, elements.overlayCanvas);
    const isSelected = region.id === state.selectedId;
    context.save();
    context.strokeStyle = isSelected ? "#E14B3F" : "#227F91";
    context.fillStyle = isSelected ? "rgba(225,75,63,.1)" : "rgba(34,127,145,.045)";
    context.lineWidth = isSelected ? lineWidth * 1.45 : lineWidth;
    context.setLineDash(isSelected ? [] : [5 / scale, 4 / scale]);
    context.strokeRect(box.x, box.y, box.width, box.height);
    context.fillRect(box.x, box.y, box.width, box.height);
    context.setLineDash([]);

    const labelSize = 16 / scale;
    context.fillStyle = isSelected ? "#E14B3F" : "#227F91";
    context.fillRect(box.x, Math.max(0, box.y - labelSize), labelSize * 1.25, labelSize);
    context.fillStyle = "white";
    context.font = `700 ${9 / scale}px Arial`;
    context.textAlign = "center";
    context.textBaseline = "middle";
    context.fillText(String(state.regions.indexOf(region) + 1), box.x + labelSize * 0.625, Math.max(labelSize / 2, box.y - labelSize / 2));
    context.restore();
  }

  if (selected?.enabled) {
    const box = normalizedBoxToPixels(selected.renderBox, elements.overlayCanvas);
    const handle = 9 / Math.max(0.01, scale);
    context.fillStyle = "#FFFDF7";
    context.strokeStyle = "#E14B3F";
    context.lineWidth = 2 / Math.max(0.01, scale);
    context.fillRect(box.x + box.width - handle / 2, box.y + box.height - handle / 2, handle, handle);
    context.strokeRect(box.x + box.width - handle / 2, box.y + box.height - handle / 2, handle, handle);
  }
}

function renderAll() {
  renderResult();
  requestAnimationFrame(drawOverlay);
  elements.downloadButton.disabled = !state.image || state.processing;
}

function updateRegionList() {
  elements.regionList.innerHTML = "";
  state.regions.forEach((region, index) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `region-card${region.id === state.selectedId ? " selected" : ""}${region.enabled ? "" : " disabled"}`;
    button.innerHTML = `
      <span class="region-card-top"><span>Zona ${index + 1}</span><span>${typeLabels[region.type] || "Otro"}</span></span>
      <strong></strong>
      <small></small>
    `;
    button.querySelector("strong").textContent = region.translation || "Sin traducción";
    button.querySelector("small").textContent = region.original || "Sin original";
    button.addEventListener("click", () => selectRegion(region.id));
    elements.regionList.append(button);
  });
}

function syncFormFromRegion() {
  const region = selectedRegion();
  if (!region) return;
  state.syncingForm = true;
  const index = state.regions.indexOf(region) + 1;
  elements.selectedRegionTitle.textContent = `Zona ${index} · ${typeLabels[region.type] || "Otro"}`;
  elements.regionEnabled.checked = region.enabled;
  elements.originalText.value = region.original;
  elements.translationText.value = region.translation;
  elements.regionType.value = region.type;
  elements.cleanupMode.value = region.cleanup;
  elements.fontCategory.value = region.fontCategoryOverride || region.style.fontCategory;
  elements.textAlignment.value = region.style.alignment;
  elements.fontScale.value = String(Math.round(region.fontScale * 100));
  elements.fontScaleOutput.textContent = `${Math.round(region.fontScale * 100)} %`;
  elements.textColor.value = region.style.textColor || "#111111";
  elements.outlineColor.value = region.style.outlineColor || "#FFFFFF";
  elements.hasOutline.checked = Boolean(region.style.outlineColor && region.style.outlineWidth > 0);
  elements.fontBold.checked = region.style.fontWeight >= 650;
  elements.fontItalic.checked = region.style.italic;
  elements.fontUppercase.checked = region.style.uppercase;
  elements.verticalLayout.checked = region.verticalLayout;
  state.syncingForm = false;
}

function updateInspector() {
  const hasRegions = state.regions.length > 0;
  elements.emptyInspector.hidden = hasRegions;
  elements.inspectorContent.hidden = !hasRegions;
  elements.regionCount.textContent = hasRegions
    ? `${state.regions.length} zona${state.regions.length === 1 ? "" : "s"}`
    : "Sin analizar";
  elements.sourceLanguage.textContent = `${state.sourceLanguage || "—"} → ES`;
  if (!hasRegions) return;
  if (!selectedRegion()) state.selectedId = state.regions[0].id;
  updateRegionList();
  syncFormFromRegion();
}

function selectRegion(id) {
  state.selectedId = id;
  updateRegionList();
  syncFormFromRegion();
  drawOverlay();
}

function updateSelected(mutator, { redraw = true, list = false } = {}) {
  if (state.syncingForm) return;
  const region = selectedRegion();
  if (!region) return;
  mutator(region);
  if (list) updateRegionList();
  if (redraw) renderAll();
}

function bindEditorControls() {
  elements.regionEditor.addEventListener("submit", (event) => event.preventDefault());
  elements.regionEnabled.addEventListener("change", () => updateSelected((region) => { region.enabled = elements.regionEnabled.checked; }, { list: true }));
  elements.originalText.addEventListener("input", () => updateSelected((region) => { region.original = elements.originalText.value; }, { redraw: false, list: true }));
  elements.translationText.addEventListener("input", () => updateSelected((region) => { region.translation = elements.translationText.value; }, { list: true }));
  elements.regionType.addEventListener("change", () => updateSelected((region) => { region.type = elements.regionType.value; }, { list: true }));
  elements.cleanupMode.addEventListener("change", () => updateSelected((region) => { region.cleanup = elements.cleanupMode.value; }));
  elements.fontCategory.addEventListener("change", () => updateSelected((region) => { region.fontCategoryOverride = elements.fontCategory.value; }));
  elements.textAlignment.addEventListener("change", () => updateSelected((region) => { region.style.alignment = elements.textAlignment.value; }));
  elements.fontScale.addEventListener("input", () => {
    elements.fontScaleOutput.textContent = `${elements.fontScale.value} %`;
    updateSelected((region) => { region.fontScale = Number(elements.fontScale.value) / 100; });
  });
  elements.textColor.addEventListener("input", () => updateSelected((region) => { region.style.textColor = elements.textColor.value; }));
  elements.outlineColor.addEventListener("input", () => updateSelected((region) => {
    if (elements.hasOutline.checked) region.style.outlineColor = elements.outlineColor.value;
  }));
  elements.hasOutline.addEventListener("change", () => updateSelected((region) => {
    region.style.outlineColor = elements.hasOutline.checked ? elements.outlineColor.value : null;
    region.style.outlineWidth = elements.hasOutline.checked ? Math.max(2, region.style.outlineWidth || 0) : 0;
  }));
  elements.fontBold.addEventListener("change", () => updateSelected((region) => { region.style.fontWeight = elements.fontBold.checked ? 800 : 400; }));
  elements.fontItalic.addEventListener("change", () => updateSelected((region) => { region.style.italic = elements.fontItalic.checked; }));
  elements.fontUppercase.addEventListener("change", () => updateSelected((region) => { region.style.uppercase = elements.fontUppercase.checked; }));
  elements.verticalLayout.addEventListener("change", () => updateSelected((region) => { region.verticalLayout = elements.verticalLayout.checked; }));
}

async function loadCustomFont(file) {
  if (!file) return;
  try {
    const url = URL.createObjectURL(file);
    const font = new FontFace("TintaPersonalizada", `url(${url})`);
    await font.load();
    document.fonts.add(font);
    state.customFont = file.name;
    if (!elements.fontCategory.querySelector('option[value="custom"]')) {
      const option = document.createElement("option");
      option.value = "custom";
      option.textContent = "Fuente propia";
      elements.fontCategory.append(option);
    }
    elements.fontUploadLabel.textContent = file.name;
    elements.fontCategory.value = "custom";
    updateSelected((region) => { region.fontCategoryOverride = "custom"; });
    URL.revokeObjectURL(url);
    showToast("Fuente cargada y aplicada a la zona seleccionada.");
  } catch {
    showToast("No se pudo cargar esa fuente.", "error");
  }
}

function addManualRegion(box) {
  const normalized = pixelsBoxToNormalized(box, elements.overlayCanvas);
  const id = `manual-${Date.now()}`;
  state.regions.push({
    id,
    order: state.regions.length + 1,
    original: "",
    translation: "Texto en español",
    type: "dialogue",
    confidence: 1,
    textBox: { ...normalized },
    renderBox: { ...normalized },
    polygon: [
      { x: normalized.x, y: normalized.y },
      { x: normalized.x + normalized.width, y: normalized.y },
      { x: normalized.x + normalized.width, y: normalized.y + normalized.height },
      { x: normalized.x, y: normalized.y + normalized.height },
    ],
    rotation: 0,
    vertical: false,
    verticalLayout: false,
    style: {
      fontCategory: "comic",
      fontWeight: 700,
      italic: false,
      uppercase: false,
      textColor: "#111111",
      outlineColor: null,
      outlineWidth: 0,
      alignment: "center",
      backgroundColor: null,
      letterSpacing: 0,
      shadow: false,
    },
    enabled: true,
    cleanup: "auto",
    fontScale: 1,
    manual: true,
  });
  state.selectedId = id;
  updateInspector();
  renderAll();
  setTimeout(() => elements.translationText.select(), 0);
}

function pointerPosition(event) {
  const bounds = elements.overlayCanvas.getBoundingClientRect();
  return {
    x: (event.clientX - bounds.left) * elements.overlayCanvas.width / bounds.width,
    y: (event.clientY - bounds.top) * elements.overlayCanvas.height / bounds.height,
  };
}

function pointInside(point, box) {
  return point.x >= box.x && point.x <= box.x + box.width && point.y >= box.y && point.y <= box.y + box.height;
}

function beginPointerAction(event) {
  if (!state.image) return;
  const point = pointerPosition(event);
  elements.overlayCanvas.setPointerCapture(event.pointerId);

  if (state.adding) {
    state.pointerAction = { type: "add", start: point, current: point };
    return;
  }

  const current = selectedRegion();
  const displayScale = overlayDisplayScale();
  if (current) {
    const selectedBox = normalizedBoxToPixels(current.renderBox, elements.overlayCanvas);
    const tolerance = 13 / Math.max(0.01, displayScale);
    const nearHandle = Math.abs(point.x - (selectedBox.x + selectedBox.width)) <= tolerance
      && Math.abs(point.y - (selectedBox.y + selectedBox.height)) <= tolerance;
    if (nearHandle) {
      state.pointerAction = { type: "resize", start: point, original: { ...selectedBox }, regionId: current.id };
      return;
    }
  }

  const hit = [...state.regions].reverse().find((region) => region.enabled && pointInside(point, normalizedBoxToPixels(region.renderBox, elements.overlayCanvas)));
  if (hit) {
    selectRegion(hit.id);
    state.pointerAction = {
      type: "move",
      start: point,
      original: normalizedBoxToPixels(hit.renderBox, elements.overlayCanvas),
      originalTextBox: normalizedBoxToPixels(hit.textBox, elements.overlayCanvas),
      regionId: hit.id,
    };
  }
}

function movePointerAction(event) {
  if (!state.pointerAction) return;
  const point = pointerPosition(event);
  const action = state.pointerAction;
  action.current = point;

  if (action.type === "add") {
    drawOverlay();
    const context = elements.overlayCanvas.getContext("2d");
    context.save();
    context.strokeStyle = "#E14B3F";
    context.fillStyle = "rgba(225,75,63,.12)";
    context.lineWidth = 2 / Math.max(0.01, overlayDisplayScale());
    const x = Math.min(action.start.x, point.x);
    const y = Math.min(action.start.y, point.y);
    const width = Math.abs(point.x - action.start.x);
    const height = Math.abs(point.y - action.start.y);
    context.strokeRect(x, y, width, height);
    context.fillRect(x, y, width, height);
    context.restore();
    return;
  }

  const region = state.regions.find((candidate) => candidate.id === action.regionId);
  if (!region) return;
  const dx = point.x - action.start.x;
  const dy = point.y - action.start.y;
  if (action.type === "move") {
    const moved = {
      ...action.original,
      x: Math.max(0, Math.min(elements.overlayCanvas.width - action.original.width, action.original.x + dx)),
      y: Math.max(0, Math.min(elements.overlayCanvas.height - action.original.height, action.original.y + dy)),
    };
    region.renderBox = pixelsBoxToNormalized(moved, elements.overlayCanvas);
    if (region.manual) {
      const movedText = { ...action.originalTextBox, x: action.originalTextBox.x + dx, y: action.originalTextBox.y + dy };
      region.textBox = pixelsBoxToNormalized(movedText, elements.overlayCanvas);
    }
  } else if (action.type === "resize") {
    const resized = {
      ...action.original,
      width: Math.max(18, point.x - action.original.x),
      height: Math.max(18, point.y - action.original.y),
    };
    region.renderBox = pixelsBoxToNormalized(resized, elements.overlayCanvas);
    if (region.manual) region.textBox = { ...region.renderBox };
  }
  renderAll();
}

function endPointerAction(event) {
  const action = state.pointerAction;
  if (!action) return;
  if (elements.overlayCanvas.hasPointerCapture(event.pointerId)) elements.overlayCanvas.releasePointerCapture(event.pointerId);
  state.pointerAction = null;

  if (action.type === "add") {
    const point = action.current || action.start;
    const box = {
      x: Math.min(action.start.x, point.x),
      y: Math.min(action.start.y, point.y),
      width: Math.abs(point.x - action.start.x),
      height: Math.abs(point.y - action.start.y),
    };
    state.adding = false;
    elements.overlayCanvas.classList.remove("adding");
    elements.addRegionButton.textContent = "Añadir zona";
    if (box.width > 18 && box.height > 18) addManualRegion(box);
    else drawOverlay();
  }
}

function toggleAddRegion() {
  if (!state.image) return;
  state.adding = !state.adding;
  elements.overlayCanvas.classList.toggle("adding", state.adding);
  if (state.adding) {
    elements.addRegionButton.textContent = "Dibuja sobre la página";
    showToast("Arrastra sobre el texto o el bocadillo que quieras añadir.");
  } else {
    elements.addRegionButton.textContent = "Añadir zona";
  }
}

function deleteSelectedRegion() {
  const index = state.regions.findIndex((region) => region.id === state.selectedId);
  if (index < 0) return;
  state.regions.splice(index, 1);
  state.selectedId = state.regions[Math.min(index, state.regions.length - 1)]?.id ?? null;
  updateInspector();
  renderAll();
}

function updateComparison() {
  const percentage = Number(elements.compareRange.value);
  elements.originalCanvas.style.clipPath = `inset(0 ${100 - percentage}% 0 0)`;
  elements.overlayCanvas.style.opacity = percentage > 96 ? "0" : "1";
}

function downloadResult() {
  if (!state.image) return;
  elements.resultCanvas.toBlob((blob) => {
    if (!blob) {
      showToast("No se pudo preparar el PNG.", "error");
      return;
    }
    const link = document.createElement("a");
    const url = URL.createObjectURL(blob);
    link.href = url;
    link.download = `${state.imageName}-es.png`;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    showToast("Página exportada en PNG.");
  }, "image/png");
}

function setupDropZone() {
  const prevent = (event) => {
    event.preventDefault();
    event.stopPropagation();
  };
  for (const name of ["dragenter", "dragover"]) {
    elements.dropStage.addEventListener(name, (event) => {
      prevent(event);
      elements.dropStage.classList.add("dragging");
    });
  }
  for (const name of ["dragleave", "drop"]) {
    elements.dropStage.addEventListener(name, (event) => {
      prevent(event);
      elements.dropStage.classList.remove("dragging");
    });
  }
  elements.dropStage.addEventListener("drop", (event) => acceptImage(event.dataTransfer.files[0]));
}

elements.fileInput.addEventListener("change", () => acceptImage(elements.fileInput.files[0]));
elements.replaceInput.addEventListener("change", () => acceptImage(elements.replaceInput.files[0]));
elements.refreshModels.addEventListener("click", refreshModels);
elements.analyzeButton.addEventListener("click", analyzePage);
elements.downloadButton.addEventListener("click", downloadResult);
elements.addRegionButton.addEventListener("click", toggleAddRegion);
elements.compareRange.addEventListener("input", updateComparison);
elements.deleteRegionButton.addEventListener("click", deleteSelectedRegion);
elements.fontInput.addEventListener("change", () => loadCustomFont(elements.fontInput.files[0]));
elements.overlayCanvas.addEventListener("pointerdown", beginPointerAction);
elements.overlayCanvas.addEventListener("pointermove", movePointerAction);
elements.overlayCanvas.addEventListener("pointerup", endPointerAction);
elements.overlayCanvas.addEventListener("pointercancel", endPointerAction);
window.addEventListener("resize", () => requestAnimationFrame(drawOverlay));

bindEditorControls();
setupDropZone();
refreshModels();
