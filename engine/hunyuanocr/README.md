# HunyuanOCR-1.5 en TintaES

TintaES usa HunyuanOCR-1.5 como fuente primaria opcional para leer el texto de una página completa. `comic-text-detector` sigue aportando geometría, máscara e inpainting; HunyuanOCR aporta el texto reconocido antes de enviarlo a TranslateGemma.

## Preparación local (Windows)

Desde PowerShell, en esta carpeta:

```powershell
.\setup-hunyuanocr.ps1
```

El script:

1. instala el runtime CUDA oficial precompilado si detecta una GPU NVIDIA; si no,
   compila `llama.cpp` para la plataforma disponible;
2. descarga el checkpoint oficial `tencent/HunyuanOCR`;
3. genera `model\hyocr-f16.gguf` y `model\mmproj-hyocr-f16.gguf`;
4. deja `llama-server.exe` en la ubicación que TintaES detecta automáticamente.

Por defecto usa CUDA si detecta una GPU NVIDIA. No hace falta instalar CUDA Toolkit:
si no encuentra `nvcc`, descarga el runtime CUDA oficial y deja la compilación CPU
existente como respaldo. Se puede forzar:

```powershell
.\setup-hunyuanocr.ps1 -Backend cuda
.\setup-hunyuanocr.ps1 -Backend cpu
```

## Uso

No hace falta arrancar nada a mano. Al analizar una página, TintaES intenta `http://127.0.0.1:8080/v1/models`. Si no hay servidor pero están los GGUF y `llama-server.exe`, TintaES lo arranca automáticamente.

Para una prueba manual:

```powershell
.\start-hunyuanocr.ps1
```

## Configuración

Variables opcionales:

- `TINTAES_HUNYUAN_OCR=0` desactiva HunyuanOCR y fuerza el lector clásico.
- `TINTAES_HUNYUAN_OCR_URL=http://127.0.0.1:8080` cambia el endpoint.
- `TINTAES_HUNYUAN_OCR_MODEL=HYVL` fuerza el identificador de modelo de la API.
- `TINTAES_HUNYUAN_SERVER=C:\ruta\llama-server.exe` fuerza un ejecutable concreto.

Los bloques visuales reconocidos se guardan en una caché independiente, ligada al
contenido de la imagen, al modelo, al prompt y al runtime. Volver a abrir una página
ya reconocida no repite la inferencia y no reutiliza resultados si cambia cualquiera
de esos elementos.

Si HunyuanOCR no está disponible o falla una petición, el análisis no se cancela: TintaES conserva el OCR clásico y muestra el estado en la barra inferior.

## Arquitectura

Flujo principal:

`CTD / máscara -> regiones geométricas -> HunyuanOCR text spotting -> asociación por coordenadas -> TranslateGemma -> lettering`

HunyuanOCR recibe la página completa y se le pide agrupar todas las líneas del mismo bocadillo, didascalia, cartel o contenedor visual en un único bloque. La lectura anterior de TintaES se conserva en `ocrAlternatives` cuando Hunyuan la sustituye.

El despliegue local sigue la ruta oficial de HunyuanOCR-1.5 mediante `llama.cpp` y un `llama-server` compatible con OpenAI.
