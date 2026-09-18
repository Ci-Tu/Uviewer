using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace Uviewer.Services
{
    public sealed class TextViewportService
    {
        private const double LineHeightMultiplier = 1.8;

        public void NavigatePage(ScrollViewer scrollViewer, double fontSize, int direction)
        {
            double current = scrollViewer.VerticalOffset;
            double viewport = scrollViewer.ViewportHeight;
            double lineHeight = fontSize * LineHeightMultiplier;
            double overlap = lineHeight;

            if (overlap > viewport * 0.5)
            {
                overlap = viewport * 0.2;
            }

            double scrollAmount = viewport - overlap;
            double target = direction > 0
                ? current + scrollAmount
                : current - scrollAmount;

            scrollViewer.ChangeView(null, target, null, true);
        }

        public int GetTopVisibleLineIndex(
            ItemsRepeater repeater,
            ScrollViewer scrollViewer,
            int lineCount,
            double fontSize)
        {
            if (lineCount == 0) return 1;

            try
            {
                double viewportTop = scrollViewer.Padding.Top;
                int childCount = VisualTreeHelper.GetChildrenCount(repeater);
                if (childCount == 0) return 1;

                UIElement? closest = null;
                double minDistance = double.MaxValue;

                for (int i = 0; i < childCount; i++)
                {
                    var child = VisualTreeHelper.GetChild(repeater, i) as UIElement;
                    if (child == null) continue;

                    var transform = child.TransformToVisual(scrollViewer);
                    var point = transform.TransformPoint(new Point(0, 0));
                    double top = point.Y;
                    double bottom = top + ((FrameworkElement)child).ActualHeight;

                    if (top <= viewportTop && bottom > viewportTop)
                    {
                        int index = repeater.GetElementIndex(child);
                        if (index >= 0) return index + 1;
                    }

                    double distance = Math.Abs(top - viewportTop);
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        closest = child;
                    }
                }

                if (closest != null)
                {
                    int index = repeater.GetElementIndex(closest);
                    if (index >= 0) return index + 1;
                }
            }
            catch
            {
            }

            double lineHeight = fontSize * LineHeightMultiplier;
            if (lineHeight > 0)
            {
                return (int)(scrollViewer.VerticalOffset / lineHeight) + 1;
            }

            return 1;
        }

        public async Task ScrollToLineAsync(
            ItemsRepeater repeater,
            ScrollViewer scrollViewer,
            int line,
            int lineCount,
            double fontSize,
            CancellationToken token = default)
        {
            if (lineCount == 0) return;
            int index = Math.Clamp(line, 1, lineCount) - 1;
            var source = repeater.ItemsSource;

            // Let the newly attached source finish its normal layout. Forcing
            // UpdateLayout at an estimated nonzero offset can repeatedly change
            // StackLayout's extent and ScrollViewer's anchor during a mode switch.
            await Task.Delay(16, token);
            if (!ReferenceEquals(source, repeater.ItemsSource) || scrollViewer.Visibility != Visibility.Visible) return;
            var element = repeater.GetOrCreateElement(index);
            await Task.Delay(16, token);
            if (!ReferenceEquals(source, repeater.ItemsSource) || scrollViewer.Visibility != Visibility.Visible) return;

            if (element != null && repeater.GetElementIndex(element) == index)
            {
                element.StartBringIntoView(new BringIntoViewOptions
                {
                    VerticalAlignmentRatio = 0,
                    AnimationDesired = false
                });
            }
            else
            {
                scrollViewer.ChangeView(null, index * fontSize * LineHeightMultiplier, null, true);
            }
        }
    }
}
