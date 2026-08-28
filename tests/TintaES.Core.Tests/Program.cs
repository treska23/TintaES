using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TintaES.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Sanea cajas gigantes", TestSanitizeAsync),
    ("Rechaza polígonos de bocadillo desmesurados", TestOversizedBubblePolygonAsync),
    ("Aprovecha el interior orgánico del bocadillo", TestDetectedBubbleInteriorAsync),
    ("Combina lecturas solapadas", TestMergeAsync),
    ("Conserva las lecturas OCR alternativas", TestRetainedOcrEvidenceAsync),
    ("Agrupa todas las líneas de un bocadillo", TestWholeBalloonGroupingAsync),
    ("Une un encabezado OCR con su propio bocadillo", TestWholeBalloonHeaderTranslationAsync),
    ("Separa áreas de rotulación que compiten", TestCompetingRenderAreasAsync),
    ("Nunca vuelve a dibujar el OCR inglés como texto de resultado", TestDisplayTextNeverUsesOriginalAsync),
    ("Separa detección y traducción", TestOllamaPipelineAsync),
    ("No desplaza traducciones si TranslateGemma omite una línea", TestTranslateGemmaStableMappingAsync),
    ("Reduce llamadas de TranslateGemma 12B sin perder recuperación", TestTranslateGemma12BThroughputAsync),
    ("Estructura una página completa de TranslateGemma 12B en una llamada", TestTranslateGemma12BStructuredPageAsync),
    ("Recupera individualmente un lote completo sin etiquetas", TestTranslateGemmaWholeBatchRecoveryAsync),
    ("Reintenta traducciones semánticamente incompletas", TestIncompleteTranslationRecoveryAsync),
    ("Nunca incrusta un marcador cuando la traducción falla", TestTranslationFailureNeverRendersMarkerAsync),
    ("Conserva el sentido y el registro español en la escena real", TestComicSceneSemanticGuardsAsync)
};

int failures = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"OK  {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} pruebas superadas");
return failures == 0 ? 0 : 1;

static Task TestSanitizeAsync()
{
    var region = new ComicRegion
    {
        Original = "\"SMASH\"",
        Type = "sfx",
        TextBox = new NormalizedRect(420, 300, 80, 45),
        RenderBox = new NormalizedRect(180, 120, 600, 720),
        Confidence = 2,
        Style = new ComicTextStyle
        {
            FontFamily = "  Impact  ",
            FontWeight = 947,
            FontSize = 500,
            FontWidthRatio = 3,
            LineHeightRatio = 0.2,
            OriginalLineCount = 99
        }
    };
    RegionMerger.Sanitize(region);
    Assert(region.Original == "SMASH", "Debe retirar comillas artificiales.");
    Assert(region.RenderBox.Area < 20_000, "Debe sustituir una caja de panel completo.");
    Assert(region.Confidence == 1, "Debe limitar la confianza.");
    Assert(region.Style.FontFamily == "Impact", "Debe limpiar el nombre de la fuente detectada.");
    Assert(region.Style.FontSize == 250, "Debe limitar tamaños tipográficos imposibles.");
    Assert(region.Style.FontWidthRatio == 1.5, "Debe limitar la anchura tipográfica.");
    Assert(region.Style.LineHeightRatio == 0.8, "Debe limitar el interlineado.");
    Assert(region.Style.OriginalLineCount == 20, "Debe limitar el número de líneas detectado.");
    return Task.CompletedTask;
}

static Task TestOversizedBubblePolygonAsync()
{
    var region = new ComicRegion
    {
        Original = "THIS IS A SPEECH BUBBLE",
        Type = "dialogue",
        TextBox = new NormalizedRect(400, 300, 120, 50),
        RenderBox = new NormalizedRect(100, 50, 700, 650),
        SafePolygon =
        [
            new NormalizedPoint(120, 80),
            new NormalizedPoint(780, 80),
            new NormalizedPoint(780, 680),
            new NormalizedPoint(120, 680)
        ]
    };

    RegionMerger.Sanitize(region);

    Assert(region.RenderBox.Width < 150, "Un polígono enorme no puede ampliar la rotulación por media viñeta.");
    Assert(region.RenderBox.Height < 70, "La altura segura debe mantenerse cerca del texto original.");
    Assert(region.SafePolygon.Count >= 20, "Debe crear una forma elíptica conservadora para el diálogo.");
    return Task.CompletedTask;
}

