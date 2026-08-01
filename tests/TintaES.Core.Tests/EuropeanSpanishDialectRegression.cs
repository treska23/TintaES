using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class EuropeanSpanishDialectRegression
{
    [ModuleInitializer]
    internal static void VerifyEuropeanSpanishPolicy()
    {
        AssertRetry(
            "You should leave now.",
            "Usted debería irse ahora.",
            expected: true,
            "Un diálogo informal no puede introducir usted por defecto.");

        AssertRetry(
            "You guys wait here.",
            "Ustedes esperen aquí.",
            expected: true,
            "El plural informal debe rehacerse con vosotros/os y concordancia peninsular.");

        AssertRetry(
            "Call me on my phone.",
            "Llámame al celular.",
            expected: true,
            "Celular debe revisarse para español de España.");

        AssertRetry(
            "Officer, could you help me?",
            "Agente, ¿podría usted ayudarme?",
            expected: false,
            "El tratamiento formal debe conservarse cuando el original lo justifica.");

        AssertRetry(
            "Come with us!",
            "¡Ven con nosotros!",
            expected: false,
            "Una traducción peninsular natural no debe repetirse.");
    }

    private static void AssertRetry(
        string source,
        string translation,
        bool expected,
        string message)
    {
        bool actual = EuropeanSpanishDialect.RequiresRetry(source, translation);
        if (actual != expected)
        {
            throw new InvalidOperationException(message);
        }
    }
}
