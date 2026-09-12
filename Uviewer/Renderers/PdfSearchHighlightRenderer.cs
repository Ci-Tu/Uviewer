using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using Uviewer.Models;
using Uviewer.Services;
using Windows.Foundation;

namespace Uviewer.Renderers
{
    internal static class PdfSearchHighlightRenderer
    {
        internal static void Draw(
            CanvasControl sender,
            CanvasDrawEventArgs args,
            CanvasBitmap? currentBitmap,
            bool hasPdfDocument,
            int currentPageIndex,
            double zoomLevel,
            double panX,
            double panY,
            int activeSearchPageIndex,
            IReadOnlyList<PdfSearchHighlight> highlights,
            int activeMatchIndex)
        {
            if (!hasPdfDocument || currentBitmap == null) return;
            if (activeSearchPageIndex != currentPageIndex || highlights.Count == 0) return;
            if (!PdfPageLayout.TryGetPageRect(currentBitmap, sender.Size, zoomLevel, panX, panY, out var pageRect)) return;

            foreach (var highlight in highlights)
            {
                if (highlight.MatchIndex == activeMatchIndex) continue;
                DrawHighlight(args, pageRect, highlight, SearchHighlightService.HighlightColor);
            }

            foreach (var highlight in highlights)
            {
                if (highlight.MatchIndex != activeMatchIndex) continue;
                DrawHighlight(args, pageRect, highlight, SearchHighlightService.CurrentHighlightColor);
            }
        }

        private static void DrawHighlight(
            CanvasDrawEventArgs args,
            Rect pageRect,
            PdfSearchHighlight highlight,
            Windows.UI.Color color)
        {
            if (highlight.PageWidth <= 0 || highlight.PageHeight <= 0) return;

            double x = pageRect.X + (highlight.Left / highlight.PageWidth) * pageRect.Width;
            double y = pageRect.Y + ((highlight.PageHeight - highlight.Top) / highlight.PageHeight) * pageRect.Height;
            double width = Math.Max(2.0, ((highlight.Right - highlight.Left) / highlight.PageWidth) * pageRect.Width);
            double height = Math.Max(2.0, ((highlight.Top - highlight.Bottom) / highlight.PageHeight) * pageRect.Height);

            var rect = new Rect(x - 1, y - 1, width + 2, height + 2);
            args.DrawingSession.FillRectangle(rect, color);
        }
    }
}
