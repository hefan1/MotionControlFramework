using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;

namespace MotionControl.Framework.Runtime.FlowScheduling
{
    /// <summary>并行运行设备项目提供的各工位步骤，并在暂停时等待所有当前步骤退出。</summary>
    internal sealed class StationFlowScheduler
    {
        private readonly IReadOnlyList<IStationFlow> _stationFlows;
        private readonly object _runSync = new object();
        private CancellationTokenSource _runCancellation;
        private Task _runTask;

        /// <summary>检查并保存本设备需要并行运行的工位流程。</summary>
        public StationFlowScheduler(IEnumerable<IStationFlow> stationFlows)
        {
            if (stationFlows == null)
            {
                throw new ArgumentNullException(nameof(stationFlows));
            }

            var flowList = stationFlows.ToList();
            if (flowList.Count == 0)
            {
                throw new ArgumentException("设备至少需要配置一个工位流程。", nameof(stationFlows));
            }

            var stationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var flow in flowList)
            {
                if (flow == null)
                {
                    throw new ArgumentException("工位流程列表中包含空对象。", nameof(stationFlows));
                }

                if (string.IsNullOrWhiteSpace(flow.StationName))
                {
                    throw new ArgumentException("工位流程没有配置 StationName。", nameof(stationFlows));
                }

                if (!stationNames.Add(flow.StationName))
                {
                    throw new ArgumentException(
                        "工位名称重复：'" + flow.StationName + "'。请检查设备流程配置。",
                        nameof(stationFlows));
                }
            }

            _stationFlows = flowList.AsReadOnly();
        }

        public IReadOnlyList<IStationFlow> StationFlows
        {
            get { return _stationFlows; }
        }

        public bool IsRunning
        {
            get
            {
                lock (_runSync)
                {
                    return _runTask != null && !_runTask.IsCompleted;
                }
            }
        }

        /// <summary>启动所有工位流程；各工位在独立任务中并行执行。</summary>
        public Task Start()
        {
            lock (_runSync)
            {
                if (_runTask != null && !_runTask.IsCompleted)
                {
                    throw new InvalidOperationException("工位流程已经在运行。");
                }

                if (_runCancellation != null)
                {
                    _runCancellation.Dispose();
                }

                _runCancellation = new CancellationTokenSource();
                _runTask = RunAllStationsAsync(_runCancellation);
                return _runTask;
            }
        }

        /// <summary>从报警通知等同步事件中请求暂停，不等待工位退出。</summary>
        public void RequestPause()
        {
            lock (_runSync)
            {
                if (_runCancellation != null && _runTask != null && !_runTask.IsCompleted)
                {
                    _runCancellation.Cancel();
                }
            }
        }

        /// <summary>请求所有工位暂停，并在指定时限内等待它们完成当前步骤。</summary>
        public async Task PauseAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "暂停等待时限必须大于 0。");
            }

            CancellationTokenSource cancellation;
            Task runTask;
            Exception cancellationError = null;

            lock (_runSync)
            {
                cancellation = _runCancellation;
                runTask = _runTask;
                if (cancellation == null || runTask == null)
                {
                    return;
                }

                try
                {
                    cancellation.Cancel();
                }
                catch (Exception error)
                {
                    cancellationError = error;
                }
            }

            Exception runError = null;
            try
            {
                using (var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var timeoutTask = Task.Delay(timeout, waitCancellation.Token);
                    var completedTask = await Task.WhenAny(runTask, timeoutTask).ConfigureAwait(false);
                    if (completedTask != runTask && !runTask.IsCompleted)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException(
                            "等待工位流程响应暂停超时；流程仍在后台运行，不能复位或断开运动控制卡。");
                    }

                    waitCancellation.Cancel();
                }

                await runTask.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                runError = error;
            }
            finally
            {
                if (runTask.IsCompleted)
                {
                    lock (_runSync)
                    {
                        if (ReferenceEquals(_runCancellation, cancellation))
                        {
                            _runCancellation = null;
                            _runTask = null;
                            cancellation.Dispose();
                        }
                    }
                }
            }

            if (runError is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw runError;
            }

            if (cancellationError != null && runError != null)
            {
                throw new AggregateException("请求暂停和等待工位退出时都发生错误。", cancellationError, runError);
            }

            if (runError != null)
            {
                throw new InvalidOperationException("工位流程暂停失败。", runError);
            }

            if (cancellationError != null)
            {
                throw new InvalidOperationException("请求工位流程暂停时发生错误。", cancellationError);
            }
        }

        /// <summary>并行启动所有工位，并在一个工位结束或出错时通知其他工位退出。</summary>
        private async Task RunAllStationsAsync(CancellationTokenSource cancellation)
        {
            var stationTasks = _stationFlows
                .Select(flow => Task.Run(() => RunStationAsync(flow, cancellation.Token), cancellation.Token))
                .ToArray();

            var firstCompleted = await Task.WhenAny(stationTasks).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested)
            {
                var pauseErrors = await WaitForStationsToStopAsync(stationTasks, true).ConfigureAwait(false);
                if (pauseErrors.Count > 0)
                {
                    throw new AggregateException("暂停工位流程时发生错误。", pauseErrors);
                }

                return;
            }

            var cancellationErrors = new List<Exception>();
            try
            {
                cancellation.Cancel();
            }
            catch (Exception cancellationError)
            {
                cancellationErrors.Add(cancellationError);
            }

            var failures = await WaitForStationsToStopAsync(stationTasks, false).ConfigureAwait(false);
            failures.AddRange(cancellationErrors);

            if (!firstCompleted.IsFaulted && !firstCompleted.IsCanceled)
            {
                failures.Insert(0, new InvalidOperationException(
                    "工位流程意外结束：自动运行期间 ExecuteCurrentStepAsync 不应正常返回整个流程。"));
            }

            if (failures.Count == 0)
            {
                failures.Add(new InvalidOperationException("工位流程异常结束，但没有提供具体错误信息。"));
            }

            throw new AggregateException("一个或多个工位流程发生错误，其他工位已请求暂停。", failures);
        }

        /// <summary>重复执行单个工位的当前步骤，并保留步骤执行异常。</summary>
        private static async Task RunStationAsync(IStationFlow stationFlow, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await stationFlow.ExecuteCurrentStepAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception stepError)
                {
                    throw new InvalidOperationException(
                        "工位 '" + stationFlow.StationName + "' 的步骤 '" + stationFlow.CurrentStepName + "' 执行失败。",
                        stepError);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>等待所有工位退出，并收集暂停或故障期间产生的异常。</summary>
        private static async Task<List<Exception>> WaitForStationsToStopAsync(
            Task[] stationTasks,
            bool cancellationWasRequested)
        {
            try
            {
                await Task.WhenAll(stationTasks).ConfigureAwait(false);
                return new List<Exception>();
            }
            catch (Exception waitError)
            {
                var failures = stationTasks
                    .Where(task => task.IsFaulted)
                    .SelectMany(task => task.Exception.InnerExceptions)
                    .ToList();

                if (failures.Count == 0 && !cancellationWasRequested)
                {
                    failures.Add(waitError);
                }

                return failures;
            }
        }
    }
}
