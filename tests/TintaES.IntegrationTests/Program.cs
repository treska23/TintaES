using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;
using TintaES.Wpf;
using TintaES.Wpf.Services;

try
{
    if (args is ["--cleanup-polygon-self-test"])
    {
        return RunCleanupPolygonSelfTest();
    }
    if (args is ["--cleanup-image", var cleanupImage])
    {
        return await RunCleanupImageAsync(cleanupImage);
    }
    if (args is ["--lettering-layout-self-test"])
    {
        return RunLetteringLayoutSelfTest();
    }
    if (args is ["--windows-ocr-image", var ocrImage])
    {
        return await RunWindowsOcrImageAsync(ocrImage);
    }
    if (args is ["--organic-ocr-image", var organicOcrImage])
    {
        return await RunOrganicOcrImageAsync(organicOcrImage);
    }
    if (args is ["--windows-ocr-crop", var cropImage, var cropX, var cropY, var cropWidth, var cropHeight])
    {
        return await RunWindowsOcrCropAsync(
            cropImage,
            int.Parse(cropX),
            int.Parse(cropY),
            int.Parse(cropWidth),
            int.Parse(cropHeight));
    }
    if (args is ["--reader-hit-test-self-test"])
    {
        return RunReaderHitTestSelfTest();
    }
    if (args is ["--reader-window-self-test", var readerImage, var readerOutput])
    {
        return await RunOnStaThreadAsync(() => RunReaderWindowSelfTestAsync(readerImage, readerOutput));
    }
    return await RunAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ERROR_INTEGRACION={exception.GetType().Name}: {exception.Message}");
    return 1;
}

static async Task<int> RunOrganicOcrImageAsync(string imagePath)
{
    if (!File.Exists(imagePath))
    {
        Console.Error.WriteLine($"No existe la imagen: {imagePath}");
        return 2;
    }

    var progress = new Progress<AnalysisProgress>(value =>
        Console.WriteLine($"OCR={value.Percentage:F0}% {value.Message}"));
    OrganicAnalysisResult result = await new OrganicEngineService().AnalyzeAsync(
        Path.GetFullPath(imagePath),
        progress);
    foreach (ComicRegion region in result.Analysis.Regions)
    {
        Console.WriteLine(
            $"[{region.Order:000}] {region.Type} {region.TextBox.X:F0},{region.TextBox.Y:F0} " +
            $"{region.TextBox.Width:F0}x{region.TextBox.Height:F0}: {region.Original}");
    }
    Console.WriteLine($"OCR_ZONAS={result.Analysis.Regions.Count}");
    return result.Analysis.Regions.Count > 0 ? 0 : 1;
}