static Task TestDetectedBubbleInteriorAsync()
{
    NormalizedPoint[] cleanupPolygon =
    [
        new NormalizedPoint(448, 355),
        new NormalizedPoint(510, 355),
        new NormalizedPoint(510, 450),
        new NormalizedPoint(448, 450)
    ];
    var region = new ComicRegion
    {
        Original = "YOU ARE TALKING THE TALK",
        Type = "dialogue",
        BubbleConfidence = 0.98,
        TextBox = new NormalizedRect(450, 360, 55, 82),
        BubbleBox = new NormalizedRect(425, 300, 105, 205),
        RenderBox = new NormalizedRect(432, 340, 92, 122),
        CleanupPolygon = cleanupPolygon
    };

    RegionMerger.Sanitize(region);

    Assert(region.RenderBox.Height >= 155, "Debe usar la altura interior del globo, no una caja pegada al OCR.");
    Assert(region.RenderBox.Width >= 80, "Debe conservar un ancho útil dentro del globo.");
    Assert(region.SafePolygon.Count >= 20, "El área automática debe ser orgánica, no rectangular.");
    Assert(
        region.CleanupPolygon.SequenceEqual(cleanupPolygon),
        "Ampliar la rotulación no debe ampliar ni sustituir el contorno de borrado.");
    Assert(region.RenderBox.Y < region.TextBox.Y, "Debe recuperar el espacio libre superior del bocadillo.");
    Assert(region.RenderBox.Bottom > region.TextBox.Bottom, "Debe recuperar el espacio libre inferior del bocadillo.");
    return Task.CompletedTask;
}

static Task TestMergeAsync()
{
    var first = new ComicRegion
    {
        Original = "HELLO!",
        Confidence = 0.8,
        TextBox = new NormalizedRect(100, 100, 120, 50),
        RenderBox = new NormalizedRect(90, 90, 150, 80)
    };
    var duplicate = new ComicRegion
    {
        Original = "Hello!",
        Confidence = 0.95,
        TextBox = new NormalizedRect(105, 103, 118, 48),
        RenderBox = new NormalizedRect(92, 92, 152, 78)
    };
    var second = new ComicRegion
    {
        Original = "BOOM!",
        Confidence = 0.9,
        TextBox = new NormalizedRect(400, 600, 150, 90),
        RenderBox = new NormalizedRect(390, 590, 170, 110)
    };
    IReadOnlyList<ComicRegion> merged = RegionMerger.Merge([first, duplicate, second]);
    Assert(merged.Count == 2, "Las zonas solapadas del mismo texto deben combinarse.");
    Assert(merged.Any(region => region.Confidence == 0.95), "Debe conservar la lectura más fiable.");
    return Task.CompletedTask;
}

static Task TestRetainedOcrEvidenceAsync()
{
    var partial = new ComicRegion
    {
        Original = "HELLO THER",
        Confidence = 0.99,
        TextBox = new NormalizedRect(100, 100, 120, 50),
        RenderBox = new NormalizedRect(90, 90, 150, 80)
    };
    var complete = new ComicRegion
    {
        Original = "HELLO THERE!",
        Confidence = 0.80,
        TextBox = new NormalizedRect(105, 103, 118, 48),
        RenderBox = new NormalizedRect(92, 92, 152, 78)
    };

    ComicRegion merged = RegionMerger.Merge([partial, complete]).Single();
    Assert(merged.Original == "HELLO THERE!", "Debe conservar la lectura más completa y fiable.");
    Assert(
        merged.StoredOcrAlternatives.Contains("HELLO THER"),
        "Debe retener la lectura descartada como evidencia OCR alternativa.");

    var wholeBalloon = new ComicRegion
    {
        Original = "WULK'S NOT COMING OUT ANY TIME SOON",
        Confidence = 0.90,
        TextBox = new NormalizedRect(470, 370, 125, 44),
        RenderBox = new NormalizedRect(460, 350, 150, 85)
    };
    var damagedTile = new ComicRegion
    {
        Original = "NOT COMIM ANY TIME S",
        Confidence = 0.82,
        TextBox = new NormalizedRect(472, 379, 81, 34),
        RenderBox = new NormalizedRect(465, 365, 100, 60)
    };
    ComicRegion contained = RegionMerger.Merge([wholeBalloon, damagedTile]).Single();
    Assert(
        contained.Original == wholeBalloon.Original
        && contained.StoredOcrAlternatives.Contains(damagedTile.Original),
        "Un fragmento OCR contenido no debe convertirse en otro bocadillo.");
    return Task.CompletedTask;
}

