using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>在已有任务会话内执行独立准备任务；准备不创建生产工位、不清空设备变量。</summary>
    public sealed partial class TaskMachineController
    {
        /// <summary>配置要求的准备任务及其最终现场状态检查。</summary>
        private readonly Dictionary<string, Action> _preparationChecks = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
        /// <summary>本会话确实执行成功的准备任务，不从磁盘恢复完成标记。</summary>
        private readonly HashSet<string> _completedPreparationTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>当前准备任务；与生产共用同一设备命令权。</summary>
        private TaskRun _preparationRun;
        /// <summary>包括执行前检查在内的准备占用，防止启动或复位插入。</summary>
        private bool _preparationActive;
        /// <summary>准备执行器已完整收尾的信号，关闭会话时必须等待。</summary>
        private TaskCompletionSource<bool> _preparationSettled;

        /// <summary>在首次生产前登记必须完成的准备任务；检查委托只读取现场状态，不发送动作。</summary>
        public void RegisterPreparationTask(string taskId, Action validateReady)
        {
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("准备任务编号不能为空。", nameof(taskId));
            if (validateReady == null) throw new ArgumentNullException(nameof(validateReady));
            lock (_sync)
            {
                ValidatePreparationAllowedCore();
                if (!_library.ContainsKey(taskId)) throw new InvalidOperationException("准备任务不存在：" + taskId);
                if (_preparationChecks.ContainsKey(taskId)) throw new InvalidOperationException("准备任务重复登记：" + taskId);
                _preparationChecks.Add(taskId, validateReady);
            }
        }

        /// <summary>核对可以准备的会话阶段；生产启动过后禁止重新准备，以免改变保留的生产步骤和在制品。</summary>
        public void ValidatePreparationAllowed()
        {
            lock (_sync) ValidatePreparationAllowedCore();
        }

        /// <summary>调用方持锁时检查准备与生产、关闭及其他准备命令互斥。</summary>
        private void ValidatePreparationAllowedCore()
        {
            RequireOpen();
            if (_workersStarted) throw new InvalidOperationException("本任务会话已开始生产，不能重新回零；请先按设备工艺处理在制品并结束生产会话。");
            if (_preparationActive) throw new InvalidOperationException("已有工位准备任务正在执行。");
            if (_state == TaskMachineState.Running || _state == TaskMachineState.Pausing)
                throw new InvalidOperationException("任务仍在运行或停止处理中，不能执行工位准备。");
        }

        /// <summary>执行一次已登记准备任务，实时报告节点；取消后等待轴收尾，成功后仍不启动生产。</summary>
        public Task ExecutePreparationTaskAsync(string taskId, Action<TaskRunSnapshot> report, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                ValidatePreparationAllowedCore();
                if (!_preparationChecks.ContainsKey(taskId)) throw new InvalidOperationException("准备任务尚未登记：" + taskId);
                _completedPreparationTasks.Remove(taskId);
                _preparationActive = true;
                _preparationSettled = NewSignal();
            }
            return Task.Run(() => ExecutePreparationCoreAsync(taskId, report, cancellationToken));
        }

        /// <summary>冻结并运行准备任务，保留失败上下文，所有出口都等待任务退出后解除准备占用。</summary>
        private async Task ExecutePreparationCoreAsync(string taskId, Action<TaskRunSnapshot> report, CancellationToken cancellationToken)
        {
            TaskRun run = null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token))
            {
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    AlarmService.EnsureStopAlarmConditionsRecovered();
                    if (AlarmService.HasLatchedStopAlarm) throw new InvalidOperationException("停机报警尚未复位，不能开始工位准备。");
                    lock (_sync)
                    {
                        run = new TaskExecutionEngine().StartPreparation(_library[taskId], _device,
                            _library.Values.Where(item => !string.Equals(item.TaskId, taskId, StringComparison.OrdinalIgnoreCase)), this, linked.Token);
                        _preparationRun = run;
                    }
                    while (!run.Completion.IsCompleted)
                    {
                        report?.Invoke(run.GetSnapshot());
                        await Task.WhenAny(run.Completion, Task.Delay(100)).ConfigureAwait(false);
                    }
                    TaskRunSnapshot result = await run.Completion.ConfigureAwait(false);
                    report?.Invoke(result);
                    linked.Token.ThrowIfCancellationRequested();
                    if (result.State == TaskRunState.Cancelled) throw new OperationCanceledException("工位准备已停止。");
                    if (result.State != TaskRunState.Completed)
                        throw new InvalidOperationException("准备任务失败：" + taskId + "。" + result.Error?.Message, result.Error);
                    _preparationChecks[taskId]();
                    linked.Token.ThrowIfCancellationRequested();
                    lock (_sync) _completedPreparationTasks.Add(taskId);
                }
                finally
                {
                    if (run != null && !run.Completion.IsCompleted)
                    {
                        run.Cancel();
                        await run.Completion.ConfigureAwait(false);
                    }
                    lock (_sync)
                    {
                        _preparationRun = null;
                        _preparationActive = false;
                        _preparationSettled.TrySetResult(true);
                    }
                }
            }
        }

        /// <summary>首次生产前核对所有配置的准备均执行成功且现场仍满足条件；暂停续跑保留原工艺状态。</summary>
        private void EnsurePreparationCompletedForStart()
        {
            if (_preparationActive) throw new InvalidOperationException("工位准备尚未停止，不能启动生产。");
            if (_workersStarted) return;
            foreach (var item in _preparationChecks)
            {
                if (!_completedPreparationTasks.Contains(item.Key)) throw new InvalidOperationException("请先完成工位准备：" + item.Key);
                item.Value();
            }
        }
    }
}
