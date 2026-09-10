using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class PreparedPageWindowRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        VerifyPreparationOrderAndExactResultsAsync().GetAwaiter().GetResult();
        VerifyRetriesDoNotReuseMutatedResultsAsync().GetAwaiter().GetResult();
        VerifyPreparationErrorsAreIsolatedAsync().GetAwaiter().GetResult();
        VerifyCancellationStopsPreparationAsync().GetAwaiter().GetResult();
        VerifyCancellationKeepsUnconsumedResultsAsync().GetAwaiter().GetResult();
        VerifyModelLoadsAndResultsAsync().GetAwaiter().GetResult();
        VerifyArgumentsAndPageOrderAsync().GetAwaiter().GetResult();
    }

    private static async Task VerifyPreparationOrderAndExactResultsAsync()
    {
        var window = new PreparedPageWindow<Page>(7);
        var events = new List<string>();
        var prepared = new Dictionary<int, Page>();
        Task<Page> Prepare(int position, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            events.Add($"prepare{position}");
            var page = new Page($"source{position}");
            prepared.Add(position, page);
            return Task.FromResult(page);
        }

        for (int position = 0; position < 7; position++)
        {
            Page page = await window.TakeAsync(position, Prepare);
            Require(ReferenceEquals(page, prepared[position]), "Debe entregar el resultado exacto de su propia página.");
            Require(page.Source == $"source{position}", "No debe intercambiar ni modificar los textos preparados.");
            events.Add($"translate{position}");
        }

        Require(events.SequenceEqual(new[]
        {
            "prepare0", "translate0",
            "prepare1", "prepare2", "prepare3", "prepare4",
            "translate1", "translate2", "translate3", "translate4",
            "prepare5", "prepare6", "translate5", "translate6"
        }), "La primera página debe salir inmediatamente y las siguientes agruparse en ventanas acotadas, incluida la última incompleta.");
    }

    private static async Task VerifyRetriesDoNotReuseMutatedResultsAsync()
    {
        var window = new PreparedPageWindow<Page>(5);
        var calls = new List<int>();
        Task<Page> Prepare(int position, CancellationToken token)
        {
            calls.Add(position);
            return Task.FromResult(new Page($"source{position}"));
        }

        Page first = await window.TakeAsync(0, Prepare);
        first.Translation = "mutated failed attempt";
        Page retry = await window.TakeAsync(0, Prepare);
        Require(!ReferenceEquals(first, retry) && retry.Translation.Length == 0,
            "Un reintento debe obtener una instancia nueva sin las mutaciones del intento anterior.");
        Require(calls.SequenceEqual(new[] { 0, 0 }), "Reintentar la primera página no debe preparar más páginas.");

        Page second = await window.TakeAsync(1, Prepare);
        second.Translation = "another failed attempt";
        Page secondRetry = await window.TakeAsync(1, Prepare);
        Require(!ReferenceEquals(second, secondRetry) && secondRetry.Source == second.Source
                && secondRetry.Translation.Length == 0,
            "También debe descartar resultados consumidos de una ventana posterior.");
        Require(calls.SequenceEqual(new[] { 0, 0, 1, 2, 3, 4, 1 }),
            "Reintentar una página consumida debe preparar solo esa posición.");
        for (int position = 2; position < 5; position++)
        {
            await window.TakeAsync(position, Prepare);
        }
        Require(calls.Count == 7, "Un reintento no debe perder las otras páginas ya preparadas.");
    }

    private static async Task VerifyPreparationErrorsAreIsolatedAsync()
    {
        var window = new PreparedPageWindow<Page>(5);
        var calls = new List<int>();
        var expectedError = new IOException("unreadable page two");
        bool fail = true;
        Task<Page> Prepare(int position, CancellationToken token)
        {
            calls.Add(position);
            if (position == 2 && fail)
            {
                throw expectedError;
            }
            return Task.FromResult(new Page($"source{position}"));
        }

        await window.TakeAsync(0, Prepare);
        await window.TakeAsync(1, Prepare);
        Require(calls.SequenceEqual(new[] { 0, 1, 2, 3, 4 }),
            "Un fallo de preparación no debe impedir preparar las páginas siguientes.");
        IOException actualError = await ExpectAsync<IOException>(() => window.TakeAsync(2, Prepare));
        Require(ReferenceEquals(expectedError, actualError), "Debe devolver el error original al consumir su página.");
        fail = false;
        Page recovered = await window.TakeAsync(2, Prepare);
        Require(recovered.Source == "source2" && calls.SequenceEqual(new[] { 0, 1, 2, 3, 4, 2 }),
            "Debe permitir recuperar solo la página fallida sin repetir el resto de la ventana.");
        Require((await window.TakeAsync(3, Prepare)).Source == "source3"
                && (await window.TakeAsync(4, Prepare)).Source == "source4" && calls.Count == 6,
            "Los vecinos de una página fallida deben conservar sus resultados.");
    }

    private static async Task VerifyCancellationStopsPreparationAsync()
    {
        var window = new PreparedPageWindow<Page>(5);
        var calls = new List<int>();
        using var cancellation = new CancellationTokenSource();
        Task<Page> Prepare(int position, CancellationToken token)
        {
            calls.Add(position);
            if (position == 2)
            {
                cancellation.Cancel();
            }
            return Task.FromResult(new Page($"source{position}"));
        }

        await window.TakeAsync(0, Prepare);
        await ExpectAsync<OperationCanceledException>(() => window.TakeAsync(1, Prepare, cancellation.Token));
        Require(calls.SequenceEqual(new[] { 0, 1, 2 }),
            "Debe detenerse al cancelar incluso si el preparador devuelve un resultado sin atender el token.");
        await ExpectAsync<OperationCanceledException>(() => window.TakeAsync(1, Prepare, cancellation.Token));
        Require(calls.Count == 3, "Un token ya cancelado no debe iniciar más páginas.");
        for (int position = 1; position < 5; position++)
        {
            Require((await window.TakeAsync(position, Prepare)).Source == $"source{position}",
                "Reanudar una preparación cancelada no debe saltar ni intercambiar páginas.");
        }
        Require(calls.SequenceEqual(new[] { 0, 1, 2, 2, 3, 4 }),
            "Al reanudar debe conservar las preparaciones completas y repetir la interrumpida.");

        var throwingWindow = new PreparedPageWindow<Page>(4);
        var throwingCalls = new List<int>();
        Task<Page> ThrowingPrepare(int position, CancellationToken token)
        {
            throwingCalls.Add(position);
            if (position == 2)
            {
                throw new OperationCanceledException("worker cancellation");
            }
            return Task.FromResult(new Page($"source{position}"));
        }
        await throwingWindow.TakeAsync(0, ThrowingPrepare);
        await ExpectAsync<OperationCanceledException>(() => throwingWindow.TakeAsync(1, ThrowingPrepare));
        Require(throwingCalls.SequenceEqual(new[] { 0, 1, 2 }),
            "La cancelación de un preparador debe propagarse inmediatamente aunque use un token distinto.");
    }

    private static async Task VerifyCancellationKeepsUnconsumedResultsAsync()
    {
        var window = new PreparedPageWindow<Page>(5);
        var pages = Enumerable.Range(0, 5).Select(index => new Page($"source{index}")).ToArray();
        var calls = new List<int>();
        Task<Page> Prepare(int position, CancellationToken token)
        {
            calls.Add(position);
            return Task.FromResult(pages[position]);
        }

        await window.TakeAsync(0, Prepare);
        await window.TakeAsync(1, Prepare);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => window.TakeAsync(2, Prepare, cancellation.Token));
        Require(ReferenceEquals(await window.TakeAsync(2, Prepare), pages[2]) && calls.Count == 5,
            "Cancelar antes de consumir una página preparada no debe descartarla ni repetir su OCR.");
    }

    private static async Task VerifyModelLoadsAndResultsAsync()
    {
        string[] sources =
        [
            "EASY TO SAY WHEN I'M THE ONE DOING MOST OF THE FIGHTING.",
            "I HIT THAT DUDE WITH A GUITAR! TWICE!",
            "TRUE. GUESS I'M JUST THINKIN' BOUT THE LONG OF IT.",
            "WE'LL BE WAITING.",
            "LET'S GO."
        ];

        async Task<(int Loads, string[] Sources, string[] Translations)> Run(bool useWindow)
        {
            var window = new PreparedPageWindow<Page>(sources.Length);
            var results = new List<Page>();
            bool translationModelLoaded = false;
            int loads = 0;
            Task<Page> Prepare(int position, CancellationToken token)
            {
                translationModelLoaded = false; // OCR releases translation VRAM in the real pipeline.
                return Task.FromResult(new Page(sources[position]));
            }

            for (int position = 0; position < sources.Length; position++)
            {
                Page page = useWindow
                    ? await window.TakeAsync(position, Prepare)
                    : await Prepare(position, CancellationToken.None);
                if (!translationModelLoaded)
                {
                    loads++;
                    translationModelLoaded = true;
                }
                page.Translation = $"translated({page.Source})";
                results.Add(page);
            }
            return (loads, results.Select(page => page.Source).ToArray(),
                results.Select(page => page.Translation).ToArray());
        }

        var baseline = await Run(false);
        var grouped = await Run(true);
        Require(baseline.Loads == 5 && grouped.Loads == 2,
            "Cinco páginas deben pasar de cinco cargas del traductor a dos manteniendo la primera respuesta inmediata.");
        Require(baseline.Sources.SequenceEqual(grouped.Sources)
                && baseline.Translations.SequenceEqual(grouped.Translations),
            "Agrupar las preparaciones debe conservar todos los textos y resultados, en el mismo orden.");
    }

    private static async Task VerifyArgumentsAndPageOrderAsync()
    {
        await ExpectAsync<ArgumentOutOfRangeException>(() => Task.FromResult(new PreparedPageWindow<Page>(-1)));
        await ExpectAsync<ArgumentOutOfRangeException>(() => Task.FromResult(new PreparedPageWindow<Page>(1, 0)));
        var empty = new PreparedPageWindow<Page>(0);
        var window = new PreparedPageWindow<Page>(2, 1);
        int calls = 0;
        Task<Page> Prepare(int position, CancellationToken token)
        {
            calls++;
            return Task.FromResult(new Page($"source{position}"));
        }
        await ExpectAsync<ArgumentOutOfRangeException>(() => empty.TakeAsync(0, Prepare));
        await ExpectAsync<ArgumentOutOfRangeException>(() => window.TakeAsync(-1, Prepare));
        await ExpectAsync<ArgumentOutOfRangeException>(() => window.TakeAsync(2, Prepare));
        await ExpectAsync<ArgumentNullException>(() => window.TakeAsync(0, null!));
        await ExpectAsync<InvalidOperationException>(() => window.TakeAsync(1, Prepare));
        Require(calls == 0, "Argumentos inválidos y saltos de página deben rechazarse antes de preparar nada.");
        await window.TakeAsync(0, Prepare);
        Require(calls == 1, "Una ventana unitaria debe preparar solo la página solicitada.");
        await window.TakeAsync(1, Prepare);
        Require(calls == 2, "Rechazar un salto no debe impedir consumir después todas las páginas en orden.");
    }

    private static async Task<TException> ExpectAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Se esperaba {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Regresión de preparación de páginas: {message}");
        }
    }

    private sealed class Page(string source)
    {
        public string Source { get; } = source;
        public string Translation { get; set; } = string.Empty;
    }
}
