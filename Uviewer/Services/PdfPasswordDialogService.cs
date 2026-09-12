using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.System;

namespace Uviewer.Services
{
    /// <summary>
    /// 비밀번호로 보호된 PDF의 비밀번호 입력 대화상자.
    /// 입력이 취소되면 null을 반환한다.
    /// </summary>
    internal static class PdfPasswordDialogService
    {
        public static Task<string?> ShowAsync(XamlRoot? xamlRoot, ElementTheme theme, string pdfPath, bool isRetry)
        {
            if (xamlRoot is null)
            {
                return Task.FromResult<string?>(null);
            }

            // XamlRoot.Content can be temporarily null while a window is being
            // attached, even though the root itself is already valid. Prefer the
            // content dispatcher, then fall back to the current UI dispatcher.
            var dispatcher = xamlRoot.Content?.DispatcherQueue ??
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                return ShowCoreAsync(xamlRoot, theme, pdfPath, isRetry);
            }

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        tcs.TrySetResult(await ShowCoreAsync(xamlRoot, theme, pdfPath, isRetry));
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }))
            {
                tcs.TrySetResult(null);
            }

            return tcs.Task;
        }

        private static async Task<string?> ShowCoreAsync(XamlRoot xamlRoot, ElementTheme theme, string pdfPath, bool isRetry)
        {
            var panel = new StackPanel
            {
                Width = 320,
                Height = 124,
                Spacing = 8
            };

            panel.Children.Add(new TextBlock
            {
                Text = Strings.PdfPasswordPromptForFile(Path.GetFileName(pdfPath)),
                Height = 56,
                TextWrapping = TextWrapping.Wrap
            });

            // Keep this row in the layout even on the first attempt. Otherwise the
            // dialog is measured at a different height after a failed attempt.
            panel.Children.Add(new TextBlock
            {
                Text = isRetry ? Strings.PdfPasswordWrong : string.Empty,
                Height = 20,
                TextWrapping = TextWrapping.Wrap
            });

            var passwordBox = new PasswordBox
            {
                PlaceholderText = Strings.PdfPasswordPlaceholder,
                Height = 32,
                MinHeight = 32,
                MaxHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(passwordBox);

            bool accepted = false;
            var dialog = new ContentDialog
            {
                Title = Strings.PdfPasswordTitle,
                Content = panel,
                PrimaryButtonText = Strings.PdfPasswordOpen,
                CloseButtonText = Strings.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = theme
            };

            passwordBox.KeyDown += (_, args) =>
            {
                if (args.Key != VirtualKey.Enter || passwordBox.Password.Length == 0)
                {
                    return;
                }

                args.Handled = true;
                accepted = true;
                dialog.Hide();
            };

            dialog.Opened += (_, _) =>
            {
                passwordBox.Focus(FocusState.Programmatic);
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary && !accepted)
            {
                return null;
            }

            return passwordBox.Password.Length == 0 ? null : passwordBox.Password;
        }
    }
}
