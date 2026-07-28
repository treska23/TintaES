# Tinta ES

Tinta ES es una aplicación de escritorio WPF para traducir y rotular cómics completamente en local. Detecta el texto de una página, lo elimina reconstruyendo el dibujo que había detrás, traduce el diálogo al español y compone el nuevo texto dentro de la silueta real de cada bocadillo.

No usa servicios de pago, suscripciones ni claves de API. Las imágenes permanecen en el ordenador.

## Cómo funciona

- **Detección y OCR:** Comic Text Detector y OCR 48 px de Manga Image Translator.
- **Borrado orgánico:** LaMa elimina las letras mediante una máscara ajustada a los glifos; no coloca rectángulos blancos sobre la página.
- **Traducción:** `translategemma:4b` se ejecuta en Ollama y traduce todos los bocadillos de la página con contexto compartido.
- **Rotulación:** WPF guarda un polígono seguro por bocadillo y calcula una anchura diferente para cada línea, adaptándose a formas ovaladas, irregulares o con aristas.
- **Revisión:** las zonas siguen siendo editables y se puede comparar original, máscara, fondo limpio y resultado.

## Inicio rápido en el equipo configurado

1. Abre Ollama si no se inició con Windows.
2. Haz doble clic en `iniciar.bat` o abre `TintaES.sln` en Visual Studio.
3. Carga una página PNG, JPEG, WEBP, TIFF o BMP, una carpeta o un CBZ.
4. Pulsa **Analizar y traducir**.
5. Revisa las zonas y pulsa **Exportar imagen**, **Exportar PSD** o **Exportar CBZ**.
   La página puede guardarse como PNG, JPEG, WebP, TIFF, BMP o PDF.

La edición del lienzo conserva el autoajuste al corregir una traducción. Arrastrar o
redimensionar una caja activa el modo manual, que mantiene el texto dentro de sus límites.
La paleta flotante reúne las herramientas de selección y máscara; `Ctrl+Z` y `Ctrl+Y`
deshacen y rehacen los cambios.

También se puede ejecutar desde una terminal:

```powershell
dotnet run --project src\TintaES.Wpf\TintaES.Wpf.csproj --configuration Release
```

## Requisitos

- Windows 10 2004 o posterior.
- .NET 10 SDK y Visual Studio con la carga de trabajo de escritorio .NET.
- Python 3.11 en `engine/manga-image-translator/.venv`.
- Una GPU NVIDIA compatible con CUDA es muy recomendable.
- Ollama con `translategemma:4b` instalado.
- El submódulo `engine/manga-image-translator` y sus modelos CTD, OCR y LaMa.

Los modelos, el entorno virtual, las cachés y las páginas procesadas no se guardan en GitHub por su tamaño y privacidad.

Después de clonar el repositorio, descarga el motor con:

```powershell
git submodule update --init --recursive
```

## Comprobaciones

```powershell
dotnet build TintaES.sln --configuration Debug
dotnet run --project tests\TintaES.Core.Tests\TintaES.Core.Tests.csproj --configuration Debug
```

La prueba de integración acepta la ruta de una imagen y genera una vista previa local en `artifacts/`:

```powershell
dotnet run --project tests\TintaES.IntegrationTests\TintaES.IntegrationTests.csproj --configuration Debug -- "C:\ruta\pagina.jpg"
```

Esta prueba valida la traducción, las referencias tipográficas, el ajuste seguro de una
caja manual y las exportaciones PNG, JPEG, WebP, TIFF, BMP y PDF.

## Privacidad

El programa solo se comunica con Ollama en `127.0.0.1`. No contiene analítica, telemetría ni llamadas a servicios externos durante el procesamiento de una página.