static Task TestCompetingRenderAreasAsync()
{
    var left = new ComicRegion
    {
        Original = "LEFT BALLOON",
        Type = "dialogue",
        TextBox = new NormalizedRect(100, 100, 120, 80),
        RenderBox = new NormalizedRect(70, 75, 190, 125),
        SafePolygon =
        [
            new NormalizedPoint(70, 75),
            new NormalizedPoint(260, 75),
            new NormalizedPoint(260, 200),
            new NormalizedPoint(70, 200)
        ]
    };
    var right = new ComicRegion
    {
        Original = "RIGHT BALLOON",
        Type = "dialogue",
        TextBox = new NormalizedRect(225, 145, 120, 80),
        RenderBox = new NormalizedRect(185, 115, 200, 135),
        SafePolygon =
        [
            new NormalizedPoint(185, 115),
            new NormalizedPoint(385, 115),
            new NormalizedPoint(385, 250),
            new NormalizedPoint(185, 250)
        ]
    };

    RegionMerger.ResolveCompetingRenderAreas([left, right]);

    Assert(left.RenderBox.Right <= right.RenderBox.X + 0.01,
        "Dos bocadillos distintos no pueden compartir área de rotulación.");
    Assert(left.RenderBox.Right < right.RenderBox.X,
        "La separación debe dejar un margen real entre textos vecinos.");
    Assert(left.RenderBox.Right >= left.TextBox.Right - 10,
        "La separación solo puede recortar un margen mínimo del bloque original izquierdo.");
    Assert(right.RenderBox.X <= right.TextBox.X + 10,
        "La separación solo puede recortar un margen mínimo del bloque original derecho.");

    var shallowLeft = new ComicRegion
    {
        Original = "NEAR LEFT",
        Type = "dialogue",
        TextBox = new NormalizedRect(100, 400, 120, 80),
        RenderBox = new NormalizedRect(90, 390, 145, 100)
    };
    var shallowRight = new ComicRegion
    {
        Original = "NEAR RIGHT",
        Type = "dialogue",
        TextBox = new NormalizedRect(225, 402, 120, 80),
        RenderBox = new NormalizedRect(225, 392, 145, 100)
    };
    RegionMerger.ResolveCompetingRenderAreas([shallowLeft, shallowRight]);
    Assert(shallowLeft.RenderBox.Right < shallowRight.RenderBox.X,
        "Incluso un solape estrecho entre bocadillos contiguos debe dejar un canal libre.");
    return Task.CompletedTask;
}

static Task TestDisplayTextNeverUsesOriginalAsync()
{
    var region = new ComicRegion
    {
        Original = "OPEN YOUR EYES",
        Translation = string.Empty
    };

    Assert(region.DisplayText == string.Empty,
        "Una traducción vacía no puede hacer que el lienzo vuelva a dibujar el inglés.");

    region.Translation = "ABRE LOS OJOS";
    Assert(region.DisplayText == "ABRE LOS OJOS",
        "El lienzo debe mostrar exclusivamente la traducción española.");

    region.Translation = ComicRegion.PendingTranslationMarker;
    Assert(region.DisplayText == string.Empty,
        "Un marcador técnico antiguo nunca puede aparecer incrustado en el bocadillo.");
    return Task.CompletedTask;
}

