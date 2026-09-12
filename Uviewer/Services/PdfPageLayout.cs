using Microsoft.Graphics.Canvas;
using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace Uviewer.Services
{
    /// <summary>
    /// PDF 비트맵이 캔버스에 그려지는 위치/크기(pageRect)를 계산합니다.
    /// 검색 하이라이트 렌더러와 텍스트 선택이 동일한 좌표 변환을 공유하도록 합니다.
    /// </summary>
    internal static class PdfPageLayout
    {
        internal readonly record struct DisplayPage(int Index, CanvasBitmap Bitmap, Rect Bounds);

        // Rendering and hit testing must use the same page rectangles, including previews
        // rendered at a different resolution while a zoom render is pending.
        internal static IEnumerable<DisplayPage> GetDisplayPages(
            CanvasBitmap? currentBitmap, ImageCacheManager cache, int currentIndex, int pageCount,
            Size canvasSize, double zoomLevel, double panX, double panY)
        {
            if (currentIndex < 0 || currentIndex >= pageCount ||
                !TryGetPageRect(currentBitmap, canvasSize, zoomLevel, panX, panY, out var current)) yield break;

            yield return new DisplayPage(currentIndex, currentBitmap!, current);
            double gap = 20 * zoomLevel;
            foreach (int direction in new[] { -1, 1 })
            {
                double edge = direction < 0 ? current.Top : current.Bottom;
                for (int index = currentIndex + direction; index >= 0 && index < pageCount; index += direction)
                {
                    if (direction < 0 ? edge < -500 : edge > canvasSize.Height + 500) break;
                    var bitmap = cache.GetPreloadedImage(index);
                    if (bitmap == currentBitmap ||
                        !TryGetPageRect(bitmap, canvasSize, zoomLevel, panX, 0, out var bounds)) break;

                    bounds.Y = direction < 0 ? edge - gap - bounds.Height : edge + gap;
                    yield return new DisplayPage(index, bitmap!, bounds);
                    edge = direction < 0 ? bounds.Top : bounds.Bottom;
                }
            }
        }

        public static bool TryGetPageRect(
            CanvasBitmap? bitmap,
            Size canvasSize,
            double zoomLevel,
            double panX,
            double panY,
            out Rect pageRect)
        {
            pageRect = default;

            if (!CanvasBitmapHelper.TryGetBitmapSize(bitmap, out var imageSize)) return false;
            if (canvasSize.Width <= 0 || canvasSize.Height <= 0) return false;

            double fitRatio = Math.Min(canvasSize.Width / imageSize.Width, canvasSize.Height / imageSize.Height);
            var scaledSize = new Size(
                imageSize.Width * fitRatio * zoomLevel,
                imageSize.Height * fitRatio * zoomLevel);

            if (scaledSize.Width <= 0 || scaledSize.Height <= 0) return false;

            pageRect = new Rect(
                (canvasSize.Width - scaledSize.Width) / 2 + panX,
                (canvasSize.Height - scaledSize.Height) / 2 + panY,
                scaledSize.Width,
                scaledSize.Height);
            return true;
        }
    }
}