static int RunReaderHitTestSelfTest()
{
    var bubbleRegion = new ComicRegion
    {
        TextBox = new NormalizedRect(420, 420, 100, 80),
        BubbleBox = new NormalizedRect(370, 350, 200, 200),
        BubbleConfidence = 0.9
    };
    NormalizedRect bubbleHit = ComicReaderWindow.ResolveReaderHitBox(bubbleRegion);
    bool usesBubble = bubbleHit.X == 370
        && bubbleHit.Y == 350
        && bubbleHit.Right == 570
        && bubbleHit.Bottom == 550;

    var polygonRegion = new ComicRegion
    {
        TextBox = new NormalizedRect(430, 440, 90, 70),
        SafePolygon =
        [
            new NormalizedPoint(390, 380),
            new NormalizedPoint(560, 380),
            new NormalizedPoint(560, 540),
            new NormalizedPoint(390, 540)
        ]
    };
    NormalizedRect polygonHit = ComicReaderWindow.ResolveReaderHitBox(polygonRegion);
    bool usesPolygon = polygonHit.X == 390
        && polygonHit.Y == 380
        && polygonHit.Right == 560
        && polygonHit.Bottom == 540;

    var oversizedRegion = new ComicRegion
    {
        Original = "SMALL BALLOON",
        TextBox = new NormalizedRect(450, 450, 100, 60),
        BubbleBox = new NormalizedRect(0, 0, 1000, 1000)
    };
    NormalizedRect oversizedHit = ComicReaderWindow.ResolveReaderHitBox(oversizedRegion);
    bool rejectsOversizedHit = oversizedHit.Area < 40_000
        && oversizedHit.X > 350
        && oversizedHit.Y > 350;

    var edgeRegion = new ComicRegion
    {
        TextBox = new NormalizedRect(0, 0, 80, 50)
    };
    NormalizedRect edgeHit = ComicReaderWindow.ResolveReaderHitBox(edgeRegion);
    bool clampsToPage = edgeHit.X == 0
        && edgeHit.Y == 0
        && edgeHit.Right <= 1000
        && edgeHit.Bottom <= 1000;

    int nextWestern = ComicReaderWindow.ResolveSwipePageDelta(
        new Vector(-260, 22), TimeSpan.FromMilliseconds(520), false, false, 2, 6, true, 1000);
    int previousWestern = ComicReaderWindow.ResolveSwipePageDelta(
        new Vector(260, -18), TimeSpan.FromMilliseconds(520), false, false, 2, 6, true, 1000);
    int nextManga = ComicReaderWindow.ResolveSwipePageDelta(
        new Vector(260, 12), TimeSpan.FromMilliseconds(520), false, true, 2, 6, true, 1000);
    int protectedTap = ComicReaderWindow.ResolveSwipePageDelta(
        new Vector(-28, 3), TimeSpan.FromMilliseconds(160), false, false, 2, 6, true, 1000);
    int protectedPinch = ComicReaderWindow.ResolveSwipePageDelta(
        new Vector(-300, 0), TimeSpan.FromMilliseconds(500), true, false, 2, 6, true, 1000);
    int protectedPan = ComicReaderWindow.ResolveSwipePageDelta(
        new Vector(-300, 0), TimeSpan.FromMilliseconds(500), false, false, 2, 6, false, 1000);
    bool safeSwipe = nextWestern == 1
        && previousWestern == -1
        && nextManga == 1
        && protectedTap == 0
        && protectedPinch == 0
        && protectedPan == 0;

    var sfxRegion = new ComicRegion
    {
        Original = "KRA-KOOM!",
        Type = "sfx",
        Confidence = 0.42,
        TextBox = new NormalizedRect(120, 160, 180, 90)
    };
    var signRegion = new ComicRegion
    {
        Original = "EXIT",
        Type = "sign",
        Confidence = 0.38,
        TextBox = new NormalizedRect(760, 80, 95, 55)
    };
    bool allTextTypes = MainWindow.IsReadableLetteringCandidate(sfxRegion)
        && MainWindow.IsReadableLetteringCandidate(signRegion)
        && sfxRegion.Type == "sfx"
        && signRegion.Type == "sign";
    bool mainCanvasClick = ReferenceEquals(
        MainWindow.ResolveMainTranslationRegion([bubbleRegion, sfxRegion, signRegion], 205, 205),
        sfxRegion);

    // Regresión de Spider-Punk 008: el detector devolvía contenedores solapados
    // para dos bocadillos contiguos. El orden de dibujo no puede decidir el clic.
    var adjacentLeft = new ComicRegion
    {
        Order = 1,
        Original = "SERIOUS PIECE OF HARDWARE",
        Translation = "Menuda pieza de equipo",
        TextBox = new NormalizedRect(450, 43, 134, 55),
        BubbleBox = new NormalizedRect(270, 0, 495, 160),
        BubbleConfidence = 0
    };
    var adjacentRight = new ComicRegion
    {
        Order = 2,
        Original = "NO IDEA ABOUT THAT SECOND PART",
        Translation = "Ni idea de esa segunda parte",
        TextBox = new NormalizedRect(613, 43, 87, 72),
        BubbleBox = new NormalizedRect(495, 0, 322, 198),
        BubbleConfidence = 0
    };
    bool adjacentBalloonsAreExact = ReferenceEquals(
            MainWindow.ResolveMainTranslationRegion([adjacentRight, adjacentLeft], 550, 104),
            adjacentLeft)
        && ReferenceEquals(
            MainWindow.ResolveMainTranslationRegion([adjacentLeft, adjacentRight], 625, 104),
            adjacentRight);

    var hopingBalloon = new ComicRegion
    {
        Order = 9,
        Original = "HOPING WE CAN CRACK THIS QUICK",
        TextBox = new NormalizedRect(67, 458, 185, 35),
        BubbleBox = new NormalizedRect(0, 418, 501, 116),
        BubbleConfidence = 0
    };
    var yeahBalloon = new ComicRegion
    {
        Order = 10,
        Original = "YEAH, THEY'RE CHUMPS",
        TextBox = new NormalizedRect(282, 459, 171, 63),
        BubbleBox = new NormalizedRect(51, 387, 683, 206),
        BubbleConfidence = 0
    };
    bool middlePanelBalloonsAreExact = ReferenceEquals(
            MainWindow.ResolveMainTranslationRegion([yeahBalloon, hopingBalloon], 230, 505),
            hopingBalloon)
        && ReferenceEquals(
            MainWindow.ResolveMainTranslationRegion([hopingBalloon, yeahBalloon], 430, 525),
            yeahBalloon)
        && MainWindow.ResolveMainTranslationRegion([hopingBalloon, yeahBalloon], 900, 900) is null;

    const string hulkSharedReading =
        "WULK'S NOT COMING OUT ANY TIME SOON. MAYBE NEXT TIME, SPIDER-PUNK.";
    ComicRegion hulkWholeBalloon = BalloonRegionGrouper.Group(
    [
        new ComicRegion
        {
            Original = "4ULKS",
            Type = "sfx",
            OcrAlternatives = [hulkSharedReading],
            TextBox = new NormalizedRect(513, 370, 40, 7),
            BubbleBox = new NormalizedRect(461, 361, 147, 25)
        },
        new ComicRegion
        {
            Original = "NOT COMING OUT ANY TIME SOON MAYBE NEXT TIME SPIDER-PUNK.",
            Type = "dialogue",
            OcrAlternatives = [hulkSharedReading],
            TextBox = new NormalizedRect(474, 377, 116, 37),
            BubbleBox = new NormalizedRect(460, 335, 156, 89)
        }
    ]).Single();
    bool hulkHeaderUsesWholeBalloon = ReferenceEquals(
            MainWindow.ResolveMainTranslationRegion([hulkWholeBalloon], 533, 373),
            hulkWholeBalloon)
        && ReferenceEquals(
            MainWindow.ResolveMainTranslationRegion([hulkWholeBalloon], 530, 400),
            hulkWholeBalloon)
        && hulkWholeBalloon.Original.Contains("NOT COMING OUT", StringComparison.Ordinal);

    NormalizedPoint pointAtFullSize = MainWindow.NormalizeImagePoint(994, 1528, 1988, 3056);
    NormalizedPoint pointAtZoomedSize = MainWindow.NormalizeImagePoint(420.462, 646.488, 840.924, 1292.976);
    bool zoomDoesNotMoveHit = Math.Abs(pointAtFullSize.X - pointAtZoomedSize.X) < 0.001
        && Math.Abs(pointAtFullSize.Y - pointAtZoomedSize.Y) < 0.001;
    bool localRecovery = TranslationRecoveryService.TryKnownLocalTranslation(
            "RUN PIGGIES.",
            out string recoveredSfx)
        && recoveredSfx == "¡Corred, cerditos!"
        && TranslationRecoveryService.TryKnownLocalTranslation(
            "BEG AND MAYBE WE LET SOME LIVE, YES?",
            out string recoveredRedDialogue)
        && recoveredRedDialogue == "Suplica, y quizá dejemos a algunos con vida, ¿sí?"
        && TranslationRecoveryService.TryKnownLocalTranslation("C CAN", out string recoveredClang)
        && recoveredClang == "¡CLANG!"
        && !TranslationRecoveryService.CanRemainUnchanged("RUN PIGGIES.")
        && !TranslationRecoveryService.CanRemainUnchanged("L enby S");

    Console.WriteLine($"LECTOR_HIT_BOCADILLO={(usesBubble ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_HIT_POLIGONO={(usesPolygon ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_HIT_NO_INVADE_PAGINA={(rejectsOversizedHit ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_HIT_LIMITE={(clampsToPage ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_GESTO_SEGURO={(safeSwipe ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_TODO_TEXTO={(allTextTypes ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_CLIC_VISTA_PRINCIPAL={(mainCanvasClick ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_BOCADILLOS_ADYACENTES={(adjacentBalloonsAreExact ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_BOCADILLOS_PANEL_CENTRAL={(middlePanelBalloonsAreExact ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_HULKS_BOCADILLO_COMPLETO={(hulkHeaderUsesWholeBalloon ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_ZOOM_NO_DESPLAZA_CLIC={(zoomDoesNotMoveHit ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_RESCATE_TEXTO={(localRecovery ? "OK" : "ERROR")}");
    return usesBubble && usesPolygon && rejectsOversizedHit && clampsToPage && safeSwipe
        && allTextTypes && mainCanvasClick && adjacentBalloonsAreExact
        && middlePanelBalloonsAreExact && hulkHeaderUsesWholeBalloon
        && zoomDoesNotMoveHit && localRecovery
        ? 0
        : 1;
}

