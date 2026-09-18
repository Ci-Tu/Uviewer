using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Uviewer.Services
{
    /// <summary>
    /// 트레이 우클릭 메뉴에 표시할 창(열린 파일) 항목입니다.
    /// </summary>
    internal sealed class TrayWindowItem
    {
        public string Title { get; init; } = string.Empty;
        public long WindowHandle { get; init; }
        public int ProcessId { get; init; }
        public bool IsCurrent { get; init; }
    }

    /// <summary>
    /// 다중 실행 중인 인스턴스 정보입니다.
    /// </summary>
    internal sealed class InstanceInfo
    {
        public int ProcessId { get; init; }
        public long WindowHandle { get; init; }
        public string Title { get; init; } = string.Empty;
        public bool KeepInTray { get; init; }
        public bool AllowMultipleInstances { get; init; }
        public bool HasVisibleWindow { get; init; } = true;
    }

    /// <summary>
    /// 다중 실행 중인 Uviewer 인스턴스들을 파일 기반 레지스트리로 공유합니다.
    /// 트레이 아이콘 소유자 선출, 다른 창 활성화 요청, 다른 창 닫기 요청을 담당합니다.
    /// </summary>
    internal sealed class MultiInstanceCoordinator : IDisposable
    {
        private const nuint SubclassId = 0x55564948; // "UVIH"
        private const string EntryFilePrefix = "instance-";
        private const string ActivateMessageName = "Uviewer.Instance.Activate";
        private const string CloseMessageName = "Uviewer.Instance.Close";
        private const uint AsfwAny = 0xFFFFFFFF;

        private static readonly string CurrentProcessName = ResolveCurrentProcessName();

        private readonly IntPtr _windowHandle;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Action _activateRequested;
        private readonly Action _closeRequested;
        private readonly SubclassProc _subclassProc;
        private readonly uint _activateMessage;
        private readonly uint _closeMessage;
        private readonly int _processId;
        private readonly string _registryDirectory;

        private string _lastWrittenTitle = string.Empty;
        private bool _lastWrittenKeepInTray;
        private bool _lastWrittenAllowMultipleInstances;
        private bool _lastWrittenHasVisibleWindow;
        private bool _hasWrittenEntry;
        private bool _isTrayOwner = true;
        private bool _disposed;

        /// <summary>이 인스턴스가 트레이 아이콘을 소유해야 하는지 여부입니다.</summary>
        public bool IsTrayOwner => _isTrayOwner;

        public MultiInstanceCoordinator(
            IntPtr windowHandle,
            DispatcherQueue dispatcherQueue,
            Action activateRequested,
            Action closeRequested)
        {
            _windowHandle = windowHandle;
            _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
            _activateRequested = activateRequested ?? throw new ArgumentNullException(nameof(activateRequested));
            _closeRequested = closeRequested ?? throw new ArgumentNullException(nameof(closeRequested));
            _subclassProc = WindowSubclassProc;
            _processId = Environment.ProcessId;
            _registryDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Uviewer",
                "instances");

            // RegisterWindowMessage는 시스템 전역 원자이므로 모든 인스턴스가 같은 값을 받습니다.
            _activateMessage = RegisterWindowMessage(ActivateMessageName);
            _closeMessage = RegisterWindowMessage(CloseMessageName);
        }

        public void Start()
        {
            if (_disposed) return;

            try
            {
                Directory.CreateDirectory(_registryDirectory);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Multi-instance registry directory error: {ex.Message}");
            }

            if (!SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, 0))
            {
                Debug.WriteLine("Failed to install the multi-instance window hook.");
            }
        }

        /// <summary>
        /// 이 인스턴스의 상태를 등록하고 트레이 아이콘 소유자를 다시 계산합니다.
        /// </summary>
        public void UpdateSelf(string title, bool keepInTray, bool allowMultipleInstances, bool hasVisibleWindow)
        {
            if (_disposed) return;

            string normalizedTitle = NormalizeTitle(title);
            if (!_hasWrittenEntry ||
                !string.Equals(_lastWrittenTitle, normalizedTitle, StringComparison.Ordinal) ||
                _lastWrittenKeepInTray != keepInTray ||
                _lastWrittenAllowMultipleInstances != allowMultipleInstances ||
                _lastWrittenHasVisibleWindow != hasVisibleWindow)
            {
                WriteEntry(normalizedTitle, keepInTray, allowMultipleInstances, hasVisibleWindow);
                _lastWrittenTitle = normalizedTitle;
                _lastWrittenKeepInTray = keepInTray;
                _lastWrittenAllowMultipleInstances = allowMultipleInstances;
                _lastWrittenHasVisibleWindow = hasVisibleWindow;
                _hasWrittenEntry = true;
            }

            RefreshOwnerState();
        }

        /// <summary>살아 있는 인스턴스 목록을 반환합니다. 죽은 인스턴스의 항목은 정리합니다.</summary>
        public IReadOnlyList<InstanceInfo> GetLiveInstances()
        {
            var instances = new List<InstanceInfo>();
            if (_disposed) return instances;

            try
            {
                if (!Directory.Exists(_registryDirectory)) return instances;

                foreach (string path in Directory.EnumerateFiles(_registryDirectory, EntryFilePrefix + "*.txt"))
                {
                    if (TryReadEntry(path, out InstanceInfo info))
                    {
                        instances.Add(info);
                    }
                    else
                    {
                        TryDeleteEntryFile(path);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reading the multi-instance registry failed: {ex.Message}");
            }

            return instances;
        }

        /// <summary>다른 인스턴스의 창을 활성화하도록 요청합니다.</summary>
        public void RequestActivate(long windowHandle)
        {
            if (_disposed || windowHandle == 0) return;

            if (windowHandle == _windowHandle.ToInt64())
            {
                QueueAction(_activateRequested);
                return;
            }

            try
            {
                // 트레이 클릭으로 전경 권한을 얻은 이 프로세스가 대상 프로세스에 전경 전환을 허용합니다.
                AllowSetForegroundWindow(AsfwAny);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AllowSetForegroundWindow failed: {ex.Message}");
            }

            PostMessage(new IntPtr(windowHandle), _activateMessage, UIntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>현재 창을 제외한 모든 인스턴스에 종료를 요청합니다.</summary>
        public void RequestCloseOtherInstances()
        {
            if (_disposed) return;

            foreach (InstanceInfo instance in GetLiveInstances())
            {
                if (instance.ProcessId == _processId) continue;
                if (instance.WindowHandle == 0) continue;

                PostMessage(new IntPtr(instance.WindowHandle), _closeMessage, UIntPtr.Zero, IntPtr.Zero);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
            TryDeleteEntryFile(GetEntryFilePath());
        }

        private void RefreshOwnerState()
        {
            InstanceInfo? owner = null;
            foreach (InstanceInfo instance in GetLiveInstances())
            {
                if (!instance.KeepInTray) continue;
                if (owner == null || instance.ProcessId < owner.ProcessId)
                {
                    owner = instance;
                }
            }

            _isTrayOwner = owner != null && owner.ProcessId == _processId;
        }

        private IntPtr WindowSubclassProc(
            IntPtr hWnd,
            uint message,
            UIntPtr wParam,
            IntPtr lParam,
            UIntPtr subclassId,
            UIntPtr referenceData)
        {
            if (_activateMessage != 0 && message == _activateMessage)
            {
                QueueAction(_activateRequested);
            }
            else if (_closeMessage != 0 && message == _closeMessage)
            {
                QueueAction(_closeRequested);
            }

            return DefSubclassProc(hWnd, message, wParam, lParam);
        }

        private void QueueAction(Action action)
        {
            if (_disposed) return;

            try
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!_disposed) action();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Queueing a multi-instance action failed: {ex.Message}");
            }
        }

        private string GetEntryFilePath() =>
            Path.Combine(_registryDirectory, $"{EntryFilePrefix}{_processId}.txt");

        private void WriteEntry(string title, bool keepInTray, bool allowMultipleInstances, bool hasVisibleWindow)
        {
            try
            {
                Directory.CreateDirectory(_registryDirectory);

                string target = GetEntryFilePath();
                string temp = target + ".tmp";

                var builder = new StringBuilder();
                builder.Append("pid=").Append(_processId).Append('\n');
                builder.Append("hwnd=").Append(_windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture)).Append('\n');
                builder.Append("keep=").Append(keepInTray ? '1' : '0').Append('\n');
                builder.Append("multi=").Append(allowMultipleInstances ? '1' : '0').Append('\n');
                builder.Append("visible=").Append(hasVisibleWindow ? '1' : '0').Append('\n');
                builder.Append("title=").Append(title).Append('\n');

                File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
                File.Move(temp, target, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Writing the multi-instance registry entry failed: {ex.Message}");
            }
        }

        private bool TryReadEntry(string path, out InstanceInfo info)
        {
            info = new InstanceInfo();

            try
            {
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }

                string? title = null;
                int processId = 0;
                long windowHandle = 0;
                bool keepInTray = false;
                bool allowMultipleInstances = false;
                bool hasVisibleWindow = true;

                foreach (string line in text.Split('\n'))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).TrimEnd('\r');

                    switch (key)
                    {
                        case "pid":
                            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId);
                            break;
                        case "hwnd":
                            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out windowHandle);
                            break;
                        case "keep":
                            keepInTray = value == "1";
                            break;
                        case "multi":
                            allowMultipleInstances = value == "1";
                            break;
                        case "visible":
                            hasVisibleWindow = value != "0";
                            break;
                        case "title":
                            title = value;
                            break;
                    }
                }

                if (processId <= 0 || windowHandle == 0) return false;
                if (!IsProcessAlive(processId)) return false;

                info = new InstanceInfo
                {
                    ProcessId = processId,
                    WindowHandle = windowHandle,
                    Title = title ?? string.Empty,
                    KeepInTray = keepInTray,
                    AllowMultipleInstances = allowMultipleInstances,
                    HasVisibleWindow = hasVisibleWindow
                };
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reading a multi-instance registry entry failed: {ex.Message}");
                return false;
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            if (processId == Environment.ProcessId) return true;

            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return false;

                // PID 재사용으로 다른 프로세스를 인스턴스로 오인하지 않도록 이름을 확인합니다.
                return string.Equals(process.ProcessName, CurrentProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveCurrentProcessName()
        {
            try
            {
                return Process.GetCurrentProcess().ProcessName;
            }
            catch
            {
                return "Uviewer";
            }
        }

        private static string NormalizeTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;

            return title
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static void TryDeleteEntryFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Deleting a multi-instance registry entry failed: {ex.Message}");
            }
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string messageName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint processId);
    }
}
