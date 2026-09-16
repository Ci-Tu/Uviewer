using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Models;

namespace Uviewer.Services
{
    /// <summary>
    /// 현재 위치가 원격(WebDAV) 폴더일 때 하위 폴더까지 필터 검색을 수행하는 소스.
    /// </summary>
    public interface IExplorerRemoteSearchSource
    {
        /// <summary>지금 바로 검색할 수 있는 상태인지 여부.</summary>
        bool CanSearch { get; }

        /// <summary>
        /// 하위 폴더를 검색하며, 일치하는 항목을 찾는 즉시 onMatches로 배치 전달합니다.
        /// onMatches는 작업 스레드에서 호출될 수 있으므로 호출자가 UI 스레드로 넘겨야 합니다.
        /// </summary>
        Task SearchAsync(
            string filterText,
            ExplorerFilterKind kind,
            Action<IReadOnlyList<FileItem>> onMatches,
            CancellationToken token);
    }
}
