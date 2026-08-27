from pathlib import Path


def read_exact(path: Path) -> tuple[str, str]:
    with path.open("r", encoding="utf-8", newline="") as handle:
        text = handle.read()
    eol = "\r\n" if "\r\n" in text else "\n"
    return text, eol


def write_exact(path: Path, text: str) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        handle.write(text)


client_path = Path("src/TintaES.Core/OllamaClient.cs")
client, client_eol = read_exact(client_path)
method_marker = "    private async Task TranslateGemmaBatchAsync("
if client.count(method_marker) != 1:
    raise RuntimeError("TranslateGemmaBatchAsync marker changed; refusing to patch.")

helper = """    private static int GetTranslateGemmaChunkSize(
        string model,
        IReadOnlyList<ComicRegion> regions)
    {
        const int conservativeChunkSize = 6;
        if (!model.StartsWith(\"translategemma:12b\", StringComparison.OrdinalIgnoreCase))
        {
            return conservativeChunkSize;
        }

        // TranslateGemma 12B soporta lotes mayores, pero mantenemos el tamaño histórico
        // cuando una página es especialmente densa. Así reducimos llamadas y contexto
        // repetido en páginas normales sin recortar contexto ni relajar validaciones.
        int contextCharacters = regions.Sum(region => FormatSourceForModel(region).Length + 24);
        return contextCharacters <= 6000 ? 12 : conservativeChunkSize;
    }

""".replace("\n", client_eol)
client = client.replace(method_marker, helper + method_marker)

old_chunk = "        const int chunkSize = 6;"
if client.count(old_chunk) != 1:
    raise RuntimeError("TranslateGemma chunk-size line changed; refusing to patch.")
client = client.replace(
    old_chunk,
    "        int chunkSize = GetTranslateGemmaChunkSize(model, regions);",
)
write_exact(client_path, client)


test_path = Path("tests/TintaES.Core.Tests/Program.cs")
tests, test_eol = read_exact(test_path)
list_marker = '    ("No desplaza traducciones si TranslateGemma omite una línea", TestTranslateGemmaStableMappingAsync),'
if tests.count(list_marker) != 1:
    raise RuntimeError("TranslateGemma test-list marker changed; refusing to patch.")
tests = tests.replace(
    list_marker,
    list_marker
    + test_eol
    + '    ("Reduce llamadas de TranslateGemma 12B sin perder recuperación", TestTranslateGemma12BThroughputAsync),',
)

function_marker = "static async Task TestTranslateGemmaWholeBatchRecoveryAsync()"
if tests.count(function_marker) != 1:
    raise RuntimeError("TranslateGemma test-function marker changed; refusing to patch.")
new_test = """static async Task TestTranslateGemma12BThroughputAsync()
{
    var handler = new FakeTranslateGemmaHandler();
    using var http = new HttpClient(handler) { BaseAddress = new Uri(\"http://127.0.0.1:11434/\") };
    using var client = new OllamaClient(httpClient: http);
    ComicRegion[] regions = Enumerable.Range(1, 12)
        .Select(index => new ComicRegion
        {
            Original = $\"COMIC LINE {index}\",
            Type = \"dialogue\"
        })
        .ToArray();

    await client.TranslateRegionsAsync(regions, \"translategemma:12b\", CancellationToken.None);

    Assert(regions.All(region => region.HasRenderableTranslation),
        \"El lote rápido de 12B debe conservar todas las traducciones válidas.\");
    Assert(handler.Calls == 2,
        \"Doce bocadillos con una omisión deben resolverse en un lote inicial y un reintento, no en dos lotes completos.\");
}

""".replace("\n", test_eol)
tests = tests.replace(function_marker, new_test + function_marker)
write_exact(test_path, tests)
