using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Host.Wpf.Recovery;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>验证报警交接的持久记录及整机复位门禁，不连接实际设备。</summary>
    internal static partial class RuntimeRegressionChecks
    {
        /// <summary>异常重启、损坏记录和待确认报警均不能放行，干净交接保持可重连。</summary>
        private static async Task AlarmContinuityChecksAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "AlarmContinuity_" + Guid.NewGuid().ToString("N"));
            var configuration = new MachineConfigurationStore(root);
            string machine = configuration.GetMachineDirectory("Machine01");
            try
            {
                var missing = new AlarmContinuityStore(configuration, "Machine01");
                missing.Open();
                Ensure(missing.BlockReason != null, "缺失交接记录被误认为安全。 ");
                missing.CloseIfSettled(true);
                Ensure(missing.BlockReason != null, "未知状态被普通关闭操作解除。 ");

                string corruptDirectory = Path.Combine(configuration.GetMachineDirectory("CorruptMachine"), "Runtime");
                Directory.CreateDirectory(corruptDirectory);
                string corruptPath = Path.Combine(corruptDirectory, "AlarmContinuity.clean");
                File.WriteAllText(corruptPath, "incomplete-record");
                var corrupt = new AlarmContinuityStore(configuration, "CorruptMachine");
                corrupt.Open();
                Ensure(corrupt.BlockReason != null && File.ReadAllText(corruptPath) == "incomplete-record",
                    "损坏的交接记录被覆盖或错误放行。 ");

                string lockedDirectory = Path.Combine(configuration.GetMachineDirectory("LockedMachine"), "Runtime");
                Directory.CreateDirectory(lockedDirectory);
                string lockedPath = Path.Combine(lockedDirectory, "AlarmContinuity.clean");
                File.WriteAllText(lockedPath, "MC-ALARM-CONTINUITY|1|LockedMachine");
                using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var unavailable = new AlarmContinuityStore(configuration, "LockedMachine");
                    unavailable.Open();
                    Ensure(unavailable.BlockReason != null, "记录被占用时错误放行。 ");
                }

                string recordDirectory = Path.Combine(machine, "Runtime");
                Directory.CreateDirectory(recordDirectory);
                string clean = Path.Combine(recordDirectory, "AlarmContinuity.clean");
                string pending = Path.Combine(recordDirectory, "AlarmContinuity.pending");
                File.WriteAllText(clean, "MC-ALARM-CONTINUITY|1|Machine01");
                var current = new AlarmContinuityStore(configuration, "Machine01");
                current.Open();
                Ensure(current.BlockReason == null && File.Exists(pending) && !File.Exists(clean),
                    "可信的模拟初始记录没有在动作前变成未结束记录。 ");
                var restarted = new AlarmContinuityStore(configuration, "Machine01");
                restarted.Open();
                Ensure(restarted.BlockReason != null && File.Exists(pending), "异常重启丢失了旧会话门禁。 ");

                current.CloseIfSettled(true);
                Ensure(File.Exists(clean) && !File.Exists(pending), "可信且完整的会话没有安全收尾。 ");
                var next = new AlarmContinuityStore(configuration, "Machine01");
                next.Open();
                var alarm = new MachineAlarmDefinition("TEST_OLD", "原停机条件", MachineAlarmKind.StopMachine);
                var oldAlarms = new MachineAlarmService();
                oldAlarms.ReportCondition(alarm, "工位 A", true, "旧输入");
                oldAlarms.ReportCondition(alarm, "工位 A", false, "旧输入恢复但未复位");
                Ensure(oldAlarms.HasLatchedStopAlarm, "测试前提不成立：恢复条件后旧报警应仍锁存。 ");
                next.MarkUnconfirmed("旧会话报警未完成复位");
                var flow = new WaitingStationFlow("工位 A", "等待测试");
                var axis = new FakeAxis("X");
                var runtime = CreateRuntime(flow, new MachineAlarmService(), axis: axis);
                runtime.SetAlarmContinuityBlockReason(next.BlockReason);
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.ResetAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(axis.AlarmResetCount == 0, "旧报警未确认时已下发轴复位。 ");
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
                bool stopped = false;
                await runtime.RunManualStopAsync(() => { stopped = true; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
                Ensure(stopped, "报警交接门禁阻止了受控停止。 ");
                await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                next.CloseIfSettled(true);
                Ensure(File.Exists(pending) && !File.Exists(clean), "未确认的旧报警被关闭操作清除。 ");
                var afterRestart = new AlarmContinuityStore(configuration, "Machine01");
                afterRestart.Open();
                Ensure(afterRestart.BlockReason != null, "重启后旧报警未知状态被清除。 ");

                var lateAxis = new FakeAxis("Late") { PendingStatus = new TaskCompletionSource<MotionControl.Framework.Contracts.Motion.AxisStatus>() };
                var lateRuntime = CreateRuntime(new WaitingStationFlow("晚到测试", "等待"), new MachineAlarmService(), axis: lateAxis);
                await lateRuntime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                Task reset = lateRuntime.ResetAsync(CancellationToken.None);
                lateRuntime.MarkAlarmContinuityUnconfirmed("旧来源在复位过程中报告停机报警");
                var ready = new MotionControl.Framework.Contracts.Motion.AxisStatus(0, 0, true, false, true, false, false, false, false, false, null, false);
                lateAxis.PendingStatus.SetResult(ready);
                await ExpectExceptionAsync<InvalidOperationException>(() => reset).ConfigureAwait(false);
                Ensure(lateAxis.AlarmResetCount == 0, "复位读取期间旧报警到达，仍下发了轴复位。 ");
                lateAxis.PendingStatus = null;
                await lateRuntime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