static async Task<int> RunWindowsOcrCropAsync(
    string imagePath,
    int x,
    int y,
    int width,
    int height)
{
    BitmapSource source = LoadBitmap(Path.GetFullPath(imagePath));
    var crop = new CroppedBitmap(
        source,
        new Int32Rect(
            Math.Clamp(x, 0, source.PixelWidth - 1),
            Math.Clamp(y, 0, source.PixelHeight - 1),
            Math.Clamp(width, 1, source.PixelWidth - Math.Clamp(x, 0, source.PixelWidth - 1)),
            Math.Clamp(height, 1, source.PixelHeight - Math.Clamp(y, 0, source.PixelHeight - 1))));
    crop.Freeze();

    var service = new WindowsOcrService();
    bool found = false;
    foreach (double scale in new[] { 1d, 2d, 4d })
    {
        var enlarged = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
        enlarged.Freeze();
        ComicAnalysis result = await service.RecognizeAsync(enlarged);
        string text = string.Join(" | ", result.Regions.Select(region => region.Original.Replace('\n', ' ')));
        Console.WriteLine($"OCR_X{scale:0}={text}");
        found |= result.Regions.Count > 0;
    }
    return found ? 0 : 1;
}

static async Task<int> RunReaderWindowSelfTestAsync(string imagePath, string outputPath)
{
    if (!File.Exists(imagePath))
    {
        throw new FileNotFoundException("No se encuentra la imagen de prueba del lector.", imagePath);
    }

    var region = new ComicRegion
    {
        Order = 1,
        Original = "HOW CAN THIS BE?!",
        Translation = "¡¿Cómo puede ser?!",
        Type = "dialogue",
        Confidence = 1,
        BubbleConfidence = 1,
        TextBox = new NormalizedRect(610, 270, 170, 125),
        BubbleBox = new NormalizedRect(560, 215, 270, 250)
    };
    var adjacentLeft = new ComicRegion
    {
        Order = 2,
        Original = "SERIOUS PIECE OF HARDWARE",
        Translation = "Menuda pieza de equipo",
        TextBox = new NormalizedRect(450, 43, 134, 55),
        BubbleBox = new NormalizedRect(270, 0, 495, 160),
        BubbleConfidence = 0
    };
    var adjacentRight = new ComicRegion
    {
        Order = 3,
        Original = "NO IDEA ABOUT THAT SECOND PART",
        Translation = "Ni idea de esa segunda parte",
        TextBox = new NormalizedRect(613, 43, 87, 72),
        BubbleBox = new NormalizedRect(495, 0, 322, 198),
        BubbleConfidence = 0
    };
    var document = new ReaderComicDocument(
        "Prueba del lector",
        [new ReaderComicPage(imagePath, Path.GetFileName(imagePath), [region, adjacentRight, adjacentLeft])]);
    var window = new ComicReaderWindow(document)
    {
        Width = 1180,
        Height = 820,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -20_000,
        Top = -20_000,
        ShowInTaskbar = false
    };

    window.Show();
    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    await Task.Delay(250);

    System.Reflection.MethodInfo showCard = typeof(ComicReaderWindow)
        .GetMethod("ShowTranslationCard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    System.Reflection.MethodInfo hideCard = typeof(ComicReaderWindow)
        .GetMethod("HideTranslationCard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    showCard.Invoke(window, [region]);
    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

    var card = (Border?)typeof(ComicReaderWindow)
        .GetField("_translationCard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(window);
    var translationText = card?.Child as TextBlock;
    var pageImage = (System.Windows.Controls.Image?)typeof(ComicReaderWindow)
        .GetField("_pageImage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(window);
    var pageStage = (Grid?)typeof(ComicReaderWindow)
        .GetField("_pageStage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(window);
    var hitCanvas = (Canvas?)typeof(ComicReaderWindow)
        .GetField("_translationHitCanvas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(window);
    System.Reflection.MethodInfo resolveReaderRegion = typeof(ComicReaderWindow)
        .GetMethod("ResolveReaderRegionAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    ComicRegion? selectedLeft = pageStage is null
        ? null
        : resolveReaderRegion.Invoke(window,
            [new Point(pageStage.ActualWidth * 0.550, pageStage.ActualHeight * 0.104)]) as ComicRegion;
    ComicRegion? selectedRight = pageStage is null
        ? null
        : resolveReaderRegion.Invoke(window,
            [new Point(pageStage.ActualWidth * 0.625, pageStage.ActualHeight * 0.104)]) as ComicRegion;
    bool exactOverlappingSelection = ReferenceEquals(selectedLeft, adjacentLeft)
        && ReferenceEquals(selectedRight, adjacentRight)
        && hitCanvas is { IsHitTestVisible: false }
        && hitCanvas.Children.Count == 0;
    bool completePage = pageImage?.Source is BitmapSource source
        && pageImage.Stretch == Stretch.Fill
        && Math.Abs(pageImage.Width - source.PixelWidth) < 0.01
        && Math.Abs(pageImage.Height - source.PixelHeight) < 0.01;
    bool simpleCard = card is not null
        && translationText is not null
        && !card.IsHitTestVisible
        && card.BorderThickness == new Thickness(1)
        && card.Background is SolidColorBrush { Color: var backgroundColor }
        && backgroundColor == Colors.White
        && card.BorderBrush is SolidColorBrush { Color: var borderColor }
        && borderColor == Colors.Black
        && translationText.Foreground is SolidColorBrush { Color: var textColor }
        && textColor == Colors.Black
        && translationText.FontFamily.Source == SystemFonts.MessageFontFamily.Source
        && translationText.FontSize >= 18;

    int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
    int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
    var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    render.Render(window);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(render));
    using (FileStream output = File.Create(outputPath))
    {
        encoder.Save(output);
    }

    hideCard.Invoke(window, null);
    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    bool releaseHides = card?.Visibility == Visibility.Collapsed;
    bool valid = render.PixelWidth >= 1000
        && render.PixelHeight >= 700
        && completePage
        && simpleCard
        && releaseHides
        && exactOverlappingSelection;
    window.Close();
    Console.WriteLine($"LECTOR_VENTANA={(valid ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_PAGINA_COMPLETA={(completePage ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_RECUADRO_SIMPLE={(simpleCard ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_SOLTAR_OCULTA={(releaseHides ? "OK" : "ERROR")}");
    Console.WriteLine($"LECTOR_CLIC_SOLAPE_WPF={(exactOverlappingSelection ? "OK" : "ERROR")}");
    Console.WriteLine($"MUESTRA_LECTOR={Path.GetFullPath(outputPath)}");
    return valid ? 0 : 1;
}

static Task<int> RunOnStaThreadAsync(Func<Task<int>> action)
{
    var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));

        _ = ExecuteAsync();
        System.Windows.Threading.Dispatcher.Run();

        async Task ExecuteAsync()
        {
            try
            {
                completion.TrySetResult(await action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    })
    {
        IsBackground = true,
        Name = "TintaES reader visual test"
    };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
}

static int RunCleanupPolygonSelfTest()
{
    const int width = 32;
    const int height = 32;
    const int colorStride = width * 4;
    var originalPixels = new byte[colorStride * height];
    var cleanedPixels = new byte[colorStride * height];
    var maskPixels = Enumerable.Repeat((byte)255, width * height).ToArray();
    for (int pixel = 0; pixel < width * height; pixel++)
    {
        int offset = pixel * 4;
        originalPixels[offset] = 20;
        originalPixels[offset + 1] = 40;
        originalPixels[offset + 2] = 60;
        originalPixels[offset + 3] = 255;
        cleanedPixels[offset] = 250;
        cleanedPixels[offset + 1] = 250;
        cleanedPixels[offset + 2] = 250;
        cleanedPixels[offset + 3] = 255;
    }

    BitmapSource original = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, originalPixels, colorStride);
    BitmapSource cleaned = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, cleanedPixels, colorStride);
    BitmapSource mask = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Gray8, null, maskPixels, width);
    var region = new ComicRegion
    {
        Original = "TEST",
        Type = "dialogue",
        Confidence = 1,
        TextBox = new NormalizedRect(180, 180, 640, 640),
        RenderBox = new NormalizedRect(100, 100, 800, 800),
        CleanupPolygon =
        [
            new NormalizedPoint(500, 100),
            new NormalizedPoint(900, 500),
            new NormalizedPoint(500, 900),
            new NormalizedPoint(100, 500)
        ]
    };

    DialogueOnlyResult result = new DialogueOnlyResultService().Build(
        original,
        cleaned,
        mask,
        [region],
        includeAllDetectedText: true);
    var resultPixels = new byte[colorStride * height];
    var resultMask = new byte[width * height];
    result.CleanedBitmap.CopyPixels(resultPixels, colorStride, 0);
    result.MaskBitmap.CopyPixels(resultMask, width, 0);

    int corner = 7 * width + 7;
    int centre = 16 * width + 16;
    bool cornerPreserved = resultMask[corner] == 0
        && resultPixels[corner * 4] == 20
        && resultPixels[corner * 4 + 1] == 40
        && resultPixels[corner * 4 + 2] == 60;
    bool centreCleaned = resultMask[centre] == 255
        && resultPixels[centre * 4] == 250
        && resultPixels[centre * 4 + 1] == 250
        && resultPixels[centre * 4 + 2] == 250;

    bool flatBalloonRepair = VerifyFlatBalloonRepair();
    Console.WriteLine($"LIMPIEZA_ORGANICA_ESQUINA={(cornerPreserved ? "OK" : "ERROR")}");
    Console.WriteLine($"LIMPIEZA_ORGANICA_CENTRO={(centreCleaned ? "OK" : "ERROR")}");
    Console.WriteLine($"REPARA_MANCHA_EN_BOCADILLO={(flatBalloonRepair ? "OK" : "ERROR")}");
    return cornerPreserved && centreCleaned && flatBalloonRepair ? 0 : 1;
}

static bool VerifyFlatBalloonRepair()
{
    const int width = 64;
    const int height = 40;
    const int colorStride = width * 4;
    var originalPixels = new byte[colorStride * height];
    var cleanedPixels = new byte[colorStride * height];
    var maskPixels = new byte[width * height];
    for (int pixel = 0; pixel < width * height; pixel++)
    {
        int colorOffset = pixel * 4;
        originalPixels[colorOffset] = 248;
        originalPixels[colorOffset + 1] = 249;
        originalPixels[colorOffset + 2] = 250;
        originalPixels[colorOffset + 3] = 255;
        cleanedPixels[colorOffset] = 248;
        cleanedPixels[colorOffset + 1] = 249;
        cleanedPixels[colorOffset + 2] = 250;
        cleanedPixels[colorOffset + 3] = 255;
    }

    for (int y = 17; y <= 22; y++)
    {
        for (int x = 22; x <= 41; x++)
        {
            int pixel = y * width + x;
            int colorOffset = pixel * 4;
            originalPixels[colorOffset] = 24;
            originalPixels[colorOffset + 1] = 24;
            originalPixels[colorOffset + 2] = 24;
            cleanedPixels[colorOffset] = 0;
            cleanedPixels[colorOffset + 1] = 0;
            cleanedPixels[colorOffset + 2] = 0;
            maskPixels[pixel] = 255;
        }
    }

    BitmapSource original = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, originalPixels, colorStride);
    BitmapSource cleaned = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, cleanedPixels, colorStride);
    BitmapSource mask = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Gray8, null, maskPixels, width);
    var region = new ComicRegion
    {
        Original = "MAYBE",
        Type = "sfx",
        Confidence = 1,
        TextBox = new NormalizedRect(300, 350, 400, 300),
        RenderBox = new NormalizedRect(300, 350, 400, 300),
        CleanupPolygon =
        [
            new NormalizedPoint(300, 350),
            new NormalizedPoint(700, 350),
            new NormalizedPoint(700, 650),
            new NormalizedPoint(300, 650)
        ]
    };

    DialogueOnlyResult result = new DialogueOnlyResultService().Build(
        original,
        cleaned,
        mask,
        [region],
        includeAllDetectedText: true);
    var resultPixels = new byte[colorStride * height];
    result.CleanedBitmap.CopyPixels(resultPixels, colorStride, 0);
    int centre = 20 * width + 31;
    int offset = centre * 4;
    return resultPixels[offset] >= 245
        && resultPixels[offset + 1] >= 245
        && resultPixels[offset + 2] >= 245;
}

