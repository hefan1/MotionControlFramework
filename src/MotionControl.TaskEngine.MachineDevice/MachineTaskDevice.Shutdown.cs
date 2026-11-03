using System;
using System.Linq;
using System.Threading.Tasks;

namespace MotionControl.TaskEngine.MachineDevice
{
    /// <summary>保留待确认资源及设备控制权，供 Host 安全退出而不是恢复生产。</summary>
    public sealed partial class MachineTaskDevice
    {
        /// <summary>任务已终止后冻结并复读资源记录，再验证控制器输出和轴停稳并封存运行时。</summary>
        public async Task PreserveBlockedForShutdownAsync(object owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            lock (_sync)
            {
                if (!ReferenceEquals(_machineOwner, owner)) throw new InvalidOperationException("任务会话控制权已变化，不能交接退出。");
                if (_closing) throw new InvalidOperationException("任务设备正在关闭或交接，不能重复操作。");
                RemoveSettledMoves();
                if (_activeRuns.Count != 0 || _unsettledMoves.Count != 0 || _pendingStops.Count != 0 || _beginningAxes.Count != 0
                    || _pendingOutputWrites.Count != 0)
                    throw new InvalidOperationException("任务、轴动作或停止调用仍在收尾，不能交接退出。");
                var blockedOutputs = _resources.Read().SelectMany(item => item.Resources)
                    .Where(name => name.StartsWith("Output:", StringComparison.OrdinalIgnoreCase))
                    .Select(name => name.Substring(7)).ToArray();
                if (_unconfirmedOutputs.Any(name => !blockedOutputs.Contains(name, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("存在未保存隔离记录的未知输出，不能交接退出。");
                _closing = true;
            }
            bool frozen = false;
            try
            {
                await _resources.FreezeBlockedForShutdownAsync().ConfigureAwait(false);
                frozen = true;
                // 运行时保留原控制权，禁止新动作；断卡失败后仍不可继续生产。
                await _runtime.SealTaskEngineShutdownAsync(this, Catalog.Outputs.ToArray()).ConfigureAwait(false);
            }
            catch
            {
                if (!_runtime.IsShutdownSealed)
                {
                    if (frozen) _resources.CancelShutdownFreeze();
                    lock (_sync) _closing = false;
                }
                throw;
            }
        }
    }
}
