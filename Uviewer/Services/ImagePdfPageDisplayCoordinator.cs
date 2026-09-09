using Microsoft.Graphics.Canvas;
using System;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Models;

namespace Uviewer.Services
{
    internal sealed class ImagePdfPageDisplayCoordinator
    {
        private readonly IImagePdfPageDisplayHost _host;
        private readonly Func<CanvasBitmap, bool> _isBitmapInCache;
        private readonly Action _showImageUi;
        private readonly Action<ImageEntry, CanvasBitmap> _updateStatusBar;

        public ImagePdfPageDisplayCoordinator(
            IImagePdfPageDisplayHost host,
            Func<CanvasBitmap, bool> isBitmapInCache,
            Action showImageUi,
            Action<ImageEntry, CanvasBitmap> updateStatusBar)
        {
            _host = host;
            _isBitmapInCache = isBitmapInCache;
            _showImageUi = showImageUi;
            _updateStatusBar = updateStatusBar;
        }

        public async Task DisplayPdfPageAsync(ImageEntry entry, int capturedIndexAtStart, CancellationToken token)
        {
            _host.SwitchToImageMode();
            _host.IsCurrentViewSideBySide = false;

            // Any cached resolution is useful immediately; the resolution check in
            // RerenderCurrentPageAsync upgrades it after navigation settles.
            CanvasBitmap? nextBitmap = _host.ImageCache.GetPreloadedImage(_host.CurrentIndex);

            if (nextBitmap == null)
            {
                nextBitmap = await LoadMissingPdfBitmapAsync(entry, capturedIndexAtStart, token);
            }

            if (nextBitmap != null && !token.IsCancellationRequested && _host.CurrentIndex == capturedIndexAtStart)
            {
                DisplayLoadedPdfBitmap(entry, nextBitmap);
            }

            await _host.AddToRecentAsync(false);
            _host.FocusRoot();
        }

        private async Task<CanvasBitmap?> LoadMissingPdfBitmapAsync(
            ImageEntry entry,
            int capturedIndexAtStart,
            CancellationToken token)
        {
            // Keep the displayed bitmap alive until its replacement is ready.
            var nextBitmap = await _host.LoadPdfPageBitmapAsync(entry.PdfPageIndex, _host.MainCanvas!, token);

            if (nextBitmap != null)
            {
                if (token.IsCancellationRequested || _host.CurrentIndex != capturedIndexAtStart)
                {
                    _host.ImageCache.SafeDisposeBitmap(nextBitmap);
                    return null;
                }

                _host.ImageCache.UpdateCache(
                    capturedIndexAtStart,
                    nextBitmap,
                    true,
                    _host.ZoomLevel,
                    _host.CurrentBitmap);
            }

            return nextBitmap;
        }

        private void DisplayLoadedPdfBitmap(ImageEntry entry, CanvasBitmap nextBitmap)
        {
            var oldBitmap = _host.CurrentBitmap;
            _host.CurrentBitmap = nextBitmap;
            _host.LeftBitmap = null;
            _host.RightBitmap = null;

            if (!_host.IsSeamlessScroll)
            {
                _host.ImageViewportNavigationService.ResetPanForBitmap(
                    _host.MainCanvas!,
                    nextBitmap,
                    _host.ZoomLevel);
            }

            _host.MainCanvas?.Invalidate();
            _showImageUi();
            _updateStatusBar(entry, _host.CurrentBitmap);
            _ = _host.RerenderPdfCurrentPageAsync();

            if (oldBitmap != null && oldBitmap != nextBitmap && !_isBitmapInCache(oldBitmap))
            {
                _host.ImageCache.SafeDisposeBitmap(oldBitmap);
            }
        }
    }
}
