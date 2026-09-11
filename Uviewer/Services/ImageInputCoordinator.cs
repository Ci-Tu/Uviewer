using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace Uviewer.Services
{
    internal sealed class ImageInputCoordinator
    {
        private readonly IImageInputHost _host;
        private readonly Func<ImageViewportNavigationContext> _createNavigationContext;
        private readonly Func<bool, Task> _navigatePreviousAsync;
        private readonly Func<bool, Task> _navigateNextAsync;
        private readonly Action _applyZoom;
        private readonly HashSet<uint> _touchPointers = new();
        private bool _isPinchManipulation;
        private bool _suppressTouchTap;

        public ImageInputCoordinator(
            IImageInputHost host,
            Func<ImageViewportNavigationContext> createNavigationContext,
            Func<bool, Task> navigatePreviousAsync,
            Func<bool, Task> navigateNextAsync,
            Action applyZoom)
        {
            _host = host;
            _createNavigationContext = createNavigationContext;
            _navigatePreviousAsync = navigatePreviousAsync;
            _navigateNextAsync = navigateNextAsync;
            _applyZoom = applyZoom;
        }

        public void ImageAreaSizeChanged(SizeChangedEventArgs e)
        {
            _host.LastCanvasWidth = e.NewSize.Width;

            if (_host.CurrentBitmap != null &&
                (_host.MainCanvas.Visibility == Visibility.Visible ||
                 _host.SideBySideGrid.Visibility == Visibility.Visible))
            {
                _applyZoom();
            }
        }

        public async Task HandlePointerWheelAsync(PointerRoutedEventArgs e)
        {
            try
            {
                var properties = e.GetCurrentPoint(_host.ImageArea).Properties;
                var wheelDelta = properties.MouseWheelDelta;
                var isHorizontal = properties.IsHorizontalMouseWheel;

                var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
                if (ctrl.HasFlag(CoreVirtualKeyStates.Down))
                {
                    if (_host.CurrentBitmap != null && (!_host.IsCurrentViewSideBySide || _host.IsPdfMode))
                    {
                        double zoomMultiplier = Math.Exp(wheelDelta * 0.001);
                        var point = e.GetCurrentPoint(_host.ImageArea).Position;
                        _host.ImageViewportNavigationService.StartSmoothZoom(
                            _createNavigationContext(),
                            zoomMultiplier,
                            point);
                    }
                    e.Handled = true;
                    return;
                }

                if ((_isPinchManipulation && _touchPointers.Count > 0) ||
                    _host.ImageViewportNavigationService.IsSmoothZoomRunning)
                {
                    e.Handled = true;
                    return;
                }

                if (_host.CurrentBitmap != null &&
                    (_host.IsPdfMode || (_host.ZoomLevel > 1.01 && !_host.IsCurrentViewSideBySide)))
                {
                    if (isHorizontal)
                    {
                        await HandleScrollAsync(wheelDelta, 0);
                    }
                    else
                    {
                        await HandleScrollAsync(0, wheelDelta);
                    }
                    e.Handled = true;
                    return;
                }

                if (Math.Abs(wheelDelta) >= 40)
                {
                    if (wheelDelta < 0) await _navigateNextAsync(false);
                    else await _navigatePreviousAsync(false);
                }

                e.Handled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ImageArea_PointerWheelChanged: {ex.Message}");
                _host.ShowNotification($"{ex.Message}", "\uE783", "Red");
            }
        }

        public void ManipulationStarting(ManipulationStartingRoutedEventArgs e)
        {
            _isPinchManipulation = _touchPointers.Count > 1;
            e.Container = _host.ImageArea;
            e.Mode = ManipulationModes.All;
        }

        public async Task ManipulationDeltaAsync(ManipulationDeltaRoutedEventArgs e)
        {
            try
            {
                if (_host.CurrentBitmap == null || (_host.IsCurrentViewSideBySide && !_host.IsPdfMode)) return;

                if (e.Delta.Scale != 1.0f)
                {
                    // Keep this latched through translation-only and inertia deltas.
                    _isPinchManipulation = true;
                    _host.ImageViewportNavigationService.ZoomAtPosition(
                        _createNavigationContext(),
                        e.Delta.Scale,
                        e.Position);
                }

                e.Handled = true;
                await _host.ImageViewportNavigationService.HandleScrollAsync(
                    _createNavigationContext(),
                    e.Delta.Translation.X,
                    e.Delta.Translation.Y,
                    allowPageTransition: !_isPinchManipulation);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ImageArea_ManipulationDelta: {ex.Message}");
                _host.ShowNotification($"{ex.Message}", "\uE783", "Red");
            }
        }

        public void ManipulationCompleted()
        {
            _host.ImageViewportNavigationService.IsTransitioning = false;
            if (_host.IsPdfMode)
            {
                _ = _host.RerenderPdfCurrentPageAsync();
            }
        }

        public Task HandleScrollAsync(double deltaX, double deltaY) =>
            _host.ImageViewportNavigationService.HandleScrollAsync(
                _createNavigationContext(),
                deltaX,
                deltaY);

        public async Task PointerPressedAsync(PointerRoutedEventArgs e)
        {
            try
            {
                // A touch press may be the start of a pinch; navigate only on Tapped.
                if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
                {
                    if (_touchPointers.Count == 0)
                    {
                        _isPinchManipulation = false;
                        _suppressTouchTap = false;
                    }
                    _touchPointers.Add(e.Pointer.PointerId);
                    if (_touchPointers.Count > 1)
                    {
                        _isPinchManipulation = true;
                        _suppressTouchTap = true;
                    }
                    if (_host.WindowShellController.HandleFullscreenPanelPointer(e))
                    {
                        _suppressTouchTap = true;
                        e.Handled = true;
                        _host.FocusRoot();
                    }
                    return;
                }

                if (_host.WindowShellController.HandleFullscreenPanelPointer(e))
                {
                    e.Handled = true;
                    _host.FocusRoot();
                    return;
                }

                if (_host.ImageEntries.Count <= 1)
                    return;

                var point = e.GetCurrentPoint(_host.ImageArea);
                if (!point.Properties.IsLeftButtonPressed)
                    return;

                double half = _host.ImageArea.ActualWidth * 0.5;
                if (point.Position.X < half)
                {
                    if (_host.ShouldInvertControls) await _navigateNextAsync(true);
                    else await _navigatePreviousAsync(true);
                }
                else
                {
                    if (_host.ShouldInvertControls) await _navigatePreviousAsync(true);
                    else await _navigateNextAsync(true);
                }
                e.Handled = true;
                _host.FocusRoot();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ImageArea_PointerPressed: {ex.Message}");
                _host.ShowNotification($"{ex.Message}", "\uE783", "Red");
            }
        }

        public void PointerEnded(PointerRoutedEventArgs e)
        {
            _touchPointers.Remove(e.Pointer.PointerId);
        }

        public async Task TappedAsync(TappedRoutedEventArgs e)
        {
            if (e.PointerDeviceType != PointerDeviceType.Touch || _isPinchManipulation || _suppressTouchTap ||
                _touchPointers.Count > 1 || _host.ZoomLevel > 1.01 ||
                _host.IsPdfMode || _host.ImageEntries.Count <= 1)
                return;

            e.Handled = true;
            bool isLeft = e.GetPosition(_host.ImageArea).X < _host.ImageArea.ActualWidth * 0.5;
            if (isLeft != _host.ShouldInvertControls)
                await _navigatePreviousAsync(true);
            else
                await _navigateNextAsync(true);
            _host.FocusRoot();
        }
    }
}
