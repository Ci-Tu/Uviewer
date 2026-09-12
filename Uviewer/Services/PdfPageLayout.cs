using Microsoft.Graphics.Canvas;
using System;
using Windows.Foundation;

namespace Uviewer.Services
{
    /// <summary>
    /// PDF 비트맵이 캔버스에 그려지는 위치/크기(pageRect)를 계산합니다.
    /// 검색 하이라이트 렌더러와 텍스트 선택이 동일한 좌표 변환을 공유하도록 합니다.
    /// </summary>
    internal static class PdfPageLayout
    {
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