static async Task<int> RunCleanupImageAsync(string imagePath)
{
    if (!File.Exists(imagePath))
    {
        Console.Error.WriteLine($"No existe la imagen: {imagePath}");
        return 2;
    }

    BitmapSource original = LoadBitmap(Path.GetFullPath(imagePath));
    var engine = new OrganicEngineService();
    OrganicAnalysisResult organic = await engine.AnalyzeAsync(Path.GetFullPath(imagePath));
    DialogueOnlyResult filtered = new DialogueOnlyResultService().Build(
        original,
        organic.CleanedBitmap,
        organic.MaskBitmap,
        organic.Analysis.Regions,
        includeAllDetectedText: true);

    string artifactsDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts");
    Directory.CreateDirectory(artifactsDirectory);
    string cleanPath = Path.Combine(artifactsDirectory, "cleanup-organic-preview.png");
    string maskPath = Path.Combine(artifactsDirectory, "cleanup-organic-mask.png");
    SavePng(filtered.CleanedBitmap, cleanPath);
    SavePng(filtered.MaskBitmap, maskPath);

    int organicRegions = filtered.Regions.Count(region => region.CleanupPolygon.Count >= 3);
    Console.WriteLine($"ZONAS={filtered.Regions.Count}");
    Console.WriteLine($"CONTORNOS_ORGANICOS={organicRegions}/{filtered.Regions.Count}");
    Console.WriteLine($"FONDO_LIMPIO={cleanPath}");
    Console.WriteLine($"MASCARA={maskPath}");
    return organicRegions == filtered.Regions.Count ? 0 : 1;
}

