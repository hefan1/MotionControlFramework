using System;
using System.Globalization;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>对锁存输入和轴状态进行设备采样；设备读取不占用任务步骤门。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>读取任务门外的设备数据，并登记在途操作供暂停收尾等待。</summary>
        private async Task<T> SampleDeviceAsync<T>(TaskExecutionScope scope, Func<T> read, Action<long> captureVersion)
        {
            IDisposable pending = null;
            try
            {
                await scope.StepAsync(() =>
                {
                    captureVersion(_run.PauseVersion);
                    pending = _run.RegisterInFlightOperation();
                }).ConfigureAwait(false);
                return await Task.Run(read).ConfigureAwait(false);
            }
            catch
            {
                scope.RetainFailureHandoff(pending);
                pending = null;
                throw;
            }
            finally { pending?.Dispose(); }
        }

        /// <summary>两轮读取锁存数据并核对完成/成功信号，暂停期间旧采样不得写入变量。</summary>
        private async Task ReadInputWordAsync(TaskExecutionScope scope, ReadInputWordNode node)
        {
            bool committed = false;
            bool timeoutReportedThisAttempt = false;
            while (!committed)
            {
                try
                {
                    long version = 0;
                    int number = 0;
                    string readError = null;
                    try { number = await SampleDeviceAsync(scope, () => ReadStableInputWord(node), value => version = value).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (scope.Token.IsCancellationRequested) { throw; }
                    catch (TaskExecutionException) { throw; }
                    catch (Exception error) { readError = error.Message; scope.CompleteFailureHandoff(); }
                    await scope.StepAsync(() => _run.TryCommitSample(version, () =>
                    {
                        if (readError != null)
                        {
                            scope.WaitDetail = "等待读取锁存数据：" + readError;
                            _run.Report(scope, TaskNodeState.Waiting, scope.WaitDetail);
                            return;
                        }
                        string result = node.Prefix + number.ToString("D5", CultureInfo.InvariantCulture);
                        _device.WriteVariable(node.TargetVariable, result);
                        scope.Diagnostics = new TaskStepDiagnostics("已读取锁存数据并写入变量", new[] {
                            new TaskObservedValue(node.TargetVariable, "非零且稳定", result, true) });
                        committed = true;
                    })).ConfigureAwait(false);
                }
                catch (TaskExecutionException error) when (!timeoutReportedThisAttempt
                    && error.NodeId == scope.Node.Id && scope.IsOwnTimeoutOwnerExpired && error.InnerException is TimeoutException)
                {
                    bool stopRequired = ReportTimeoutAlarms(scope, node.TimeoutAlarmCodes, "读取锁存数据超时；" + scope.WaitDetail);
                    timeoutReportedThisAttempt = true;
                    scope.TimeoutAlarmsReported = true;
                    if (stopRequired) throw;
                    scope.IgnoreOwnTimeout();
                    scope.CompleteFailureHandoff();
                }
                if (!committed) await scope.NextPollAsync().ConfigureAwait(false);
            }
            if (scope.TimeoutAlarmsReported)
            {
                ClearTimeoutAlarms(scope, node.TimeoutAlarmCodes, "已读取稳定数据。");
                scope.TimeoutAlarmsReported = false;
            }
        }

        /// <summary>按低位到高位读取两轮；任一输入未知、变化、失效或为零均拒绝。</summary>
        private int ReadStableInputWord(ReadInputWordNode node)
        {
            if (node.InputNames == null || node.InputNames.Count != 16)
                throw new InvalidOperationException("读取锁存数据需要 16 路输入。");
            int first = ReadInputWordPass(node);
            int second = ReadInputWordPass(node);
            if (first == 0 || first != second) throw new InvalidOperationException("设备数据为零或两次采样不一致；未写入变量。 ");
            return first;
        }

        /// <summary>一次采样前后均检查设备完成与成功信号。</summary>
        private int ReadInputWordPass(ReadInputWordNode node)
        {
            CheckWordReady(node);
            int result = 0;
            for (int bit = 0; bit < node.InputNames.Count; bit++)
            {
                bool? value = _device.ReadInput(node.InputNames[bit]);
                if (!value.HasValue) throw new InvalidOperationException("数据输入未知：" + node.InputNames[bit]);
                if (value.Value) result |= 1 << bit;
            }
            CheckWordReady(node);
            return result;
        }

        /// <summary>完成和成功都明确有效才允许采样；未知不视作有效。</summary>
        private void CheckWordReady(ReadInputWordNode node)
        {
            if (_device.ReadInput(node.ValidInputName) != true || _device.ReadInput(node.SuccessInputName) != true)
                throw new InvalidOperationException("设备完成或成功输入无效，拒绝读取锁存数据。 ");
        }

        /// <summary>持续读取轴位置，未知、报警、未回零或未停稳均不能通过。</summary>
        private async Task WaitAxisPositionAsync(TaskExecutionScope scope, WaitAxisPositionNode node)
        {
            bool complete = false;
            bool timeoutReportedThisAttempt = false;
            while (!complete)
            {
                try
                {
                    long version = 0;
                    TaskAxisState state = null;
                    string readError = null;
                    try { state = await SampleDeviceAsync(scope, () => _device.ReadAxis(node.AxisName), value => version = value).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (scope.Token.IsCancellationRequested) { throw; }
                    catch (TaskExecutionException) { throw; }
                    catch (Exception error) { readError = error.Message; scope.CompleteFailureHandoff(); }
                    await scope.StepAsync(() => _run.TryCommitSample(version, () =>
                    {
                        complete = readError == null && state != null && state.Enabled && state.Homed && !state.IsBusy
                            && string.IsNullOrWhiteSpace(state.Alarm) && !double.IsNaN(state.Position) && !double.IsInfinity(state.Position)
                            && Math.Abs(state.Position - node.Position) <= node.Tolerance;
                        scope.WaitDetail = "等待轴 " + node.AxisName + " 停在 " + node.Position + " ± " + node.Tolerance
                            + "；当前=" + (state == null ? "未知" : state.Position.ToString(CultureInfo.InvariantCulture))
                            + (readError == null ? "" : "；读取失败：" + readError);
                        scope.Diagnostics = new TaskStepDiagnostics(scope.WaitDetail);
                        _run.Report(scope, TaskNodeState.Waiting, scope.WaitDetail);
                    })).ConfigureAwait(false);
                }
                catch (TaskExecutionException error) when (!timeoutReportedThisAttempt
                    && error.NodeId == scope.Node.Id && scope.IsOwnTimeoutOwnerExpired && error.InnerException is TimeoutException)
                {
                    bool stopRequired = ReportTimeoutAlarms(scope, node.TimeoutAlarmCodes, "等待轴位置超时；" + scope.WaitDetail);
                    timeoutReportedThisAttempt = true;
                    scope.TimeoutAlarmsReported = true;
                    if (stopRequired) throw;
                    scope.IgnoreOwnTimeout();
                    scope.CompleteFailureHandoff();
                }
                if (!complete) await scope.NextPollAsync().ConfigureAwait(false);
            }
            if (scope.TimeoutAlarmsReported)
            {
                ClearTimeoutAlarms(scope, node.TimeoutAlarmCodes, "轴位置已满足。");
                scope.TimeoutAlarmsReported = false;
            }
        }
    }
}
