from pathlib import Path

path = Path('tests/TintaES.Core.Tests/Program.cs')
text = path.read_text(encoding='utf-8')

list_marker = '    ("Conserva el sentido y el registro español en la escena real", TestComicSceneSemanticGuardsAsync)\n};'
list_replacement = '    ("Conserva el sentido y el registro español en la escena real", TestComicSceneSemanticGuardsAsync),\n    ("Exporta e importa un guion de traducción estable", TestTranslationExchangeAsync)\n};'
if text.count(list_marker) != 1:
    raise SystemExit('No se encontró exactamente una vez el cierre esperado de la lista de pruebas.')
text = text.replace(list_marker, list_replacement)

function_marker = 'static Task TestSanitizeAsync()\n{'
function = r'''static Task TestTranslationExchangeAsync()
{
    Guid firstId = Guid.NewGuid();
    Guid secondId = Guid.NewGuid();
    var document = new TranslationExchangeDocument
    {
        ComicTitle = "Prueba",
        PageCount = 1,
        Pages =
        [
            new TranslationExchangePage
            {
                Page = 1,
                Name = "001.png",
                SourceLanguage = "en",
                Regions =
                [
                    new TranslationExchangeRegion
                    {
                        RegionId = firstId,
                        Order = 1,
                        Type = "dialogue",
                        Original = "HOW ARE YOU?",
                        Translation = "¿Cómo estás?",
                        BubbleId = "B01",
                        TextBox = new TranslationExchangeRect { X = 100, Y = 120, Width = 180, Height = 70 },
                        BubbleBox = new TranslationExchangeRect { X = 80, Y = 90, Width = 230, Height = 130 },
                        RenderBox = new TranslationExchangeRect { X = 90, Y = 100, Width = 210, Height = 110 }
                    },
                    new TranslationExchangeRegion
                    {
                        RegionId = secondId,
                        Order = 2,
                        Type = "dialogue",
                        Original = "I'M FINE.",
                        Translation = "Estoy bien.",
                        BubbleId = "B02",
                        TextBox = new TranslationExchangeRect { X = 500, Y = 300, Width = 160, Height = 60 },
                        RenderBox = new TranslationExchangeRect { X = 480, Y = 280, Width = 200, Height = 100 }
                    }
                ]
            }
        ]
    };

    string exported = TranslationExchange.Serialize(document);
    Assert(exported.Contains("\"regionId\"", StringComparison.Ordinal),
        "El guion debe conservar identificadores estables de zona.");
    Assert(exported.Contains("\"bubbleId\"", StringComparison.Ordinal),
        "El guion debe explicar a qué bocadillo pertenece cada texto.");

    string reviewed = exported.Replace("¿Cómo estás?", "¿Qué tal estás?", StringComparison.Ordinal);
    string wrappedByAi = "Aquí tienes el archivo revisado:\n```json\n" + reviewed + "\n```";
    IReadOnlyDictionary<Guid, string> imported = TranslationExchange.ReadTranslations(wrappedByAi);
    Assert(imported.Count == 2, "Debe recuperar todas las traducciones aunque la IA envuelva el JSON.");
    Assert(imported[firstId] == "¿Qué tal estás?", "Debe importar la traducción revisada por su regionId.");
    Assert(imported[secondId] == "Estoy bien.", "No debe desplazar traducciones entre bocadillos.");

    bool rejected = false;
    try
    {
        TranslationExchange.ReadTranslations("{\"translation\":\"Texto sin identificador\"}");
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }
    Assert(rejected, "Un archivo sin regionId no puede aplicarse silenciosamente a otro texto.");
    return Task.CompletedTask;
}

'''
if text.count(function_marker) != 1:
    raise SystemExit('No se encontró exactamente una vez el punto de inserción de la prueba.')
text = text.replace(function_marker, function + function_marker)
path.write_text(text, encoding='utf-8')