static Task TestWholeBalloonGroupingAsync()
{
    var firstLine = new ComicRegion
    {
        Original = "THIS BALLOON HAS",
        Translation = "ESTE BOCADILLO TIENE",
        Type = "dialogue",
        Confidence = 0.91,
        TextBox = new NormalizedRect(420, 300, 150, 34),
        BubbleBox = new NormalizedRect(370, 250, 260, 230),
        Style = new ComicTextStyle { OriginalLineCount = 1 }
    };
    var secondLine = new ComicRegion
    {
        Original = "SEVERAL OCR LINES",
        Translation = "VARIAS LÍNEAS DE OCR",
        Type = "dialogue",
        Confidence = 0.88,
        TextBox = new NormalizedRect(405, 350, 180, 36),
        BubbleBox = new NormalizedRect(375, 252, 255, 226),
        Style = new ComicTextStyle { OriginalLineCount = 1 }
    };
    var thirdLine = new ComicRegion
    {
        Original = "AND MUST OPEN ONCE",
        Translation = "Y DEBE ABRIRSE DE UNA VEZ",
        Type = "dialogue",
        Confidence = 0.89,
        TextBox = new NormalizedRect(400, 402, 190, 35),
        BubbleBox = new NormalizedRect(372, 249, 258, 231),
        Style = new ComicTextStyle { OriginalLineCount = 1 }
    };
    var nearbyBalloon = new ComicRegion
    {
        Original = "DO NOT MIX ME",
        Translation = "NO ME MEZCLES",
        Type = "dialogue",
        Confidence = 0.95,
        TextBox = new NormalizedRect(690, 345, 150, 44),
        BubbleBox = new NormalizedRect(650, 285, 240, 190)
    };

    IReadOnlyList<ComicRegion> grouped = BalloonRegionGrouper.Group(
        [firstLine, secondLine, thirdLine, nearbyBalloon]);

    Assert(grouped.Count == 2, "Tres líneas de un globo deben producir una única zona.");
    ComicRegion balloon = grouped.Single(region => region.Original.StartsWith("THIS BALLOON"));
    Assert(
        balloon.Original == "THIS BALLOON HAS SEVERAL OCR LINES AND MUST OPEN ONCE",
        "El texto original debe conservar todas las líneas en orden de lectura.");
    Assert(
        balloon.Translation == "ESTE BOCADILLO TIENE VARIAS LÍNEAS DE OCR Y DEBE ABRIRSE DE UNA VEZ",
        "La tarjeta debe mostrar la traducción completa del bocadillo.");
    Assert(balloon.Style.OriginalLineCount >= 3, "Debe conservar el número total de líneas.");
    Assert(
        grouped.Single(region => region.Original == "DO NOT MIX ME").Translation == "NO ME MEZCLES",
        "Un bocadillo cercano no puede mezclarse con el anterior.");

    var leftParallelBalloon = new ComicRegion
    {
        Original = "SERIOUS PIECE OF HARDWARE",
        Translation = "QUÉ PIEZA DE EQUIPO",
        Type = "dialogue",
        TextBox = new NormalizedRect(450, 43, 134, 55),
        BubbleBox = new NormalizedRect(270, 0, 495, 160)
    };
    var rightParallelBalloon = new ComicRegion
    {
        Original = "NO IDEA ABOUT THAT",
        Translation = "NI IDEA DE ESO",
        Type = "dialogue",
        TextBox = new NormalizedRect(613, 43, 87, 72),
        BubbleBox = new NormalizedRect(495, 0, 322, 198)
    };
    IReadOnlyList<ComicRegion> parallel = BalloonRegionGrouper.Group(
        [leftParallelBalloon, rightParallelBalloon]);
    Assert(
        parallel.Count == 2,
        "Dos bocadillos paralelos deben respetar el borde que los separa aunque sus cajas se solapen.");

    const string sharedReading = "WULK'S NOT COMING OUT ANY TIME SOON. MAYBE NEXT TIME, SPIDER-PUNK.";
    var hulkHeader = new ComicRegion
    {
        Original = "4ULKS",
        Translation = "NO, NO ESTOY SEGURO DE ESO",
        Type = "sfx",
        OcrAlternatives = [sharedReading],
        TextBox = new NormalizedRect(513, 370, 40, 7),
        BubbleBox = new NormalizedRect(461, 361, 147, 25)
    };
    var hulkBody = new ComicRegion
    {
        Original = "NOT COMING OUT ANY TIME SOON MAYBE NEXT TIME SPIDER-PUNK.",
        Translation = "HULK NO VA A SALIR PRONTO",
        Type = "dialogue",
        OcrAlternatives = [sharedReading],
        TextBox = new NormalizedRect(474, 377, 116, 37),
        BubbleBox = new NormalizedRect(460, 335, 156, 89)
    };
    IReadOnlyList<ComicRegion> hulkBalloon = BalloonRegionGrouper.Group([hulkBody, hulkHeader]);
    Assert(hulkBalloon.Count == 1,
        "HULK'S y el resto de su texto deben ser una sola zona pulsable.");
    Assert(hulkBalloon[0].Type == "dialogue",
        "Un encabezado mal clasificado como SFX debe heredar el tipo del bocadillo.");
    Assert(hulkBalloon[0].Original.StartsWith("4ULKS NOT COMING", StringComparison.Ordinal),
        "El encabezado debe quedar delante del resto del bocadillo.");
    Assert(!hulkBalloon[0].HasRenderableTranslation,
        "Una traducción antigua de la microzona debe descartarse y recalcularse como bocadillo completo.");

    secondLine.Translation = string.Empty;
    IReadOnlyList<ComicRegion> incomplete = BalloonRegionGrouper.Group([firstLine, secondLine]);
    Assert(
        !incomplete[0].HasRenderableTranslation,
        "Si falta una línea, el bocadillo entero debe quedar pendiente para retraducirse.");
    return Task.CompletedTask;
}

