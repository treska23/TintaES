const fs = require('fs');
const path = require('path');
const { PNG } = require('pngjs');
const { writePsdBuffer } = require('ag-psd');

function parseArgs(argv) {
  const result = {};
  for (let i = 2; i < argv.length; i += 2) {
    const key = argv[i];
    const value = argv[i + 1];
    if (!key || !key.startsWith('--') || value === undefined) {
      throw new Error(`Argumentos PSD inválidos cerca de '${key || ''}'.`);
    }
    result[key.substring(2)] = value;
  }
  for (const required of ['background', 'composite', 'regions', 'output']) {
    if (!result[required]) {
      throw new Error(`Falta el argumento --${required}.`);
    }
  }
  return result;
}

function readPng(filePath) {
  const png = PNG.sync.read(fs.readFileSync(filePath));
  return {
    width: png.width,
    height: png.height,
    data: new Uint8ClampedArray(png.data.buffer, png.data.byteOffset, png.data.byteLength),
  };
}

function parseHexColor(value) {
  let raw = String(value || '#111111').trim().replace(/^#/, '');
  if (raw.length === 3) {
    raw = raw.split('').map(ch => ch + ch).join('');
  }
  if (!/^[0-9a-fA-F]{6}$/.test(raw)) {
    raw = '111111';
  }
  return {
    r: parseInt(raw.substring(0, 2), 16),
    g: parseInt(raw.substring(2, 4), 16),
    b: parseInt(raw.substring(4, 6), 16),
  };
}

function postScriptFont(style) {
  const requested = String((style && style.fontFamily) || '').trim().toLowerCase();
  const mapping = {
    'arial': 'ArialMT',
    'arial narrow': 'ArialNarrow',
    'comic sans ms': 'ComicSansMS',
    'georgia': 'Georgia',
    'impact': 'Impact',
    'consolas': 'Consolas',
    'segoe print': 'SegoePrint',
  };
  return mapping[requested] || 'ArialMT';
}

function fontSizeForRegion(region, pageHeight, boxHeight) {
  const manualBase = Number(region.manualBaseFontSize || 0);
  let base;
  if (manualBase > 0) {
    base = manualBase;
  } else {
    const style = region.style || {};
    const normalized = Number(style.fontSize || 0);
    if (normalized > 0) {
      base = normalized / 1000 * pageHeight;
    } else {
      const text = String(region.translation || region.original || '');
      const lines = Math.max(1, text.split(/\r?\n/).length);
      base = Math.max(8, boxHeight / (lines * 1.45));
    }
  }
  const scale = Number(region.manualFontScale || 1);
  return Math.max(4, Math.min(500, base * scale));
}

function buildTextLayer(region, index, width, height) {
  if (region.isEnabled === false) return null;

  let text = String(region.translation || region.original || '').trim();
  if (!text) return null;

  const style = region.style || {};
  if (style.uppercase) text = text.toUpperCase();
  // Photoshop usa retornos CR en el contenido interno de capas de texto.
  text = text.replace(/\r?\n/g, '\r');

  const box = region.renderBox || {};
  const normalizedX = Number(box.x || 0) + Number(region.textOffsetX || 0);
  const normalizedY = Number(box.y || 0) + Number(region.textOffsetY || 0);
  const normalizedW = Math.max(5, Number(box.width || 100));
  const normalizedH = Math.max(5, Number(box.height || 50));

  const left = normalizedX / 1000 * width;
  const top = normalizedY / 1000 * height;
  const boxWidth = normalizedW / 1000 * width;
  const boxHeight = normalizedH / 1000 * height;
  const fontSize = fontSizeForRegion(region, height, boxHeight);

  return {
    name: `Texto ${String(index).padStart(2, '0')} - ${region.type || 'dialogue'}`,
    top: Math.max(0, Math.floor(top)),
    left: Math.max(0, Math.floor(left)),
    bottom: Math.min(height, Math.ceil(top + boxHeight)),
    right: Math.min(width, Math.ceil(left + boxWidth)),
    text: {
      text,
      transform: [1, 0, 0, 1, left, top + fontSize],
      style: {
        font: { name: postScriptFont(style) },
        fontSize,
        fillColor: parseHexColor(style.textColor),
        fauxBold: Number(style.fontWeight || 400) >= 650,
        fauxItalic: Boolean(style.italic),
      },
    },
  };
}

function main() {
  const args = parseArgs(process.argv);
  const background = readPng(args.background);
  const composite = readPng(args.composite);
  if (background.width !== composite.width || background.height !== composite.height) {
    throw new Error('El fondo limpio y la composición final no tienen el mismo tamaño.');
  }

  const regions = JSON.parse(fs.readFileSync(args.regions, 'utf8').replace(/^\uFEFF/, ''));
  const children = [
    {
      name: 'Fondo limpio',
      top: 0,
      left: 0,
      bottom: background.height,
      right: background.width,
      imageData: background,
    },
  ];

  let layerIndex = 1;
  for (const region of regions) {
    const layer = buildTextLayer(region, layerIndex, background.width, background.height);
    if (layer) {
      children.push(layer);
      layerIndex += 1;
    }
  }

  const psd = {
    width: background.width,
    height: background.height,
    imageData: composite,
    children,
  };

  const output = writePsdBuffer(psd, {
    generateThumbnail: true,
    invalidateTextLayers: true,
    noBackground: true,
  });
  fs.mkdirSync(path.dirname(args.output), { recursive: true });
  fs.writeFileSync(args.output, output);
}

try {
  main();
} catch (error) {
  console.error(error && error.stack ? error.stack : String(error));
  process.exit(1);
}
