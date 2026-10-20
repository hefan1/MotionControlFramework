using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>按逻辑轴名称等待并占用单轴或轴组，供同一台设备的并行流程共用。</summary>
    internal sealed class AxisResourceCoordinator : IAxisResourceCoordinator
    {
        /// <summary>每根逻辑轴对应一个占用信号量；名称不区分大小写。</summary>
        private readonly Dictionary<string, SemaphoreSlim> _axisLocks;

        /// <summary>为配置中的每根逻辑轴建立独立占用信号量。</summary>
        public AxisResourceCoordinator(IEnumerable<string> logicalAxisNames)
        {
            if (logicalAxisNames == null)
            {
                throw new ArgumentNullException(nameof(logicalAxisNames));
            }

            _axisLocks = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
            foreach (string axisName in logicalAxisNames)
            {
                if (string.IsNullOrWhiteSpace(axisName))
                {
                    throw new ArgumentException("逻辑轴资源名称不能为空。", nameof(logicalAxisNames));
                }

                if (!_axisLocks.ContainsKey(axisName))
                {
                    _axisLocks.Add(axisName, new SemaphoreSlim(1, 1));
                }
            }
        }

        /// <summary>等待并占用一根逻辑轴。</summary>
        public Task<IDisposable> AcquireAxisAsync(
            string logicalAxisName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(logicalAxisName))
            {
                throw new ArgumentException("逻辑轴名称不能为空。", nameof(logicalAxisName));
            }

            return AcquireAxisGroupAsync(new[] { logicalAxisName }, cancellationToken);
        }

        /// <summary>按固定名称顺序等待并同时占用一组逻辑轴。</summary>
        public async Task<IDisposable> AcquireAxisGroupAsync(
            IEnumerable<string> logicalAxisNames,
            CancellationToken cancellationToken)
        {
            if (logicalAxisNames == null)
            {
                throw new ArgumentNullException(nameof(logicalAxisNames));
            }

            var uniqueAxisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string axisName in logicalAxisNames)
            {
                if (string.IsNullOrWhiteSpace(axisName))
                {
                    throw new ArgumentException("逻辑轴名称不能为空。", nameof(logicalAxisNames));
                }

                if (!_axisLocks.ContainsKey(axisName))
                {
                    throw new KeyNotFoundException("找不到可申请的逻辑轴资源：'" + axisName + "'。");
                }

                uniqueAxisNames.Add(axisName);
            }

            if (uniqueAxisNames.Count == 0)
            {
                throw new ArgumentException("轴组至少要包含一根逻辑轴。", nameof(logicalAxisNames));
            }

            var orderedAxisNames = uniqueAxisNames
                .OrderBy(axisName => axisName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var acquiredLocks = new List<SemaphoreSlim>();

            try
            {
                foreach (string axisName in orderedAxisNames)
                {
                    SemaphoreSlim axisLock = _axisLocks[axisName];
                    await axisLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    acquiredLocks.Add(axisLock);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new AxisResourceLease(acquiredLocks);
            }
            catch
            {
                ReleaseAxisLocks(acquiredLocks);
                throw;
            }
        }

        /// <summary>按相反顺序释放当前已申请的轴，避免取消等待后遗留占用。</summary>
        private static void ReleaseAxisLocks(IReadOnlyList<SemaphoreSlim> axisLocks)
        {
            for (int index = axisLocks.Count - 1; index >= 0; index--)
            {
                axisLocks[index].Release();
            }
        }

        /// <summary>持有一组已占用的轴，并在释放时将它们全部归还。</summary>
        private sealed class AxisResourceLease : IDisposable
        {
            /// <summary>本次占用成功申请的信号量，释放后不再使用。</summary>
            private readonly IReadOnlyList<SemaphoreSlim> _axisLocks;

            /// <summary>记录占用凭证是否已释放，保证重复释放不会多次归还轴。</summary>
            private int _isDisposed;

            /// <summary>保存本次已申请的轴信号量。</summary>
            public AxisResourceLease(IReadOnlyList<SemaphoreSlim> axisLocks)
            {
                _axisLocks = axisLocks;
            }

            /// <summary>释放本次占用的全部逻辑轴；重复调用不会重复释放。</summary>
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                {
                    return;
                }

                ReleaseAxisLocks(_axisLocks);
            }
        }
    }
}