static async Task TestWholeBalloonHeaderTranslationAsync()
{
    const string sharedReading = "WULK'S NOT COMING OUT ANY TIME SOON. MAYBE NEXT TIME, SPIDER-PUNK.";
    IReadOnlyList<ComicRegion> grouped = BalloonRegionGrouper.Group(
    [
        new ComicRegion
        {
            Original = "4ULKS",
            Type = "sfx",
            OcrAlternatives = [sharedReading],
            TextBox = new NormalizedRect(513, 370, 40, 7),
            BubbleBox = new NormalizedRect(461, 361, 147, 25)
        },
        new ComicRegion
        {
            Original = "NOT COMING OUT ANY TIME SOON MAYBE NEXT TIME SPIDER-PUNK.",
            Type = "dialogue",
            OcrAlternatives = [sharedReading],
            TextBox = new NormalizedRect(474, 377, 116, 37),
            BubbleBox = new NormalizedRect(460, 335, 156, 89)
        }
    ]);

    var handler = new FakeTranslateGemmaHandler();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    await client.TranslateRegionsAsync(grouped, "translategemma:4b", CancellationToken.None);

    Assert(handler.Prompts.Any(prompt => prompt.Contains(
            "HULK'S NOT COMING OUT ANY TIME SOON",
            StringComparison.Ordinal)),
        "El traductor debe recibir HULK'S reparado y el bocadillo completo, no la microzona 4ULKS.");
    Assert(grouped[0].HasRenderableTranslation,
        "El bocadillo completo debe recibir una única traducción.");
}

static async Task TestOllamaPipelineAsync()
{
    var handler = new FakeOllamaHandler();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    var tile = new ComicImageTile(1, 1, 0, 0, 1000, 1000, 1000, 1000, [1, 2, 3, 4]);
    ComicAnalysis result = await client.AnalyzePageAsync([tile], "qwen3.5:9b");
    Assert(result.Regions.Count == 1, "Debe devolver la región detectada.");
    Assert(result.Regions[0].Translation == "¡CRASH!", "La traducción separada no puede quedar vacía.");
    Assert(result.Regions[0].RenderBox.Area < 20_000, "Debe corregir la caja gigante del detector.");
    Assert(result.Regions[0].Style.FontFamily == "Impact", "Debe conservar la familia tipográfica detectada.");
    Assert(Math.Abs(result.Regions[0].Style.FontSize - 52) < 0.01, "Debe convertir el tamaño detectado a coordenadas de página.");
    Assert(result.Regions[0].Style.OriginalLineCount == 1, "Debe conservar el número de líneas original.");
    Assert(handler.VisionCalls == 1 && handler.TranslationCalls == 1, "Debe realizar detección y traducción por separado.");
}

static async Task TestTranslateGemmaStableMappingAsync()
{
    var handler = new FakeTranslateGemmaHandler();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    ComicRegion[] regions =
    [
        new() { Original = "FIRST LINE", Type = "dialogue" },
        new() { Original = "SECOND LINE", Type = "dialogue" },
        new() { Original = "THIRD LINE", Type = "dialogue" }
    ];

    await client.TranslateRegionsAsync(regions, "translategemma:4b", CancellationToken.None);

    Assert(regions[0].Translation == "Primera frase", "La primera traducción debe conservar su región.");
    Assert(regions[1].Translation == "Segunda frase", "La línea omitida debe repetirse de forma aislada.");
    Assert(regions[2].Translation == "Tercera frase", "La tercera traducción no puede desplazarse a la segunda región.");
    Assert(handler.Calls == 2, "Debe hacer un lote y un único reintento para la línea omitida.");
}

