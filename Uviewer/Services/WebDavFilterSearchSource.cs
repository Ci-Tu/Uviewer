using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Models;

namespace Uviewer.Services
{
    /// <summary>
    /// WebDAV 원격 폴더를 여러 요청으로 동시에 탐색하며, 필터와 일치하는 항목을 찾는 즉시 알려 주는 검색 소스.
    /// </summary>
    internal sealed class WebDavFilterSearchSource : IExplorerRemoteSearchSource
    {
        private const int MaxParallelListings = 4;
        private const int MaxResults = 500;
        private const int MaxFolders = 500;

        private readonly WebDavService _service;
        private readonly ExplorerState _state;
        private readonly Func<bool> _isActive;
        private readonly Func<string?> _currentPath;

        public WebDavFilterSearchSource(
            WebDavService service,
            ExplorerState state,
            Func<bool> isActive,
            Func<string?> currentPath)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
            _currentPath = currentPath ?? throw new ArgumentNullException(nameof(currentPath));
        }

        public bool CanSearch => _isActive() && _service.IsConnected && !string.IsNullOrEmpty(_currentPath());

        public Task SearchAsync(
            string filterText,
            ExplorerFilterKind kind,
            Action<IReadOnlyList<FileItem>> onMatches,
            CancellationToken token)
        {
            var rootPath = _currentPath();
            if (string.IsNullOrEmpty(rootPath) || !_service.IsConnected) return Task.CompletedTask;

            // 현재 폴더의 항목은 이미 표시되어 있으므로 하위 폴더부터 탐색합니다.
            var startFolders = _state.AllItems
                .Where(item => item.IsDirectory && !item.IsParentDirectory && item.IsWebDav)
                .Select(item => string.IsNullOrEmpty(item.WebDavPath) ? item.FullPath : item.WebDavPath!)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (startFolders.Count == 0) return Task.CompletedTask;

            return SearchLevelsAsync(rootPath, startFolders, filterText, kind, onMatches, token);
        }

        /// <summary>
        /// 폴더 단위로 너비 우선 탐색을 진행하며, 각 단계의 폴더들을 동시에 조회합니다.
        /// 한 폴더를 읽을 때마다 일치 항목을 즉시 onMatches로 넘겨 결과가 나오는 순서대로 표시되게 합니다.
        /// </summary>
        private async Task SearchLevelsAsync(
            string rootPath,
            IReadOnlyList<string> startFolders,
            string filterText,
            ExplorerFilterKind kind,
            Action<IReadOnlyList<FileItem>> onMatches,
            CancellationToken token)
        {
            var visited = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<string>();
            foreach (var folder in startFolders)
            {
                if (visited.TryAdd(NormalizePath(folder), folder)) pending.Add(folder);
            }

            var counters = new int[2]; // 0: 찾은 항목 수, 1: 조회한 폴더 수

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (counters[0] >= MaxResults || counters[1] >= MaxFolders) break;

                var nextLevel = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                await Parallel.ForEachAsync(
                    pending,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxParallelListings,
                        CancellationToken = token
                    },
                    async (folder, ct) =>
                    {
                        if (Interlocked.Increment(ref counters[1]) > MaxFolders) return;

                        var items = await _service.ListFolderAsync(folder, ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested || items.Count == 0) return;

                        var matches = new List<FileItem>();
                        foreach (var remote in items)
                        {
                            var normalized = NormalizePath(remote.FullPath);
                            if (remote.IsDirectory && visited.TryAdd(normalized, remote.FullPath))
                            {
                                nextLevel.TryAdd(normalized, remote.FullPath);
                            }

                            var fileItem = CreateCandidate(remote);
                            if (fileItem == null) continue;
                            if (!FileExplorerService.MatchesFilter(fileItem, filterText, kind)) continue;

                            fileItem.Name = ToRelativeDisplayName(rootPath, remote.FullPath);
                            matches.Add(fileItem);
                        }

                        if (matches.Count == 0) return;

                        Interlocked.Add(ref counters[0], matches.Count);
                        onMatches(matches);
                    }).ConfigureAwait(false);

                pending = nextLevel.Values.ToList();
            }
        }

        private static FileItem? CreateCandidate(WebDavItem remote)
        {
            var kind = FileExplorerService.GetSupportedFileKind(remote.Name);
            if (!remote.IsDirectory && kind == SupportedFileKind.Unsupported) return null;

            var fileItem = new FileItem
            {
                Name = remote.Name,
                FullPath = remote.FullPath,
                IsDirectory = remote.IsDirectory,
                IsWebDav = true,
                WebDavPath = remote.FullPath
            };
            FileExplorerService.ApplyFileKind(fileItem, kind);
            return fileItem;
        }

        /// <summary>현재 폴더 기준 상대 경로를 표시 이름으로 만듭니다(예: 하위\파일.png).</summary>
        private static string ToRelativeDisplayName(string rootPath, string remotePath)
        {
            var root = rootPath.EndsWith("/", StringComparison.Ordinal) ? rootPath : rootPath + "/";
            var path = remotePath.TrimEnd('/');

            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(root.Length).Replace('/', '\\');
            }

            var lastSlash = path.LastIndexOf('/');
            return lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
        }

        private static string NormalizePath(string remotePath) => remotePath.TrimEnd('/');
    }
}