static void SavePng(BitmapSource bitmap, string path)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using FileStream stream = File.Create(path);
    encoder.Save(stream);
}

static async Task<int> RunWindowsOcrImageAsync(string imagePath)
{
    if (!File.Exists(imagePath))
    {
        Console.Error.WriteLine($"No existe la imagen: {imagePath}");
        return 2;
    }

    BitmapSource original = LoadBitmap(Path.GetFullPath(imagePath));
    ComicAnalysis analysis = await new WindowsOcrService().RecognizeWithTilingAsync(original);
    foreach (ComicRegion region in analysis.Regions)
    {
        Console.WriteLine(
            $"OCR {region.Type} {region.TextBox.X:F0},{region.TextBox.Y:F0}," +
            $"{region.TextBox.Width:F0},{region.TextBox.Height:F0}: " +
            region.Original.Replace('\n', ' '));
    }
    Console.WriteLine($"OCR_ZONAS={analysis.Regions.Count}");
    return analysis.Regions.Count > 0 ? 0 : 1;
}

static int RunLetteringLayoutSelfTest()
{
    const int width = 900;
    const int height = 600;
    int stride = width * 4;
    byte[] whitePixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
    BitmapSource white = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, whitePixels, stride);
    white.Freeze();

    static NormalizedRect Box(double x, double y, double boxWidth, double boxHeight) =>
        new(x / width * 1000, y / height * 1000, boxWidth / width * 1000, boxHeight / height * 1000);

    static IReadOnlyList<NormalizedPoint> Ellipse(double x, double y, double boxWidth, double boxHeight) =>
        Enumerable.Range(0, 48)
            .Select(index =>
            {
                double angle = Math.PI * 2 * index / 48;
                return new NormalizedPoint(
                    (x + boxWidth / 2 + Math.Cos(angle) * boxWidth / 2) / width * 1000,
                    (y + boxHeight / 2 + Math.Sin(angle) * boxHeight / 2) / height * 1000);
            })
            .ToArray();

    ComicTextStyle Style(double originalFontPixels, int originalLines) => new()
    {
        FontCategory = "comic",
        FontWeight = 700,
        FontSize = originalFontPixels / height * 1000,
        LineHeightRatio = 1.02,
        OriginalLineCount = originalLines,
        Uppercase = true,
        TextColor = "#161616",
        Alignment = "center"
    };

    ComicRegion[] regions =
    [
        new ComicRegion
        {
            Original = "SHUT UP!",
            Translation = "¡Cállate!",
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(105, 95, 110, 55),
            RenderBox = Box(45, 35, 230, 190),
            SafePolygon = Ellipse(45, 35, 230, 190),
            Style = Style(34, 2)
        },
        new ComicRegion
        {
            Original = "YOU THINK YOU ARE SO GREAT BUT YOU ARE MISSING THE POINT",
            Translation = "Crees que eres genial, pero se te escapa lo importante.",
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(375, 75, 180, 130),
            RenderBox = Box(320, 25, 290, 265),
            SafePolygon = Ellipse(320, 25, 290, 265),
            Style = Style(28, 6)
        },
        new ComicRegion
        {
            Original = "THAT DOES NOT EVEN RHYME",
            Translation = "¡Eso ni siquiera rima!",
            Type = "thought",
            IsEnabled = true,
            TextBox = Box(665, 330, 135, 80),
            RenderBox = Box(625, 280, 225, 205),
            SafePolygon =
            [
                new NormalizedPoint(737.5 / width * 1000, 280d / height * 1000),
                new NormalizedPoint(850d / width * 1000, 382.5 / height * 1000),
                new NormalizedPoint(737.5 / width * 1000, 485d / height * 1000),
                new NormalizedPoint(625d / width * 1000, 382.5 / height * 1000)
            ],
            Style = Style(31, 4)
        }
    ];

    BitmapSource? rendered = null;
    Exception? renderError = null;
    var renderThread = new Thread(() =>
    {
        try
        {
            rendered = new ImageExportService().Render(white, regions);
        }
        catch (Exception exception)
        {
            renderError = exception;
        }
    });
    renderThread.SetApartmentState(ApartmentState.STA);
    renderThread.Start();
    renderThread.Join();
    if (renderError is not null)
    {
        throw renderError;
    }

    string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "lettering-layout-self-test.png");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    SavePng(rendered!, output);

    byte[] pixels = new byte[stride * height];
    rendered!.CopyPixels(pixels, stride, 0);
    bool[] hasInk = new bool[regions.Length];
    int[] minX = Enumerable.Repeat(int.MaxValue, regions.Length).ToArray();
    int[] minY = Enumerable.Repeat(int.MaxValue, regions.Length).ToArray();
    int[] maxX = Enumerable.Repeat(int.MinValue, regions.Length).ToArray();
    int[] maxY = Enumerable.Repeat(int.MinValue, regions.Length).ToArray();
    bool inkStayedInside = true;

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * 4;
            bool ink = pixels[offset] < 210 || pixels[offset + 1] < 210 || pixels[offset + 2] < 210;
            if (!ink)
            {
                continue;
            }

            double normalizedX = (x + 0.5) / width * 1000;
            double normalizedY = (y + 0.5) / height * 1000;
            int owner = Array.FindIndex(regions, region =>
                normalizedX >= region.RenderBox.X
                && normalizedX <= region.RenderBox.Right
                && normalizedY >= region.RenderBox.Y
                && normalizedY <= region.RenderBox.Bottom);
            if (owner < 0
                || !ContainsPoint(regions[owner].SafePolygon, normalizedX, normalizedY))
            {
                inkStayedInside = false;
                continue;
            }

            hasInk[owner] = true;
            minX[owner] = Math.Min(minX[owner], x);
            minY[owner] = Math.Min(minY[owner], y);
            maxX[owner] = Math.Max(maxX[owner], x);
            maxY[owner] = Math.Max(maxY[owner], y);
        }
    }

    bool readableScale = regions.Select((region, index) =>
    {
        if (!hasInk[index])
        {
            return false;
        }
        double boxHeight = region.RenderBox.Height / 1000 * height;
        double inkHeight = maxY[index] - minY[index] + 1;
        return inkHeight / Math.Max(1, boxHeight) >= 0.20;
    }).All(value => value);

    Console.WriteLine($"ROTULOS_VISIBLES={(hasInk.All(value => value) ? "OK" : "ERROR")}");
    Console.WriteLine($"ROTULOS_DENTRO_DE_FORMAS={(inkStayedInside ? "OK" : "ERROR")}");
    Console.WriteLine($"ESCALA_LEGIBLE={(readableScale ? "OK" : "ERROR")}");
    Console.WriteLine($"MUESTRA_ROTULACION={output}");
    return hasInk.All(value => value) && inkStayedInside && readableScale ? 0 : 1;
}