static async Task TestTranslateGemma12BThroughputAsync()
{
    var handler = new FakeTranslateGemmaHandler();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    ComicRegion[] regions = Enumerable.Range(1, 12)
        .Select(index => new ComicRegion
        {
            Original = $"COMIC LINE {index}",
            Type = "dialogue"
        })
        .ToArray();

    await client.TranslateRegionsAsync(regions, "translategemma:12b", CancellationToken.None);

    Assert(regions.All(region => region.HasRenderableTranslation),
        "El lote rápido de 12B debe conservar todas las traducciones válidas.");
    Assert(handler.Calls == 2,
        "Doce bocadillos con una omisión deben resolverse en un lote inicial y un reintento, no en dos lotes completos.");
}

static async Task TestTranslateGemma12BStructuredPageAsync()
{
    var handler = new FakeStructuredTranslateGemmaHandler();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    ComicRegion[] regions = Enumerable.Range(1, 20)
        .Select(index => new ComicRegion
        {
            Original = $"COMIC DIALOGUE LINE {index}",
            Type = "dialogue"
        })
        .ToArray();

    await client.TranslateRegionsAsync(regions, "translategemma:12b", CancellationToken.None);

    Assert(handler.Calls == 1,
        "Una página normal de veinte bocadillos debe traducirse en una sola inferencia estructurada.");
    Assert(handler.ReceivedStrictFormat,
        "Ollama debe recibir un esquema que exija una clave distinta para cada bocadillo.");
    Assert(handler.PredictionBudget < 20 * 110,
        "El presupuesto de salida debe depender del texto real y no del máximo antiguo por bocadillo.");
    Assert(regions.All(region => region.HasRenderableTranslation),
        "La salida estructurada debe conservar una traducción válida y estable por región.");
}

static async Task TestTranslateGemmaWholeBatchRecoveryAsync()
{
    var handler = new FakeTranslateGemmaWholeBatchHandler(failEveryRequest: false);
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    ComicRegion[] regions =
    [
        new() { Original = "FIRST LINE", Type = "dialogue" },
        new() { Original = "SECOND LINE", Type = "dialogue" },
        new() { Original = "THIRD LINE", Type = "dialogue" }
    ];

    await client.TranslateRegionsAsync(regions, "translategemma:4b", CancellationToken.None);

    Assert(regions.All(region => region.HasRenderableTranslation),
        "Cada bocadillo debe recuperarse aunque el modelo pierda todas las etiquetas del lote.");
    Assert(regions[0].Translation == "Primera frase", "La recuperación individual debe respetar la primera zona.");
    Assert(regions[1].Translation == "Segunda frase", "La recuperación individual debe respetar la segunda zona.");
    Assert(regions[2].Translation == "Tercera frase", "La recuperación individual debe respetar la tercera zona.");
    Assert(handler.Calls == 5,
        "Tras dos lotes inválidos debe realizar exactamente un reintento individual por zona.");
}

static async Task TestTranslationFailureNeverRendersMarkerAsync()
{
    var handler = new FakeTranslateGemmaWholeBatchHandler(failEveryRequest: true);
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    ComicRegion[] regions =
    [
        new() { Original = "FIRST LINE", Type = "dialogue" },
        new() { Original = "SECOND LINE", Type = "dialogue" }
    ];

    bool failed = false;
    try
    {
        await client.TranslateRegionsAsync(regions, "translategemma:4b", CancellationToken.None);
    }
    catch (InvalidOperationException exception)
    {
        failed = exception is IncompleteTranslationException
                 && exception.Message.Contains("no devolvió", StringComparison.OrdinalIgnoreCase);
    }

    Assert(failed, "Una traducción incompleta debe fallar y dejar la página reintentable.");
    Assert(regions.All(region => string.IsNullOrEmpty(region.Translation)),
        "Las zonas fallidas deben quedar vacías, sin texto inglés ni marcadores.");
    Assert(regions.All(region => region.DisplayText == string.Empty),
        "El lienzo nunca debe dibujar un aviso técnico como si fuera rotulación.");
}

