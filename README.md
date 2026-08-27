# Tinta ES

Tinta ES es un lector y traductor de cómics WPF completamente local. Detecta diálogos, pensamientos, cartuchos, letreros y onomatopeyas, comprende el contexto de la página y lo traduce al español, pero conserva intacta la imagen original. Al pulsar cualquier texto, su traducción aparece en una tarjeta centrada y desaparece al pulsar fuera o, en una pantalla táctil, al levantar el dedo.

No usa servicios de pago, suscripciones ni claves de API. Las imágenes permanecen en el ordenador.

## Cómo funciona

- **Detección y OCR:** Comic Text Detector aporta geometría y máscaras; PaddleOCR-VL 1.6 mejora localmente la lectura del OCR 48 px de Manga Image Translator.
- **Traducción:** `translategemma:12b` se ejecuta en Ollama y traduce todos los textos detectados de la página con contexto compartido. TintaES conserva compatibilidad con `translategemma:4b` en equipos con menos memoria.
- **Lectura intacta:** las zonas de pulsación son invisibles y nunca borran, tapan ni sustituyen píxeles del cómic.
- **Interacción directa:** rueda para ampliar, clic y arrastre para moverse, pellizco táctil para zoom y barrido horizontal seguro para cambiar de página.
- **Revisión:** cada traducción se puede corregir desde el lector o desde el panel de textos y queda guardada en el proyecto `.tinta`.
- **Modo lector:** pantalla completa con `F11`, ajuste a página o ancho, dirección occidental/manga, precarga y caché de páginas.

## Inicio rápido en el equipo configurado

1. Abre Ollama si no se inició con Windows.
2. Haz doble clic en `iniciar.bat` o abre `TintaES.sln` en Visual Studio.
3. Carga una página PNG, JPEG, WEBP, TIFF o BMP, una carpeta o un CBZ.
4. Pulsa **Detectar y traducir**.
5. Pulsa **Leer** —o usa directamente la vista principal— y toca o haz clic sobre cualquier texto traducido.
6. Guarda el proyecto `.tinta` si has corregido alguna traducción.

En PC, la rueda controla el zoom y el botón izquierdo arrastra la página sin necesidad de
mantener Espacio ni Control. En una pantalla táctil, un dedo desplaza y dos dedos amplían o
reducen. Un barrido solo cambia de página cuando es horizontal, largo, no comenzó en un
bocadillo y la vista ya se encuentra en el borde; así un toque de traducción no puede pasar
de página accidentalmente.

También se puede ejecutar desde una terminal:

```powershell
dotnet run --project src\TintaES.Wpf\TintaES.Wpf.csproj --configuration Release
```

## Requisitos

- Windows 10 2004 o posterior.
- .NET 10 SDK y Visual Studio con la carga de trabajo de escritorio .NET.
- Python 3.11 en `engine/manga-image-translator/.venv`.
- Una GPU NVIDIA compatible con CUDA es muy recomendable.
- Ollama con `translategemma:12b` instalado (modelo de calidad recomendado). `translategemma:4b` sigue siendo una alternativa más rápida.
- El submódulo `engine/manga-image-translator` y sus modelos CTD y OCR.
- El entorno aislado de PaddleOCR-VL 1.6 preparado con `engine\paddleocr\setup-paddleocr.ps1`.

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

La prueba de integración acepta la ruta de una imagen y comprueba OCR y traducción local:

```powershell
dotnet run --project tests\TintaES.IntegrationTests\TintaES.IntegrationTests.csproj --configuration Debug -- "C:\ruta\pagina.jpg"
```

Las regresiones específicas del lector comprueban las zonas pulsables y que los barridos
cortos, los pellizcos o los arrastres sin llegar al borde nunca cambien de página:

```powershell
dotnet run --project tests\TintaES.IntegrationTests\TintaES.IntegrationTests.csproj --configuration Debug -- --reader-hit-test-self-test
```

## Privacidad

El programa solo se comunica con Ollama en `127.0.0.1`. No contiene analítica, telemetría ni llamadas a servicios externos durante el procesamiento de una página.
