using System.Runtime.CompilerServices;
using System.Windows;
using TintaES.Wpf;

internal static class ReaderTranslationCardPlacementRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var page = new Rect(0, 0, 1000, 1400);
        var card = new Size(260, 130);

        // Caso normal: la traducción debe quedar a la izquierda del bocadillo y del puntero.
        var middleBubble = new Rect(430, 560, 180, 150);
        var middlePointer = new Point(520, 635);
        Point middle = ComicReaderWindow.ResolveReaderTranslationCardPlacement(
            page, middleBubble, middlePointer, card, isTouch: true);
        var middleCard = new Rect(middle, card);
        Require(middleCard.Right < middleBubble.Left,
            "La posición normal debe quedar a la izquierda del bocadillo.");
        Require(!middleCard.Contains(middlePointer),
            "La tarjeta no puede quedar bajo el dedo en el caso normal.");
        Require(Contains(page, middleCard),
            "La tarjeta normal debe quedar completamente dentro de la página.");

        // Bocadillo pegado a la izquierda: debe saltar en diagonal hacia la derecha y arriba.
        var leftBubble = new Rect(5, 560, 190, 150);
        var leftPointer = new Point(95, 635);
        Point left = ComicReaderWindow.ResolveReaderTranslationCardPlacement(
            page, leftBubble, leftPointer, card, isTouch: true);
        var leftCard = new Rect(left, card);
        Require(leftCard.Left > leftBubble.Right,
            "Si no cabe a la izquierda, debe pasar a la derecha del bocadillo.");
        Require(leftCard.Bottom < leftPointer.Y,
            "Con espacio superior, el salto desde el margen izquierdo debe ser diagonal hacia arriba.");
        Require(!leftCard.Contains(leftPointer) && Contains(page, leftCard),
            "La diagonal derecha debe seguir visible y dejar libre el dedo.");

        // Esquina superior izquierda: arriba ya no cabe, así que la tarjeta debe caer por debajo.
        var topLeftBubble = new Rect(0, 4, 200, 135);
        var topLeftPointer = new Point(90, 55);
        Point topLeft = ComicReaderWindow.ResolveReaderTranslationCardPlacement(
            page, topLeftBubble, topLeftPointer, card, isTouch: true);
        var topLeftCard = new Rect(topLeft, card);
        Require(topLeftCard.Left > topLeftBubble.Right,
            "En la esquina superior izquierda debe conservar la salida por la derecha.");
        Require(topLeftCard.Top > topLeftBubble.Bottom,
            "Si el borde superior bloquea la tarjeta, debe desplazarse hacia abajo.");
        Require(!topLeftCard.Contains(topLeftPointer) && Contains(page, topLeftCard),
            "La corrección por borde superior debe mantener tarjeta y dedo visibles.");

        // Bocadillo alto con espacio a la izquierda: se puede mantener a la izquierda, pero la Y
        // debe deslizarse hacia abajo en vez de salir fuera de la página.
        var topBubble = new Rect(430, 0, 180, 150);
        var topPointer = new Point(520, 35);
        Point top = ComicReaderWindow.ResolveReaderTranslationCardPlacement(
            page, topBubble, topPointer, card, isTouch: false);
        var topCard = new Rect(top, card);
        Require(topCard.Top >= page.Top,
            "El borde superior debe actuar como pared y empujar la tarjeta hacia abajo.");
        Require(Contains(page, topCard),
            "Una tarjeta junto al borde superior nunca puede desaparecer fuera de la página.");

        // Al mover el dedo dentro del mismo bocadillo, la posición vertical debe poder adaptarse.
        var tallBubble = new Rect(420, 360, 170, 360);
        Point fingerHigh = ComicReaderWindow.ResolveReaderTranslationCardPlacement(
            page, tallBubble, new Point(505, 430), card, isTouch: true);
        Point fingerLow = ComicReaderWindow.ResolveReaderTranslationCardPlacement(
            page, tallBubble, new Point(505, 650), card, isTouch: true);
        Require(Math.Abs(fingerHigh.Y - fingerLow.Y) > 40,
            "La tarjeta debe recolocarse al mover el dedo dentro del bocadillo.");
    }

    private static bool Contains(Rect outer, Rect inner) =>
        inner.Left >= outer.Left - 0.01
        && inner.Top >= outer.Top - 0.01
        && inner.Right <= outer.Right + 0.01
        && inner.Bottom <= outer.Bottom + 0.01;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