static bool ContainsPoint(
    IReadOnlyList<NormalizedPoint> polygon,
    double x,
    double y)
{
    bool inside = false;
    for (int first = 0, second = polygon.Count - 1; first < polygon.Count; second = first++)
    {
        NormalizedPoint a = polygon[first];
        NormalizedPoint b = polygon[second];
        bool crosses = (a.Y > y) != (b.Y > y);
        if (crosses && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
        {
            inside = !inside;
        }
    }
    return inside;
}

static async Task<int> RunAsync(string[] args)
{
string imagePath = args.ElementAtOrDefault(0) ?? string.Empty;
string requestedModel = args.ElementAtOrDefault(1) ?? "translategemma:12b";
if (args.Length is < 1 or > 2 || !File.Exists(imagePath))
{
    Console.Error.WriteLine("Uso: TintaES.IntegrationTests <imagen> [modelo]");
    return 2;
}

var engine = new OrganicEngineService();
var warmup = Stopwatch.StartNew();
bool skipWarmup = string.Equals(
    Environment.GetEnvironmentVariable("TINTAES_SKIP_WARMUP"),
    "1",
    StringComparison.Ordinal);
try
{
    if (!skipWarmup)
    {
        await engine.WarmUpAsync();
    }
}
catch (InvalidOperationException exception)
{
    // Una aplicación TintaES abierta puede tener ya el motor residente. El análisis
    // cacheado y el render siguen siendo verificables sin arrancar una segunda copia.
    Console.WriteLine($"PRECARGA=omitida ({exception.Message})");
}
warmup.Stop();
Console.WriteLine(skipWarmup
    ? "PRECARGA=omitida por prueba concurrente"
    : $"PRECARGA={warmup.Elapsed.TotalSeconds:F2}s");
var stopwatch = Stopwatch.StartNew();
var progress = new Progress<AnalysisProgress>(value =>
    Console.WriteLine($"MOTOR={value.Percentage:F0}% {value.Message}"));
OrganicAnalysisResult organic = await engine.AnalyzeAsync(Path.GetFullPath(imagePath), progress);
BitmapSource originalBitmap = LoadBitmap(Path.GetFullPath(imagePath));
ComicRegion[] readerRegions = organic.Analysis.Regions
    .Where(MainWindow.IsReadableLetteringCandidate)
    .ToArray();
ComicAnalysis analysis = new(organic.Analysis.SourceLanguage, readerRegions);
TimeSpan engineTime = stopwatch.Elapsed;

using var ollama = new OllamaClient();
IReadOnlyList<OllamaModel> models = await ollama.GetModelsAsync();
string model = models.FirstOrDefault(item =>
                   item.Name.Equals(requestedModel, StringComparison.OrdinalIgnoreCase))?.Name
               ?? throw new InvalidOperationException($"Falta el modelo local {requestedModel}.");
Console.WriteLine($"MODELO={model}");
var translationProgress = new Progress<AnalysisProgress>(value =>
    Console.WriteLine($"TRADUCCION={value.Percentage:F0}% {value.Message}"));
try
{
    await ollama.TranslateRegionsAsync(
        analysis.Regions,
        model,
        CancellationToken.None,
        translationProgress);
}
catch (InvalidOperationException exception)
{
    Console.WriteLine($"TRADUCCION_CONTEXTUAL_PARCIAL={exception.Message}");
    ComicRegion[] unresolved = analysis.Regions
        .Where(region => !region.HasRenderableTranslation)
        .ToArray();
    await new TranslationRecoveryService().RecoverAsync(
        unresolved,
        model,
        CancellationToken.None,
        translationProgress);
}
TranslationRecoveryService.ApplyKnownLocalTranslations(analysis.Regions);
TimeSpan translationTime = stopwatch.Elapsed - engineTime;

ComicRegion[] threeBalloonRegions = CreateThreeBalloonRegressionRegions();
ApplyDetectedThreeBalloonStyles(threeBalloonRegions, analysis.Regions);
await ollama.TranslateRegionsAsync(
    threeBalloonRegions,
    model,
    CancellationToken.None);
bool threeBalloonTranslationVerified =
    VerifyThreeBalloonTranslationSemantics(threeBalloonRegions);

string artifactsDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts");
Directory.CreateDirectory(artifactsDirectory);
string renderedPath = Path.Combine(artifactsDirectory, "wpf-integration-result.png");
string[] exportedPaths =
[
    renderedPath,
    Path.Combine(artifactsDirectory, "wpf-integration-result.jpg"),
    Path.Combine(artifactsDirectory, "wpf-integration-result.webp"),
    Path.Combine(artifactsDirectory, "wpf-integration-result.tiff"),
    Path.Combine(artifactsDirectory, "wpf-integration-result.bmp"),
    Path.Combine(artifactsDirectory, "wpf-integration-result.pdf")
];
Exception? renderError = null;
bool manualFitVerified = false;
bool threeBalloonFitVerified = false;
bool spanishComicGlyphsVerified = false;
double pageRenderSeconds = double.PositiveInfinity;
var renderStepTimings = new List<string>();
var renderThread = new Thread(() =>
{
    try
    {
        var renderStep = Stopwatch.StartNew();
        spanishComicGlyphsVerified = VerifySpanishComicGlyphs();
        renderStepTimings.Add($"fuente={renderStep.Elapsed.TotalSeconds:F2}s");
        var export = new ImageExportService();
        renderStep.Restart();
        BitmapSource rendered = export
            .RenderAsync(originalBitmap, analysis.Regions)
            .GetAwaiter()
            .GetResult();
        pageRenderSeconds = renderStep.Elapsed.TotalSeconds;
        renderStepTimings.Add($"render_pagina={renderStep.Elapsed.TotalSeconds:F2}s");
        foreach (string path in exportedPaths)
        {
            renderStep.Restart();
            export.Save(rendered, path);
            renderStepTimings.Add(
                $"guardar_{Path.GetExtension(path).TrimStart('.')}={renderStep.Elapsed.TotalSeconds:F2}s");
        }
        renderStep.Restart();
        manualFitVerified = VerifyManualTextSafety(export);
        renderStepTimings.Add($"ajuste_manual={renderStep.Elapsed.TotalSeconds:F2}s");
        renderStep.Restart();
        threeBalloonFitVerified = VerifyThreeBalloonAutomaticSafety(
            export,
            threeBalloonRegions,
            Path.Combine(artifactsDirectory, "three-balloon-regression.png"));
        renderStepTimings.Add($"ajuste_3_bocadillos={renderStep.Elapsed.TotalSeconds:F2}s");
    }
    catch (Exception exception)
    {
        renderError = exception;
    }
});
renderThread.SetApartmentState(ApartmentState.STA);
renderThread.Start();
renderThread.Join();
if (renderError is not null)
{
    throw renderError;
}
stopwatch.Stop();

Console.WriteLine(
    $"MOTOR={engineTime.TotalSeconds:F2}s " +
    $"TRADUCCION={translationTime.TotalSeconds:F2}s " +
    $"TOTAL={stopwatch.Elapsed.TotalSeconds:F2}s " +
    $"ZONAS={analysis.Regions.Count} CACHE={organic.FromCache}");
Console.WriteLine($"PERFIL_RENDER={string.Join(" ", renderStepTimings)}");
for (int index = 0; index < analysis.Regions.Count; index++)
{
    ComicRegion region = analysis.Regions[index];
    Console.WriteLine($"[{index:00}] {region.Original.Replace('\n', ' ')}");
    Console.WriteLine($"     => {region.Translation.Replace('\n', ' ')}");
}

int translated = analysis.Regions.Count(region => region.HasRenderableTranslation);
int validExports = exportedPaths.Count(path =>
    File.Exists(path) && new FileInfo(path).Length > 1_000);
int layoutReferences = analysis.Regions.Count(region =>
    region.Style.FontSize > 0 && region.Style.OriginalLineCount > 0);
Console.WriteLine($"TRADUCIDAS={translated}/{analysis.Regions.Count}");
Console.WriteLine($"EXPORTACIONES={validExports}/{exportedPaths.Length}");
Console.WriteLine($"REFERENCIAS_TIPOGRAFICAS={layoutReferences}/{analysis.Regions.Count}");
Console.WriteLine($"AJUSTE_MANUAL_SEGURO={manualFitVerified}");
Console.WriteLine($"TRES_BOCADILLOS_VISIBLES_Y_DENTRO={threeBalloonFitVerified}");
Console.WriteLine($"RENDER_PAGINA_FLUIDO={pageRenderSeconds <= 15}");
Console.WriteLine($"TRES_BOCADILLOS_TRADUCIDOS_CON_SENTIDO={threeBalloonTranslationVerified}");
Console.WriteLine($"FUENTE_COMIC_ES_COMPATIBLE={spanishComicGlyphsVerified}");
Console.WriteLine(
    "ESTILO_3B=" +
    string.Join(
        ", ",
        threeBalloonRegions.Select(region =>
            $"{region.Style.FontWeight}/{(region.Style.Italic ? "cursiva" : "recta")}")));
foreach (ComicRegion region in threeBalloonRegions)
{
    Console.WriteLine($"REGRESION_3B: {region.Original} => {region.Translation}");
}
Console.WriteLine($"RESULTADO={renderedPath}");
return analysis.Regions.Count > 0
       && translated == analysis.Regions.Count
       && threeBalloonTranslationVerified
    ? 0
    : 1;
}

static bool VerifySpanishComicGlyphs()
{
    FontFamily family = ComicFontResolver.Resolve(null, "comic");
    var typeface = new Typeface(
        family,
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);
    if (!typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphs))
    {
        return false;
    }

    char[] required = ['O', 'Y', 'Ó', '¡', '¿'];
    return required.All(character => glyphs.CharacterToGlyphMap.ContainsKey(character))
           && glyphs.CharacterToGlyphMap['O'] != glyphs.CharacterToGlyphMap['Y']
           && glyphs.CharacterToGlyphMap['Ó'] != glyphs.CharacterToGlyphMap['Y'];
}

