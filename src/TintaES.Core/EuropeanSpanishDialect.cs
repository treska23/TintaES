using System.Text.RegularExpressions;

namespace TintaES.Core;

/// <summary>
/// Política lingüística única para TintaES. El destino es español europeo contemporáneo,
/// con el registro adulto y la intensidad del original. En este proyecto «ustedes» no se
/// usa como plural general ni como tratamiento automático.
/// </summary>
public static class EuropeanSpanishDialect
{
    public const string ModelInstruction =
        "Escribe exclusivamente en español de España contemporáneo, no en español neutro ni latinoamericano. " +
        "Para una sola persona usa tú en conversación normal y usted únicamente cuando el original o la escena " +
        "marquen de verdad cortesía, jerarquía o distancia. Para varias personas usa vosotros/vosotras, con " +
        "sus formas verbales y pronombres (os, vuestro/a). Los imperativos dirigidos a un grupo deben usar las " +
        "formas peninsulares correspondientes, como permitidme, dejadme, mirad, escuchad, venid o decidme; no " +
        "uses formas de ustedes como permítanme, déjenme, miren, escuchen, vengan o díganme. No uses «ustedes» " +
        "en este proyecto: no lo emplees ni como plural general ni como tratamiento de cortesía automático. " +
        "Traduce las exclamaciones por su función en la escena, no por una acepción aislada: ante «where are my " +
        "manners?» la idea es «qué modales los míos» o «dónde están mis modales», nunca «qué pena»; en ese " +
        "contexto «shoot» equivale a «vaya» o «caramba». Mantén el registro exacto de los personajes. El cómic " +
        "es para adultos: conserva tacos, insultos, amenazas, vulgaridad, violencia verbal y dobles sentidos con " +
        "una intensidad equivalente; no censures, no pongas asteriscos, no eufemices y no suavices palabras como " +
        "fuck, fucking, shit, asshole, bastard, bitch o motherfucker. Utiliza léxico habitual en España y evita " +
        "regionalismos latinoamericanos como computadora, celular, carro, boleto, estacionamiento, elevador, " +
        "manejar, platicar o enojado cuando el sentido normal en España sea ordenador, móvil, coche, billete/entrada, " +
        "aparcamiento, ascensor, conducir, hablar o enfadado.";

    private static readonly Regex SuspiciousDialect = new(
        @"\b(?:ustedes|permítanme|déjenme|mírenme|escúchenme|díganme|computador(?:a|as|es)?|"
        + @"celular(?:es)?|carro(?:s)?|boleto(?:s)?|estacionamiento(?:s)?|elevador(?:es)?|"
        + @"manejar(?:lo|la|los|las|me|te|se|nos)?|platicar(?:on|án|ía|ían|é|ás|emos)?|"
        + @"enojad[oa]s?|chamarra(?:s)?|playera(?:s)?|jugo(?:s)?|refrigerador(?:es)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExplicitFormalSource = new(
        @"\b(?:sir|ma['’]?am|madam|mister|mr\.?|mrs\.?|miss|officer|detective|doctor|dr\.?|"
        + @"professor|captain|commander|general|chief|boss|judge|your\s+(?:majesty|highness|honor)|"
        + @"my\s+lord|your\s+lordship|father|mother\s+superior)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExplicitSingularFormalTranslation = new(
        @"\busted\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StrongAdultSource = new(
        @"\b(?:motherfucker(?:s)?|motherfucking|fucker(?:s)?|fucking|fuck(?:ed|ing|s)?|"
        + @"bullshit|shit(?:ty|head|heads)?|asshole(?:s)?|bastard(?:s)?|bitch(?:es)?|"
        + @"son\s+of\s+a\s+bitch|cunt(?:s)?|dickhead(?:s)?|prick(?:s)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StrongAdultSpanish = new(
        @"\b(?:joder|jodid[oa]s?|mierda|put[oa]s?|putísimo|putísima|cabr[oó]n(?:es|a|as)?|"
        + @"gilipollas|capull[oa]s?|hij[oa]s?\s+de\s+puta|coño|zorra(?:s)?|mamón(?:es|a|as)?|"
        + @"cabronazo(?:s)?|cabronaza(?:s)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MannersSource = new(
        @"\bWHERE(?:['’]?RE|\s+ARE)\s+MY\s+MANNERS\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MannersMeaningInSpanish = new(
        @"\b(?:modales|educación)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool RequiresRetry(string? source, string? translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        string original = source ?? string.Empty;
        string target = translation.Trim();
        if (SuspiciousDialect.IsMatch(target))
        {
            return true;
        }

        // Evita la lectura errónea observada en Spider-Punk: aquí «shoot» es una exclamación
        // y la pregunta habla de los modales del personaje, no de sentir pena.
        if (MannersSource.IsMatch(original)
            && (!MannersMeaningInSpanish.IsMatch(target)
                || Regex.IsMatch(target, @"\bqué\s+pena\b", RegexOptions.IgnoreCase)))
        {
            return true;
        }

        // «Usted» singular sí existe en España, pero no se acepta como sustituto automático
        // de cualquier YOU. La frase se rehace completa para conservar concordancias.
        if (ExplicitSingularFormalTranslation.IsMatch(target)
            && !ExplicitFormalSource.IsMatch(original))
        {
            return true;
        }

        // Si el original contiene un taco fuerte y el resultado no conserva ninguna marca de
        // intensidad adulta, probablemente el modelo lo ha suavizado o censurado.
        return StrongAdultSource.IsMatch(original)
            && !StrongAdultSpanish.IsMatch(target);
    }
}
