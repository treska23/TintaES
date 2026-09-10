# Medición de la coordinación OCR/traducción

Este comprobador manual utiliza CTD, PaddleOCR y `translategemma:12b` instalados en el equipo. No cambia el modelo, los parámetros, los prompts, las comprobaciones ni los reintentos internos de `OllamaClient.TranslateRegionsAsync`. No ejecuta la recuperación adicional de la interfaz ni dibuja la página. Se limita a preparar las mismas páginas y traducirlas en el mismo orden: una a una (`baseline`) o primera página inmediata y siguientes grupos de cuatro (`window`).

Compilar primero el proyecto de integración y ejecutar su `.exe` desde la raíz del repositorio:

```powershell
$benchmark = '.\tests\TintaES.IntegrationTests\bin\Release\net10.0-windows10.0.19041.0\TintaES.IntegrationTests.exe'
$pages = @('ruta\pagina1.png', 'ruta\pagina2.png', 'ruta\pagina3.png', 'ruta\pagina4.png', 'ruta\pagina5.png')
& $benchmark --translation-scheduling-benchmark baseline .artifacts\translation-baseline --reload-before-ocr @pages
& $benchmark --translation-scheduling-benchmark window .artifacts\translation-window --reload-before-ocr --analysis-from .artifacts\translation-baseline @pages
```

Se requieren al menos tres páginas, preferiblemente cinco, y directorios de resultados nuevos o vacíos. Ambas ejecuciones descargan inicialmente el modelo de traducción fuera del tramo medido. El límite de ejecución es de treinta minutos y Ollama debe estar disponible en `http://127.0.0.1:11434`. No borra cachés. La primera pasada puede poblarlas mediante el flujo habitual; para comparar las mismas condiciones de OCR, debe existir previamente caché válida para todas las páginas.

`--reload-before-ocr` fuerza la descarga de Gemma antes de cada preparación de página, incluso cuando se reutiliza OCR. Reproduce el coste de alternar motores, que normalmente ocurre en páginas sin caché, manteniendo el OCR constante. **Debe publicarse como medición de coordinación y traducción con OCR en caché y descarga forzada, nunca como medición completa de OCR frío.** Sin esta opción se respeta `HasReusableAnalysisAsync`; consulte los aciertos de caché reales de cada página en `summary.json`. Comparar una primera pasada fría con una segunda en caché no permite atribuir la diferencia al planificador.

El cargador OCR genera identificadores nuevos incluso al leer un manifiesto guardado. `page-NNN-ocr.json` conserva el resultado real y sus identificadores antes de traducir. `page-NNN-input.json` recoge las zonas legibles con el mismo filtro y promoción de lecturas que la aplicación. Al indicar `--analysis-from`, el comprobador exige igualdad estructural del análisis legible con el baseline, ignorando únicamente `Id`; si difiere, falla antes de traducir esa página. Tras esa comprobación traduce una copia de la entrada baseline, con sus identidades originales. Así pueden compararse solicitudes idénticas sin modificar el OCR obtenido ni el código productivo. La equivalencia del OCR completo se puede comprobar por separado en `page-NNN-ocr.json`, ignorando solamente `Id`.

Se guardan los cuerpos exactos de solicitudes y respuestas `api/chat`, las traducciones por página, versión/modelos de Ollama, imágenes y sus SHA-256, progreso y métricas individuales. Compare todos los `request-NNN.json` byte a byte entre modos, y las traducciones completas por página. `summary.json` incluye tiempo total y primera página, OCR, traducción, tokens, carga del modelo y evaluación. La cuenta `observedColdModelLoads` utiliza la ausencia del modelo en `api/ps` inmediatamente antes de una respuesta satisfactoria; `loadsLongerThan5Seconds` es un umbral diagnóstico adicional, no una identificación infalible de recargas. Los fallos de consulta de residencia se declaran por separado. El tiempo total incluye la instrumentación y la escritura de resultados; los tiempos de etapa y de servidor permiten separarlas.

Esta medición no demuestra precisión del 100 % en imágenes no probadas. Un resultado favorable exige entradas OCR equivalentes, peticiones idénticas, misma cobertura de traducción y revisión de cualquier diferencia de salida, además de menor tiempo de ejecución.
