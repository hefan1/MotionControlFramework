using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Execution
{
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>同一次轴动作的暂停与普通收尾共享结果，并保留固定动作凭证。</summary>
        private sealed class MotionFinish
        {
            /// <summary>保护首次收尾任务的发布。</summary>
            private readonly object _sync = new object();
            /// <summary>动作所属设备。</summary>
            private readonly ITaskDevice _device;
            /// <summary>动作接受时冻结的凭证。</summary>
            private readonly ITaskAxisMove _move;
            /// <summary>动作持有的临时轴或父范围。</summary>
            private readonly TaskResourceRequest _resource;
            /// <summary>用于故障定位的逻辑轴名称。</summary>
            private readonly string _axisName;
            /// <summary>首次启动的收尾任务；后续回调只能等待它。</summary>
            private Task _endTask;

            /// <summary>保存一个动作及其资源，不调用设备。</summary>
            internal MotionFinish(ITaskDevice device, ITaskAxisMove move, TaskResourceRequest resource, string axisName)
            { _device = device; _move = move; _resource = resource; _axisName = axisName; }

            /// <summary>只发起一次收尾；在线程池调用设备，避免同步实现堵住其他停轴请求。</summary>
            internal Task EndAsync(bool completed)
            {
                lock (_sync)
                {
                    if (_endTask == null) _endTask = Task.Run(() => EndCoreAsync(completed));
                    return _endTask;
                }
            }

            /// <summary>设备收尾失败时先隔离资源，再上报原始错误。</summary>
            private async Task EndCoreAsync(bool completed)
            {
                try { await _device.EndMoveAsync(_move, completed, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error)
                {
                    BlockUnconfirmed(_device, _resource, _axisName, error);
                    if (error is TaskMoveCleanupPendingException pending)
                    {
                        try { await pending.PendingCleanup.ConfigureAwait(false); }
                        catch (Exception cleanupError) { throw new AggregateException("轴停止和原动作收尾均失败：" + _axisName, error, cleanupError); }
                    }
                    throw;
                }
            }

            /// <summary>不能确认轴安全时保留临时轴或父范围；隔离失败时同时保留两项错误。</summary>
            internal static void BlockUnconfirmed(ITaskDevice device, TaskResourceRequest resource, string axisName, Exception error)
            {
                if (resource == null) return;
                try { device.Resources.Block(resource, "轴状态未确认安全：" + axisName + "，" + error.Message); }
                catch (Exception blockError) { throw new AggregateException("轴状态未确认且无法隔离资源：" + axisName, error, blockError); }
            }
        }
    }
}