static async Task TestIncompleteTranslationRecoveryAsync()
{
    var handler = new FakeIncompleteTranslationHandler();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    ComicRegion[] regions =
    [
        new()
        {
            Original = "WOULDN'T YOU LIKE TO ASK KRAVEN ABOUT THIS PERSONALLY?",
            Type = "dialogue"
        }
    ];

    await client.TranslateRegionsAsync(regions, "translategemma:4b", CancellationToken.None);

    Assert(handler.Calls == 3,
        "Una respuesta demasiado corta y sin interrogación debe llegar al reintento individual.");
    Assert(regions[0].Translation == "¿No te gustaría preguntárselo personalmente a Kraven?",
        "El reintento debe conservar pregunta, negación, intención y nombre propio.");
}

static async Task TestComicSceneSemanticGuardsAsync()
{
    var handler = new FakeTranslateGemmaSemanticHandler();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    ComicRegion[] regions =
    [
        new() { Original = "FIRE- BALLS, GIRLS", Type = "dialogue" },
        new() { Original = "LET ME TELL YOU WHAT IT'S ALL ABOUT", Type = "dialogue" },
        new() { Original = "TAKE THESE SUCKERS OUT", Type = "dialogue" },
        new()
        {
            Original = "4ULKS NOT COMING OUT ANY TIME SOON MAYBE NEXT TIME SPIDER-PUNK.",
            Type = "dialogue"
        }
    ];

    await client.TranslateRegionsAsync(regions, "translategemma:4b", CancellationToken.None);

    Assert(regions[0].Translation == "¡Bolas de fuego, chicas!",
        "Fireballs debe conservar el plural y la llamada a las chicas.");
    Assert(regions[1].Translation == "Dejad que os cuente de qué va todo esto.",
        "El diálogo debe usar un registro natural de España.");
    Assert(regions[2].Translation == "¡Acabad con esos capullos!",
        "La orden de combate no puede convertirse en una palabra inventada.");
    Assert(regions[3].Translation == "Hulk no va a salir pronto. Quizá la próxima vez, «Spider-Punk».",
        "HULK'S debe pertenecer al bocadillo completo y conservar su sentido en español.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeOllamaHandler : HttpMessageHandler
{
    public int VisionCalls { get; private set; }
    public int TranslationCalls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            return Json(new { models = new[] { new { name = "qwen3.5:9b", size = 123L } } });
        }

        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument requestDocument = JsonDocument.Parse(body);
        string prompt = requestDocument.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        bool hasImages = requestDocument.RootElement.GetProperty("messages")[0].TryGetProperty("images", out _);
        if (hasImages)
        {
            VisionCalls++;
            string detection = JsonSerializer.Serialize(new
            {
                source_language = "en",
                regions = new[]
                {
                    new
                    {
                        original = "\"SMASH\"",
                        type = "sfx",
                        confidence = 0.98,
                        text_box = new { x = 420, y = 300, width = 80, height = 45 },
                        render_box = new { x = 180, y = 120, width = 600, height = 720 },
                        rotation = 0,
                        vertical = false,
                        style = new
                        {
                            font_category = "display",
                            font_family = "Impact",
                            font_weight = 900,
                            font_size = 52,
                            font_width_ratio = 0.88,
                            line_height_ratio = 1.02,
                            line_count = 1,
                            italic = false,
                            uppercase = true,
                            text_color = "#111111",
                            outline_color = (string?)null,
                            outline_width = 0,
                            alignment = "center",
                            background_color = "#FFFFFF",
                            shadow = false
                        }
                    }
                }
            });
            return Json(new { message = new { content = detection } });
        }

        TranslationCalls++;
        int marker = prompt.IndexOf("ELEMENTOS:", StringComparison.Ordinal);
        using JsonDocument items = JsonDocument.Parse(prompt[(marker + "ELEMENTOS:".Length)..].Trim());
        string id = items.RootElement[0].GetProperty("id").GetString()!;
        string translation = JsonSerializer.Serialize(new
        {
            translations = new[] { new { id, translation = "¡CRASH!" } }
        });
        return Json(new { message = new { content = translation } });
    }

    private static HttpResponseMessage Json(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}

sealed class FakeStructuredTranslateGemmaHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    public bool ReceivedStrictFormat { get; private set; }
    public int PredictionBudget { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument requestDocument = JsonDocument.Parse(body);
        JsonElement root = requestDocument.RootElement;
        PredictionBudget = root.GetProperty("options").GetProperty("num_predict").GetInt32();
        string prompt = root
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString()!;
        Match[] targets = Regex.Matches(
                prompt,
                @"\[\[(R[A-F0-9]+)\]\]\s*(.*?)\s*\[\[/\1\]\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();

        JsonElement translationsSchema = root
            .GetProperty("format")
            .GetProperty("properties")
            .GetProperty("translations");
        string[] required = translationsSchema
            .GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        string[] properties = translationsSchema
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        string[] tokens = targets.Select(target => target.Groups[1].Value).ToArray();
        ReceivedStrictFormat = translationsSchema.GetProperty("additionalProperties").ValueKind
                                   == JsonValueKind.False
                               && required.Order().SequenceEqual(tokens.Order())
                               && properties.Order().SequenceEqual(tokens.Order());

        var translations = new Dictionary<string, string>();
        for (int index = 0; index < tokens.Length; index++)
        {
            translations[tokens[index]] = $"Diálogo traducido {index + 1}";
        }
        string content = JsonSerializer.Serialize(new { translations });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { message = new { content } }),
                Encoding.UTF8,
                "application/json")
        };
    }
}

