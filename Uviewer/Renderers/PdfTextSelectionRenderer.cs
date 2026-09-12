using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using Uviewer.Services;
using Windows.Foundation;

namespace Uviewer.Renderers
{
    /// <summary>
    /// PDF 뷰어에서 Ctrl+드래그로 선택한 텍스트 범위를 하이라이트로 그립니다.
    /// 검색 하이라이트(PdfSearchHighlightRenderer)와 동일한 좌표 변환을 사용합니다.
    /// </summary>
    internal static class PdfTextSelectionRenderer
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
            int selectionPageIndex,
            IReadOnlyList<PdfSearchHighlight> highlights)
        {
            if (!hasPdfDocument || currentBitmap == null) return;
            if (selectionPageIndex != currentPageIndex || highlights.Count == 0) return;

            if (!PdfPageLayout.TryGetPageRect(currentBitmap, sender.Size, zoomLevel, panX, panY, out var pageRect)) return;

            foreach (var highlight in highlights)
            {
                if (highlight.PageWidth <= 0 || highlight.PageHeight <= 0) continue;

                double x = pageRect.X + (highlight.Left / highlight.PageWidth) * pageRect.Width;
                double y = pageRect.Y + ((highlight.PageHeight - highlight.Top) / highlight.PageHeight) * pageRect.Height;
                double width = Math.Max(2.0, ((highlight.Right - highlight.Left) / highlight.PageWidth) * pageRect.Width);
                double height = Math.Max(2.0, ((highlight.Top - highlight.Bottom) / highlight.PageHeight) * pageRect.Height);

                var rect = new Rect(x - 1, y - 1, width + 2, height + 2);
                args.DrawingSession.FillRectangle(rect, TextSelectionVisuals.SelectionColor);
            }
        }
    }
}
