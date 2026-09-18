using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Uviewer.Services
{
    internal sealed class TrayIconService : IDisposable
    {
        private const uint TrayIconId = 1;
        private const uint TrayCallbackMessage = 0x8000 + 41;
        private const uint WmNull = 0x0000;
        private const uint WmContextMenu = 0x007B;
        private const uint WmLButtonDoubleClick = 0x0203;
        private const uint WmRButtonUp = 0x0205;
        private const uint NimAdd = 0x00000000;
        private const uint NimDelete = 0x00000002;
        private const uint NimSetVersion = 0x00000004;
        private const uint NifMessage = 0x00000001;
        private const uint NifIcon = 0x00000002;
        private const uint NifTip = 0x00000004;
        private const uint NifShowTip = 0x00000080;
        private const uint NotifyIconVersion4 = 4;
        private const uint ImageIcon = 1;
        private const uint LrLoadFromFile = 0x00000010;
        private const uint LrDefaultSize = 0x00000040;
        private const uint MfString = 0x00000000;
        private const uint MfChecked = 0x00000008;
        private const uint MfSeparator = 0x00000800;
        private const uint TpmRightButton = 0x0002;
        private const uint TpmReturnCommand = 0x0100;
        private const uint OpenCommandId = 1;
        private const uint ExitCommandId = 2;
        private const uint WindowCommandIdStart = 0x1000;
        private const int WindowMenuItemMaxLength = 80;
        private const int MaxWindowMenuItems = 40;
        private const nuint SubclassId = 0x55564945;

        private readonly IntPtr _windowHandle;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Func<string> _openText;
        private readonly Func<string> _exitText;
        private readonly Action _openRequested;
        private readonly Action _exitRequested;
        private readonly Action _contextMenuOpening;
        private readonly Action _contextMenuClosed;
        private readonly Func<IReadOnlyList<TrayWindowItem>> _windowItemsProvider;
        private readonly Action<long> _windowSelected;
        private readonly SubclassProc _subclassProc;
        private readonly uint _taskbarCreatedMessage;
        private IntPtr _iconHandle;
        private bool _isVisible;
        private bool _isVersion4;
        private bool _isContextMenuOpen;
        private bool _disposed;

        public bool IsVisible => _isVisible;

        public TrayIconService(
            IntPtr windowHandle,
            DispatcherQueue dispatcherQueue,
            Func<string> openText,
            Func<string> exitText,
            Action openRequested,
            Action exitRequested,
            Action contextMenuOpening,
            Action contextMenuClosed,
            Func<IReadOnlyList<TrayWindowItem>>? windowItemsProvider = null,
            Action<long>? windowSelected = null)
        {
            _windowHandle = windowHandle;
            _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
            _openText = openText ?? throw new ArgumentNullException(nameof(openText));
            _exitText = exitText ?? throw new ArgumentNullException(nameof(exitText));
            _openRequested = openRequested ?? throw new ArgumentNullException(nameof(openRequested));
            _exitRequested = exitRequested ?? throw new ArgumentNullException(nameof(exitRequested));
            _contextMenuOpening = contextMenuOpening ?? throw new ArgumentNullException(nameof(contextMenuOpening));
            _contextMenuClosed = contextMenuClosed ?? throw new ArgumentNullException(nameof(contextMenuClosed));
            _windowItemsProvider = windowItemsProvider ?? (() => Array.Empty<TrayWindowItem>());
            _windowSelected = windowSelected ?? (_ => { });
            _subclassProc = WindowSubclassProc;
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

            if (!SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, 0))
            {
                throw new InvalidOperationException("Failed to install the tray icon window hook.");
            }
        }

        public void SetVisible(bool visible)
        {
            if (_disposed || visible == _isVisible) return;

            if (visible)
            {
                _isVisible = AddIcon();
            }
            else
            {
                RemoveIcon();
                _isVisible = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RemoveIcon();
            RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);

            if (_iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
        }

        private bool AddIcon()
        {
            EnsureIconLoaded();
            if (_iconHandle == IntPtr.Zero) return false;

            var data = CreateNotifyIconData();
            if (!Shell_NotifyIcon(NimAdd, ref data)) return false;

            data.uTimeoutOrVersion = NotifyIconVersion4;
            // 버전 4가 적용되면 우클릭이 WM_CONTEXTMENU로 전달됩니다(구버전은 WM_RBUTTONUP).
            _isVersion4 = Shell_NotifyIcon(NimSetVersion, ref data);
            return true;
        }

        private void RemoveIcon()
        {
            if (!_isVisible) return;

            var data = CreateNotifyIconData();
            Shell_NotifyIcon(NimDelete, ref data);
            _isVisible = false;
        }

        private void EnsureIconLoaded()
        {
            if (_iconHandle != IntPtr.Zero) return;

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Uviewer.ico");
            if (!File.Exists(iconPath))
            {
                iconPath = Path.Combine(AppContext.BaseDirectory, "Uviewer.ico");
            }

            if (File.Exists(iconPath))
            {
                _iconHandle = LoadImage(
                    IntPtr.Zero,
                    iconPath,
                    ImageIcon,
                    0,
                    0,
                    LrLoadFromFile | LrDefaultSize);
            }

            if (_iconHandle == IntPtr.Zero && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                var largeIcons = new IntPtr[1];
                var smallIcons = new IntPtr[1];
                if (ExtractIconEx(Environment.ProcessPath, 0, largeIcons, smallIcons, 1) > 0)
                {
                    _iconHandle = smallIcons[0] != IntPtr.Zero ? smallIcons[0] : largeIcons[0];
                    IntPtr unusedIcon = _iconHandle == smallIcons[0] ? largeIcons[0] : smallIcons[0];
                    if (unusedIcon != IntPtr.Zero)
                    {
                        DestroyIcon(unusedIcon);
                    }
                }
            }
        }

        private NotifyIconData CreateNotifyIconData() => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _windowHandle,
            uID = TrayIconId,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _iconHandle,
            szTip = "Uviewer"
        };

        private IntPtr WindowSubclassProc(
            IntPtr hWnd,
            uint message,
            UIntPtr wParam,
            IntPtr lParam,
            UIntPtr subclassId,
            UIntPtr referenceData)
        {
            if (message == _taskbarCreatedMessage && _isVisible)
            {
                _isVisible = false;
                _isVisible = AddIcon();
            }
            else if (message == TrayCallbackMessage)
            {
                uint mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;

                // NOTIFYICON_VERSION_4에서는 우클릭이 WM_CONTEXTMENU로, 구버전에서는
                // WM_RBUTTONUP으로 전달됩니다. 두 메시지를 모두 처리하면 우클릭 한 번에
                // 메뉴가 두 번 표시되므로(위 메뉴를 닫으면 아래 메뉴가 드러남) 현재
                // 적용된 버전에 해당하는 메시지만 처리합니다.
                uint contextMenuMessage = _isVersion4 ? WmContextMenu : WmRButtonUp;

                if (mouseMessage == WmLButtonDoubleClick)
                {
                    QueueAction(_openRequested);
                }
                else if (mouseMessage == contextMenuMessage)
                {
                    ShowContextMenu();
                }
            }

            return DefSubclassProc(hWnd, message, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            // 우클릭 알림이 중복 전달되어도 메뉴를 두 번 표시하지 않습니다.
            if (_isContextMenuOpen) return;
            _isContextMenuOpen = true;

            _contextMenuOpening();
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                _contextMenuClosed();
                _isContextMenuOpen = false;
                return;
            }

            try
            {
                var windowCommands = new Dictionary<uint, TrayWindowItem>();

                AppendMenu(menu, MfString, OpenCommandId, _openText());
                AppendMenu(menu, MfSeparator, 0, null);

                AppendWindowItems(menu, windowCommands);

                AppendMenu(menu, MfString, ExitCommandId, _exitText());

                if (!GetCursorPos(out Point cursor)) return;

                SetForegroundWindow(_windowHandle);
                uint commandId = TrackPopupMenu(
                    menu,
                    TpmRightButton | TpmReturnCommand,
                    cursor.X,
                    cursor.Y,
                    0,
                    _windowHandle,
                    IntPtr.Zero);
                PostMessage(_windowHandle, WmNull, UIntPtr.Zero, IntPtr.Zero);
                HandleCommand(commandId, windowCommands);
            }
            finally
            {
                DestroyMenu(menu);
                _contextMenuClosed();
                _isContextMenuOpen = false;
            }
        }

        private void AppendWindowItems(IntPtr menu, Dictionary<uint, TrayWindowItem> windowCommands)
        {
            IReadOnlyList<TrayWindowItem>? items = null;
            try
            {
                items = _windowItemsProvider();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error building tray window items: {ex.Message}");
            }

            if (items == null || items.Count == 0) return;

            int count = Math.Min(items.Count, MaxWindowMenuItems);
            for (int index = 0; index < count; index++)
            {
                TrayWindowItem item = items[index];
                uint flags = item.IsCurrent ? MfString | MfChecked : MfString;
                uint commandId = WindowCommandIdStart + (uint)index;
                AppendMenu(menu, flags, commandId, FormatMenuItemText(item.Title));
                windowCommands[commandId] = item;
            }

            AppendMenu(menu, MfSeparator, 0, null);
        }

        private static string FormatMenuItemText(string? title)
        {
            // '&'는 Win32 메뉴에서 단축키 표시로 해석되므로 이스케이프합니다.
            string text = string.IsNullOrWhiteSpace(title)
                ? " "
                : title.Replace("&", "&&").Trim();

            if (text.Length == 0) text = " ";
            if (text.Length > WindowMenuItemMaxLength)
            {
                text = text.Substring(0, WindowMenuItemMaxLength - 1) + "\u2026";
            }

            return text;
        }

        private void HandleCommand(uint commandId, IReadOnlyDictionary<uint, TrayWindowItem> windowCommands)
        {
            if (commandId == OpenCommandId)
            {
                QueueAction(_openRequested);
            }
            else if (commandId == ExitCommandId)
            {
                QueueAction(_exitRequested);
            }
            else if (windowCommands.TryGetValue(commandId, out TrayWindowItem? item) && item != null)
            {
                long targetHandle = item.WindowHandle;
                QueueAction(() => _windowSelected(targetHandle));
            }
        }

        private void QueueAction(Action action)
        {
            if (!_disposed)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!_disposed) action();
                });
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        private delegate IntPtr SubclassProc(
            IntPtr hWnd,
            uint message,
            UIntPtr wParam,
            IntPtr lParam,
            UIntPtr subclassId,
            UIntPtr referenceData);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc callback, nuint subclassId, nuint referenceData);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc callback, nuint subclassId);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(
            string file,
            int iconIndex,
            [Out] IntPtr[] largeIcons,
            [Out] IntPtr[] smallIcons,
            uint iconCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint loadFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string messageName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, nuint itemId, string? text);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint TrackPopupMenu(
            IntPtr menu,
            uint flags,
            int x,
            int y,
            int reserved,
            IntPtr owner,
            IntPtr rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);
    }
}