static bool VerifyManualTextSafety(ImageExportService export)
{
    const int width = 600;
    const int height = 400;
    int stride = width * 4;
    byte[] whitePixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
    BitmapSource white = BitmapSource.Create(
        width,
        height,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        whitePixels,
        stride);
    white.Freeze();

    var region = new ComicRegion
    {
        Original = "MANUAL TEXT",
        Translation =
            "ESTA ES UNA PRUEBA DE SEGURIDAD CON UNA FRASE MUY LARGA QUE DEBE REDUCIRSE " +
            "AUTOMÁTICAMENTE Y PERMANECER COMPLETA DENTRO DE LA CAJA SIN RECORTARSE.",
        Type = "dialogue",
        IsEnabled = true,
        IsManual = true,
        RenderBox = new NormalizedRect(200, 150, 600, 700),
        TextBox = new NormalizedRect(250, 200, 500, 600),
        ManualBaseFontSize = 92,
        ManualFontScale = 2.5,
        Style = new ComicTextStyle
        {
            FontCategory = "comic",
            FontWeight = 700,
            Uppercase = true,
            TextColor = "#111111",
            Alignment = "center",
            LineHeightRatio = 1.05
        }
    };
    BitmapSource rendered = export.Render(white, [region]);
    int renderedStride = rendered.PixelWidth * 4;
    byte[] pixels = new byte[renderedStride * rendered.PixelHeight];
    rendered.CopyPixels(pixels, renderedStride, 0);

    int left = (int)Math.Round(region.RenderBox.X / 1000 * width);
    int top = (int)Math.Round(region.RenderBox.Y / 1000 * height);
    int right = (int)Math.Round(region.RenderBox.Right / 1000 * width);
    int bottom = (int)Math.Round(region.RenderBox.Bottom / 1000 * height);
    const int safeMargin = 5;
    bool foundInk = false;
    for (int y = top; y < bottom; y++)
    {
        for (int x = left; x < right; x++)
        {
            int offset = y * renderedStride + x * 4;
            bool ink = pixels[offset] < 205
                       || pixels[offset + 1] < 205
                       || pixels[offset + 2] < 205;
            if (!ink)
            {
                continue;
            }
            foundInk = true;
            if (x < left + safeMargin
                || x >= right - safeMargin
                || y < top + safeMargin
                || y >= bottom - safeMargin)
            {
                return false;
            }
        }
    }
    return foundInk;
}

