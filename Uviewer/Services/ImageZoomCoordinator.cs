using Windows.Foundation;

namespace Uviewer.Services
{
    internal sealed class ImageZoomCoordinator
    {
        private readonly IImageZoomHost _host;

        public ImageZoomCoordinator(IImageZoomHost host)
        {
            _host = host;
        }

        public void ZoomActual()
        {
            if (_host.IsCurrentViewSideBySide && !_host.IsPdfMode) return;

            if (CanvasBitmapHelper.TryGetBitmapSize(_host.CurrentBitmap, out var bitmapSize))
            {
                var containerWidth = _host.ImageArea.ActualWidth;
                var containerHeight = _host.ImageArea.ActualHeight;

                if (containerWidth > 0 && containerHeight > 0)
                {
                    double previousZoom = _host.ZoomLevel;
                    _host.ZoomService.CalculateActualZoom(
                        containerWidth,
                        containerHeight,
                        bitmapSize.Width,
                        bitmapSize.Height,
                        _host.MainCanvas.Dpi / 96.0f,
                        _host.IsPdfMode);
                    PreservePdfPosition(previousZoom);
                    ApplyZoom();
                }
            }
        }

        public void ZoomIn()
        {
            if (_host.IsCurrentViewSideBySide && !_host.IsPdfMode) return;
            double previousZoom = _host.ZoomLevel;
            _host.ZoomService.ZoomIn();
            PreservePdfPosition(previousZoom);
            ApplyZoom();
        }

        public void ZoomOut()
        {
            if (_host.IsCurrentViewSideBySide && !_host.IsPdfMode) return;
            double previousZoom = _host.ZoomLevel;
            _host.ZoomService.ZoomOut();
            PreservePdfPosition(previousZoom);
            ApplyZoom();
        }

        public void FitToWindow()
        {
            double previousZoom = _host.ZoomLevel;
            _host.ZoomService.FitToWindow();
            PreservePdfPosition(previousZoom);
            ApplyZoom();
        }

        private void PreservePdfPosition(double previousZoom)
        {
            if (!_host.IsPdfMode || !CanvasBitmapHelper.TryGetBitmapSize(_host.CurrentBitmap, out var size)) return;
            var navigation = _host.ImageViewportNavigationService;
            navigation.StopSmoothZoom();
            var canvasSize = _host.MainCanvas.Size;
            var transform = ZoomService.CalculateZoomAtPosition(canvasSize, size, previousZoom,
                navigation.PanX, navigation.PanY, _host.ZoomLevel / previousZoom,
                new Point(canvasSize.Width / 2, canvasSize.Height / 2), continuousVertical: true);
            if (!transform.HasValue) return;
            navigation.PanX = transform.Value.PanX;
            navigation.PanY = transform.Value.PanY;
        }

        public void ApplyZoom()
        {
            if (!CanvasBitmapHelper.IsUsable(_host.CurrentBitmap) ||
                _host.ImageArea.ActualWidth <= 0 ||
                _host.ImageArea.ActualHeight <= 0)
            {
                return;
            }

            if (!_host.IsCurrentViewSideBySide || _host.IsPdfMode)
            {
                _host.MainCanvas?.Invalidate();
            }
            else
            {
                _host.LeftCanvas?.Invalidate();
                _host.RightCanvas?.Invalidate();
            }

            _host.MainToolbar.SetZoomLevel(_host.ZoomLevel);

            if (_host.IsPdfMode && !_host.ImageViewportNavigationService.IsSmoothZoomRunning)
            {
                _ = _host.RerenderPdfCurrentPageAsync();
            }
        }
    }
}
