# PaddleOCR-VL 1.6 en TintaES

TintaES usa PaddleOCR-VL 1.6 como lector visual opcional de los bocadillos detectados. Comic Text Detector sigue proporcionando la geometría, la máscara y el inpainting; PaddleOCR-VL relee cada recorte para mejorar la transcripción antes de traducirla con TranslateGemma.

PaddleOCR-VL 1.6 tiene pesos abiertos bajo Apache 2.0 y se ejecuta localmente. No envía las páginas a ningún servicio.

## Preparación

Desde PowerShell:

```powershell
.\setup-paddleocr.ps1
```

El instalador crea un entorno independiente en `D:\TintaESData\paddleocr`. No modifica el entorno de Manga Image Translator. En la primera prueba se descargan los modelos oficiales de PaddleOCR-VL 1.6 y su analizador de disposición.

Variables opcionales:

- `TINTAES_PADDLE_OCR=0`: desactiva el lector visual y usa solo el OCR clásico.
- `TINTAES_PADDLE_PYTHON`: ruta a otro Python preparado.
- `TINTAES_PADDLE_HOME`: carpeta de datos y modelos.
- `TINTAES_PADDLE_DEVICE=cpu`: fuerza CPU; el valor predeterminado es `gpu:0`.
- `TINTAES_PADDLE_ENGINE=paddle`: cambia el motor; en Windows se usa `transformers` de forma predeterminada.

Flujo: `CTD / máscara -> OCR clásico -> PaddleOCR-VL 1.6 -> asociación por coordenadas -> TranslateGemma 12B -> rotulación`.
