using System.Text.RegularExpressions;

namespace TintaES.Core;

/// <summary>
/// Política lingüística única para las traducciones de TintaES. El destino es español
/// europeo contemporáneo, no un español neutro indeterminado. En diálogo informal se usa
/// tú para una persona y vosotros/vosotras para varias; usted/ustedes se reserva para una
/// formalidad que esté realmente indicada por el original o por la escena.
/// </summary>
public static class EuropeanSpanishDialect
{
    public const string ModelInstruction =
        "Escribe exclusivamente en español de España contemporáneo, no en español neutro ni latinoamericano. " +
        "En conversación informal usa tú para una persona y vosotros/vosotras para varias, con sus formas " +
        "verbales y pronombres correspondientes (os, vuestro/a). No uses usted ni ustedes por defecto. " +
        "Úsalos únicamente cuando el original o el contexto indiquen de verdad respeto formal, jerarquía, " +
        "distancia social, tratamiento profesional, época histórica o fórmulas como sir, ma'am, Mr., Mrs., " +
        "officer, doctor, Your Majesty o equivalentes. Mantén el registro del personaje y utiliza léxico " +
        "habitual en España; evita regionalismos latinoamericanos como computadora, celular, carro, boleto, " +
        "estacionamiento, elevador, manejar, platicar o enojado cuando el sentido normal en España sea " +
        "ordenador, móvil, coche, billete/entrada, aparcamiento, ascensor, conducir, hablar o enfadado.";

    private static readonly Regex SuspiciousDialect = new(
        @"\b(?:ustedes|computador(?:a|as|es)?|celular(?:es)?|carro(?:s)?|boleto(?:s)?|"
        + @"estacionamiento(?:s)?|elevador(?:es)?|manejar(?:lo|la|los|las|me|te|se|nos)?|"
        + @"platicar(?:on|án|ía|ían|é|ás|emos)?|enojad[oa]s?|chamarra(?:s)?|playera(?:s)?|"
        + @"jugo(?:s)?|refrigerador(?:es)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExplicitFormalSource = new(
        @"\b(?:sir|ma['’]?am|madam|mister|mr\.?|mrs\.?|miss|officer|detective|doctor|dr\.?|"
        + @"professor|captain|commander|general|chief|boss|judge|your\s+(?:majesty|highness|honor)|"
        + @"my\s+lord|your\s+lordship|father|mother\s+superior)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExplicitFormalTranslation = new(
        @"\b(?:usted|ustedes)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool RequiresRetry(string? source, string? translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        string target = translation.Trim();
        if (SuspiciousDialect.IsMatch(target))
        {
            return true;
        }

        // No convertimos mecánicamente las conjugaciones: se repite la traducción para que
        // el modelo rehaga toda la frase con concordancia correcta. El tratamiento formal se
        // conserva cuando el inglés contiene una señal suficientemente clara.
        return ExplicitFormalTranslation.IsMatch(target)
            && !ExplicitFormalSource.IsMatch(source ?? string.Empty);
    }
}
