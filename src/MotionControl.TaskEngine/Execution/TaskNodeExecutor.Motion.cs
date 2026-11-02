using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>执行设备单轴定位和回零动作；轴组动作由专用节点处理。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>在动作接受时读取目标，随后固定绝对终点，恢复不会再读变量或叠加相对距离。</summary>
        private Task MoveAsync(TaskExecutionScope scope, MoveAxisNode node)
        {
            return RunMotionAsync(scope, node.AxisName, node, false);
        }

        /// <summary>回零或确认停止；具体指令由设备实现发出。</summary>
        private async Task AxisCommandAsync(TaskExecutionScope scope, AxisCommandNode node)
        {
            if (node.Command == TaskAxisCommand.Home)
            {
                await RunMotionAsync(scope, node.AxisName, null, true).ConfigureAwait(false);
                return;
            }

            // 先经过运行门控（暂停与时限），再向设备确认轴已闲置；确认可能需要读取真实轴状态。
            IDisposable confirmation = null;
            try
            {
                await scope.StepAsync(() => confirmation = _run.RegisterInFlightOperation()).ConfigureAwait(false);
                await _device.ConfirmStoppedAsync(node.AxisName, scope.Token).ConfigureAwait(false);
            }
            catch
            {
                scope.RetainFailureHandoff(confirmation);
                confirmation = null;
                throw;
            }
            finally { confirmation?.Dispose(); }
        }

        /// <summary>把任务定义的速度曲线转换为设备端可用的运动曲线，单位沿用工程单位。</summary>
        private static AxisMotionProfile ToAxisProfile(TaskMotionProfile profile)
        {
            return new AxisMotionProfile(profile.Speed, profile.Acceleration, profile.Deceleration);
        }

        /// <summary>按活动时间推进动作，finally 确保失败与取消均停止轴并释放本次轴占用。</summary>
        private async Task RunMotionAsync(TaskExecutionScope scope, string axisName, MoveAxisNode node, bool home)
        {
            TaskResourceRequest acquired = null;
            try
            {
                acquired = await AcquireActionAsync(scope, "Axis:" + axisName).ConfigureAwait(false);
                while (true)
                {
                    ITaskAxisMove operation = null;
                    MotionFinish finish = null;
                    IDisposable pauseStopRegistration = null;
                    bool restartAfterPause = false;
                    IDisposable completionSettled = null;
                    int stoppedForPause = 0;
                    double started = 0;
                    double duration = 0;
                    try
                    {
                        await scope.StepAsync(() =>
                        {
                            double target = 0;
                            bool acceptedBefore = !home && scope.AcceptedAxisTargets.TryGetValue(axisName, out target);
                            if (!home && !acceptedBefore) target = TaskRuntimeValues.Number(ReadRequired(node.Target));
                            try
                            {
                                operation = _device.BeginMove(axisName, target, !home && !acceptedBefore && node.Mode == TaskMoveMode.Relative, home, home ? null : ToAxisProfile(node.Profile));
                                if (operation == null) throw new InvalidOperationException("设备未返回轴动作凭证：" + axisName);
                            }
                            catch (TaskMoveRejectedBeforeCommandException)
                            {
                                // 设备明确保证没有发出命令，可以按普通前置条件失败处理。
                                throw;
                            }
                            catch (Exception error)
                            {
                                // 驱动可能已发命令，异常或空凭证都不能证明轴安全。
                                try { MotionFinish.BlockUnconfirmed(_device, acquired ?? scope.ResourceLease, axisName, error); }
                                catch { _run.MarkUnconfirmedPauseFailure(); throw; }
                                throw;
                            }
                            finish = new MotionFinish(_device, operation, acquired ?? scope.ResourceLease, axisName);
                            MotionFinish acceptedFinish = finish;
                            pauseStopRegistration = _run.RegisterPauseStopAction(() =>
                            {
                                Interlocked.Exchange(ref stoppedForPause, 1);
                                return acceptedFinish.EndAsync(false);
                            });
                            if (!home) scope.AcceptedAxisTargets[axisName] = operation.Target;
                            started = _run.ActiveMilliseconds;
                            // 100ms 仅用于内存设备推进；实际设备以动作反馈判断回零完成。
                            duration = home ? 100 : Math.Abs(operation.Target - operation.Start) / node.Profile.Speed * 1000;
                            if (double.IsInfinity(duration) || double.IsNaN(duration)) throw new InvalidOperationException("运动时长超出有限范围。");
                        }).ConfigureAwait(false);

                        while (operation != null)
                        {
                            bool completed = false;
                            await scope.StepAsync(() =>
                            {
                                if (Interlocked.CompareExchange(ref stoppedForPause, 0, 0) != 0)
                                {
                                    restartAfterPause = true;
                                    return;
                                }

                                double progress = duration == 0 ? 1 : (_run.ActiveMilliseconds - started) / duration;
                                _device.AdvanceMove(operation, progress);
                                if (!home)
                                {
                                    ObserveAxes(scope, scope.AcceptedAxisTargets);
                                    _run.Report(scope, TaskNodeState.Moving, "运动中：等待轴 " + axisName + " 到位；目标=" + operation.Target);
                                    completed = _device.IsMoveComplete(operation, progress);
                                }
                                else
                                {
                                    // 回零可能远超内存模型的 100ms，只有设备报告完成才能显示完成。
                                    completed = _device.IsMoveComplete(operation, progress);
                                    scope.Diagnostics = new TaskStepDiagnostics(completed
                                        ? "轴回零已完成。"
                                        : "正在等待轴回零完成。");
                                    _run.Report(scope, TaskNodeState.Moving, "正在等待轴 " + axisName + " 回零完成。");
                                }
                                if (completed) completionSettled = _run.RegisterInFlightOperation();
                            }).ConfigureAwait(false);

                            if (restartAfterPause)
                            {
                                await _run.WaitForResumeAsync().ConfigureAwait(false);
                                operation = null;
                                break;
                            }

                            if (completed)
                            {
                                operation = null;
                                try { await finish.EndAsync(true).ConfigureAwait(false); }
                                catch
                                {
                                    _run.MarkUnconfirmedPauseFailure();
                                    scope.RetainFailureHandoff(completionSettled);
                                    completionSettled = null;
                                    throw;
                                }
                                finally { completionSettled?.Dispose(); completionSettled = null; }
                                break;
                            }

                            await scope.NextPollAsync().ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (_run.IsPaused && !scope.Token.IsCancellationRequested)
                    {
                        // 真实轴在暂停回调中已受控停稳；恢复后按原固定绝对终点重新接受动作。
                        await _run.WaitForResumeAsync().ConfigureAwait(false);
                        operation = null;
                    }
                    finally
                    {
                        pauseStopRegistration?.Dispose();
                        if (operation != null)
                        {
                            try { await finish.EndAsync(false).ConfigureAwait(false); }
                            catch { _run.MarkUnconfirmedPauseFailure(); throw; }
                        }
                    }

                    if (!restartAfterPause) break;
                }
            }
            finally
            {
                // 暂停期间保留轴资源，防止其他工位在该轴中途位置插入动作。
                if (acquired != null) _device.Resources.Release(acquired);
            }
        }
    }
}