sealed class FakeTranslateGemmaHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    public List<string> Prompts { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument requestDocument = JsonDocument.Parse(body);
        string prompt = requestDocument.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString()!;
        Prompts.Add(prompt);
        Match[] targets = Regex.Matches(
                prompt,
                @"\[\[(R[A-F0-9]+)\]\]\s*(.*?)\s*\[\[/\1\]\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();

        IEnumerable<Match> returned = Calls == 1 && targets.Length > 1
            ? targets.Where((_, index) => index != 1)
            : targets;
        string content = string.Join(
            "\n",
            returned.Select(match =>
            {
                string token = match.Groups[1].Value;
                string source = match.Groups[2].Value;
                string translation = source.Contains("FIRST", StringComparison.Ordinal)
                    ? "Primera frase"
                    : source.Contains("SECOND", StringComparison.Ordinal)
                        ? "Segunda frase"
                        : "Tercera frase";
                return $"[[{token}]] {translation} [[/{token}]]";
            }));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { message = new { content } }),
                Encoding.UTF8,
                "application/json")
        };
    }
}

sealed class FakeTranslateGemmaWholeBatchHandler(bool failEveryRequest) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument requestDocument = JsonDocument.Parse(body);
        string prompt = requestDocument.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString()!;
        Match[] targets = Regex.Matches(
                prompt,
                @"\[\[(R[A-F0-9]+)\]\]\s*(.*?)\s*\[\[/\1\]\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();

        string content = string.Empty;
        if (!failEveryRequest && targets.Length == 1)
        {
            string source = targets[0].Groups[2].Value;
            content = source.Contains("FIRST", StringComparison.Ordinal)
                ? "Primera frase"
                : source.Contains("SECOND", StringComparison.Ordinal)
                    ? "Segunda frase"
                    : "Tercera frase";
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { message = new { content } }),
                Encoding.UTF8,
                "application/json")
        };
    }
}

sealed class FakeIncompleteTranslationHandler : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument requestDocument = JsonDocument.Parse(body);
        string prompt = requestDocument.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString()!;
        Match target = Regex.Match(
            prompt,
            @"\[\[(R[A-F0-9]+)\]\]\s*(.*?)\s*\[\[/\1\]\]",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        string candidate = Calls < 3
            ? "De acuerdo, equipo."
            : "¿No te gustaría preguntárselo personalmente a Kraven?";
        string content = target.Success
            ? $"[[{target.Groups[1].Value}]] {candidate} [[/{target.Groups[1].Value}]]"
            : candidate;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { message = new { content } }),
                Encoding.UTF8,
                "application/json")
        };
    }
}

sealed class FakeTranslateGemmaSemanticHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument requestDocument = JsonDocument.Parse(body);
        string prompt = requestDocument.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString()!;
        Match[] targets = Regex.Matches(
                prompt,
                @"\[\[(R[A-F0-9]+)\]\]\s*(.*?)\s*\[\[/\1\]\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        string content = string.Join(
            "\n",
            targets.Select(match =>
                $"[[{match.Groups[1].Value}]] Texto provisional [[/{match.Groups[1].Value}]]"));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { message = new { content } }),
                Encoding.UTF8,
                "application/json")
        };
    }
}