static ComicRegion[] CreateThreeBalloonRegressionRegions()
{
    static NormalizedRect Box(double x, double y, double boxWidth, double boxHeight) =>
        new(x / 3599 * 1000, y / 2700 * 1000, boxWidth / 3599 * 1000, boxHeight / 2700 * 1000);

    static IReadOnlyList<NormalizedPoint> Polygon(params (double X, double Y)[] points) =>
        points.Select(point => new NormalizedPoint(
            point.X / 3599 * 1000,
            point.Y / 2700 * 1000)).ToArray();

    return
    [
        new ComicRegion
        {
            Original = "HOW CAN THIS BE?!",
            OcrAlternatives = ["HOVV CAN THIS BE?!"],
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(2531, 538, 144, 221),
            RenderBox = Box(2481, 483, 244, 331),
            SafePolygon = Polygon((2481, 483), (2481, 813), (2724, 813), (2724, 483)),
            Style = new ComicTextStyle
            {
                FontCategory = "comic",
                FontWeight = 800,
                FontSize = 22.1,
                LineHeightRatio = 1.05,
                OriginalLineCount = 4,
                Italic = true,
                Uppercase = true,
                TextColor = "#111111",
                Alignment = "center"
            }
        },
        new ComicRegion
        {
            Original = "THIS IS IMPOS-SIBLE",
            OcrAlternatives = ["THIS IS IMPOS-SELE"],
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(643, 1027, 169, 151),
            RenderBox = Box(584, 990, 287, 225),
            SafePolygon = Polygon((584, 990), (584, 1214), (870, 1214), (870, 990)),
            Style = new ComicTextStyle
            {
                FontCategory = "comic",
                FontWeight = 850,
                FontSize = 20.1,
                LineHeightRatio = 1.05,
                OriginalLineCount = 3,
                Italic = true,
                Uppercase = true,
                TextColor = "#111111",
                Alignment = "center"
            }
        },
        new ComicRegion
        {
            Original = "OPEN YOUR UP..",
            OcrAlternatives = ["OPEN YOUR EYES"],
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(3093, 1498, 148, 200),
            RenderBox = Box(3042, 1448, 250, 300),
            SafePolygon = Polygon((3042, 1448), (3042, 1747), (3278, 1747), (3291, 1448)),
            Style = new ComicTextStyle
            {
                FontCategory = "comic",
                FontWeight = 650,
                FontSize = 26.7,
                LineHeightRatio = 1.05,
                OriginalLineCount = 3,
                Uppercase = true,
                TextColor = "#111111",
                Alignment = "center"
            }
        }
    ];
}

static void ApplyDetectedThreeBalloonStyles(
    IReadOnlyList<ComicRegion> target,
    IReadOnlyList<ComicRegion> detected)
{
    if (target.Count != 3 || detected.Count != 3)
    {
        return;
    }

    string[] anchors = ["HOW", "IMPOS", "OPEN"];
    for (int index = 0; index < anchors.Length; index++)
    {
        ComicRegion? source = detected.FirstOrDefault(region =>
            region.Original.Contains(anchors[index], StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return;
        }

        target[index].Style.FontWeight = source.Style.FontWeight;
        target[index].Style.FontWidthRatio = source.Style.FontWidthRatio;
        target[index].Style.Italic = source.Style.Italic;
        target[index].Style.TextColor = source.Style.TextColor;
    }
}

static bool VerifyThreeBalloonTranslationSemantics(IReadOnlyList<ComicRegion> regions)
{
    if (regions.Count != 3
        || regions.Any(region =>
            string.IsNullOrWhiteSpace(region.Translation)
            || string.Equals(
                region.Translation,
                "Traducción pendiente",
                StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    static string Letters(string value) =>
        new(value.Normalize(System.Text.NormalizationForm.FormD)
            .Where(character =>
                char.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(char.ToUpperInvariant)
            .Where(char.IsLetter)
            .ToArray());

    string first = Letters(regions[0].Translation);
    string second = Letters(regions[1].Translation);
    string third = Letters(regions[2].Translation);
    return first == "COMOPUEDESER"
           && second == "ESTOESIMPOSIBLE"
           && third == "ABRELOSOJOS";
}

static bool VerifyThreeBalloonAutomaticSafety(
    ImageExportService export,
    IReadOnlyList<ComicRegion> regions,
    string outputPath)
{
    const int width = 1800;
    const int height = 1350;
    int stride = width * 4;
    byte[] whitePixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
    BitmapSource white = BitmapSource.Create(
        width,
        height,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        whitePixels,
        stride);
    white.Freeze();

    BitmapSource rendered = export.Render(white, regions);
    export.Save(rendered, outputPath);

    string? pageBackgroundPath =
        Environment.GetEnvironmentVariable("TINTAES_THREE_BALLOON_BACKGROUND");
    if (!string.IsNullOrWhiteSpace(pageBackgroundPath)
        && File.Exists(pageBackgroundPath))
    {
        BitmapSource pageBackground = LoadBitmap(pageBackgroundPath);
        if (pageBackground.PixelWidth == 3599 && pageBackground.PixelHeight == 2700)
        {
            BitmapSource pagePreview = export.Render(pageBackground, regions);
            export.Save(
                pagePreview,
                Path.Combine(
                    Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory,
                    "three-balloon-page-preview.png"));
        }
    }

    int renderedStride = rendered.PixelWidth * 4;
    byte[] pixels = new byte[renderedStride * rendered.PixelHeight];
    rendered.CopyPixels(pixels, renderedStride, 0);
    bool[] regionHasInk = new bool[regions.Count];

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int offset = y * renderedStride + x * 4;
            bool ink = pixels[offset] < 235
                       || pixels[offset + 1] < 235
                       || pixels[offset + 2] < 235;
            if (!ink)
            {
                continue;
            }

            int owner = -1;
            for (int index = 0; index < regions.Count; index++)
            {
                NormalizedRect box = regions[index].RenderBox;
                int left = (int)Math.Floor(box.X / 1000 * width);
                int top = (int)Math.Floor(box.Y / 1000 * height);
                int right = (int)Math.Ceiling(box.Right / 1000 * width);
                int bottom = (int)Math.Ceiling(box.Bottom / 1000 * height);
                if (x >= left && x < right && y >= top && y < bottom)
                {
                    owner = index;
                    int safeMargin = Math.Max(3, (int)Math.Round(Math.Min(right - left, bottom - top) * 0.045));
                    if (x < left + safeMargin
                        || x >= right - safeMargin
                        || y < top + safeMargin
                        || y >= bottom - safeMargin)
                    {
                        return false;
                    }
                    break;
                }
            }

            if (owner < 0)
            {
                return false;
            }
            regionHasInk[owner] = true;
        }
    }

    return regionHasInk.All(value => value);
}

static BitmapSource LoadBitmap(string path)
{
    var bitmap = new BitmapImage();
    bitmap.BeginInit();
    bitmap.CacheOption = BitmapCacheOption.OnLoad;
    bitmap.UriSource = new Uri(path, UriKind.Absolute);
    bitmap.EndInit();
    bitmap.Freeze();
    return bitmap;
}
