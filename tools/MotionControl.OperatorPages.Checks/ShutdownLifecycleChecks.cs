using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Host.Wpf;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>复用现有无轴设施检查退出流程，不声称控制器运动或屏幕验证。</summary>
    internal static partial class BoundTaskLoadChecks
    {
        /// <summary>关闭已失败并撤销操作入口后，专用退出必须重试原关闭，不重新发起 Abort。</summary>
        internal static async Task RunShutdownRetryCheckAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "ShutdownRetry-" + Guid.NewGuid().ToString("N"));
            const string id = "ShutdownRetry";
            var store = new MachineConfigurationStore(root);
            var host = new MainWindow();
            var card = new LoadCard();
            var cards = new MotionCardGroup(new[] { card });
            var runtime = new MachineRuntime(cards, new IStationFlow[0], new MachineAlarmService(), new SafeCheckSafety());
            var variables = new DeviceVariableStore(new DeviceVariableDefinition[0]);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = typeof(MainWindow).Assembly.GetType("MotionControl.Host.Wpf.Tasks.TaskMonitorLauncher", true);
            object page = Activator.CreateInstance(type, flags, null, new object[] { store, id, host, null }, null);
            // 调用真实入口；反射仅限内部检查，不进入生产代码。
            Task Invoke(string name, params object[] arguments) => (Task)type.GetMethod(name, flags).Invoke(page, arguments);
            var errors = new List<Exception>();
            FieldInfo closeField = null;
            object machine = null;
            try
            {
                WriteMachine(store, id);
                await runtime.ConnectAsync(CancellationToken.None);
                await Invoke("SetMachineContextAsync", runtime, cards, new NoIo(), variables, new CheckPreparation[0]);
                await Invoke("LoadTaskSessionAsync");
                object session = type.GetField("_session", flags).GetValue(page);
                machine = session.GetType().GetProperty("Machine").GetValue(session);
                closeField = machine.GetType().GetField("_closeTask", flags);
                var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                closeField.SetValue(machine, failure.Task);
                Task closing = Invoke("CloseTaskSessionAsync", false);
                failure.SetException(new IOException("EXPECTED_SHUTDOWN_CLOSE_FAILURE"));
                await Reject(closing, "EXPECTED_SHUTDOWN_CLOSE_FAILURE");
                Require(ReferenceEquals(type.GetField("_session", flags).GetValue(page), session)
                    && runtime.IsTaskEngineControlClaimed, "关闭失败丢弃了原会话或实际持权。");
                // 恢复真实控制器关闭实现，不伪造归还控制权的成功。
                closeField.SetValue(machine, null);
                try { await Invoke("StopTaskSessionForShutdownAsync"); }
                catch (Exception error)
                {
                    throw new InvalidOperationException("SHUTDOWN_RETRY_REENTERED_INVALID_OPERATION: 退出没有继续原关闭交接。", error);
                }
                Require(type.GetField("_session", flags).GetValue(page) == null
                    && !runtime.IsTaskEngineControlClaimed, "退出重试后仍有任务会话或设备控制权。");
                var unsettled = type.GetProperty("HasUnsettledTaskSession", flags);
                Require(unsettled != null && !(bool)unsettled.GetValue(page), "安全收尾后仍报告未完成的任务会话。");
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                if (closeField != null) closeField.SetValue(machine, null);
                try { await Invoke("CloseTaskSessionAsync", false); }
                catch (Exception error) { errors.Add(error); }
                try { await runtime.DisconnectAsync(CancellationToken.None); }
                catch (Exception error) { errors.Add(error); }
                host.Close();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count != 0) throw new AggregateException("退出重试检查或收尾失败。", errors);
            Console.WriteLine("[通过] 退出重试继续原关闭，并真实归还控制权");
        }

        /// <summary>挂起原 Runtime 的退出准入，换上下文后只允许撤回原 Runtime 的退出意图。</summary>
        internal static async Task RunHostShutdownChecksAsync()
        {
            Type type = typeof(App).Assembly.GetType("MotionControl.Host.Wpf.HostShutdownOperation", false);
            Require(type != null, "SHUTDOWN_OPERATION_MISSING: 设备退出协调仍未独立。");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var original = new MachineRuntime(new MotionCardGroup(new[] { new LoadCard() }),
                new IStationFlow[0], new MachineAlarmService(), new SafeCheckSafety());
            var replacement = new MachineRuntime(new MotionCardGroup(new[] { new LoadCard() }),
                new IStationFlow[0], new MachineAlarmService(), new SafeCheckSafety());
            var commandLock = (SemaphoreSlim)typeof(MachineRuntime).GetField("_commandLock", flags).GetValue(original);
            bool lockHeld = false;
            bool contextCurrent = true;
            Task stopping = null;
            var errors = new List<Exception>();
            // 只检查 App 对象身份；具体连接与轴反馈仍由 Runtime 负责。
            Action checkContext = () =>
            {
                if (!contextCurrent) throw new InvalidOperationException("SHUTDOWN_CONTEXT_REPLACED");
            };
            object CreateOperation() => Activator.CreateInstance(type, flags, null,
                new object[] { original, null, null, null, false, checkContext }, null);
            Task Invoke(object operation, string method) => (Task)type.GetMethod(method, flags).Invoke(operation, null);
            Task<bool> Cancel(object operation) => (Task<bool>)type.GetMethod("TryCancelAsync", flags).Invoke(operation, null);
            try
            {
                await original.ConnectAsync(CancellationToken.None);
                await replacement.ConnectAsync(CancellationToken.None);
                await commandLock.WaitAsync();
                lockHeld = true;
                object pending = CreateOperation();
                stopping = Invoke(pending, "StopAsync");
                Require(!stopping.IsCompleted, "退出检查未进入预期等待窗口。");
                await replacement.BeginShutdownAsync(CancellationToken.None);
                contextCurrent = false;
                commandLock.Release();
                lockHeld = false;
                await Reject(stopping, "SHUTDOWN_CONTEXT_REPLACED");
                Require(original.IsShutdownInProgress && replacement.IsShutdownInProgress,
                    "旧退出失败前未保留两个 Runtime 各自的保护。");
                Require(await Cancel(pending), "原 Runtime 已空闲却无法撤回退出意图。");
                Require(!original.IsShutdownInProgress && replacement.IsShutdownInProgress,
                    "旧退出操作解除或改写了新 Runtime 的退出保护。");

                // 未经过停止不能进入输出确认；无连接服务也不能保存为退出成功。
                contextCurrent = true;
                object normal = CreateOperation();
                await Reject(Invoke(normal, "VerifyAndSaveAsync"), "停止收尾尚未完成");
                await Invoke(normal, "StopAsync");
                await Reject(Invoke(normal, "VerifyAndSaveAsync"), "控制卡连接状态未知");
                Require(await Cancel(normal), "无动作的退出失败未能归还原门禁。");
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                if (lockHeld) commandLock.Release();
                // 撤销是上述场景预期；仍等原等待退出后再断开两个独立 Runtime。
                if (stopping != null) { try { await stopping; } catch { } }
                foreach (var runtime in new[] { original, replacement })
                {
                    try
                    {
                        if (!await runtime.TryCancelShutdownAsync())
                            throw new InvalidOperationException("测试 Runtime 退出门禁没有解除。");
                        await runtime.DisconnectAsync(CancellationToken.None);
                    }
                    catch (Exception error) { errors.Add(error); }
                }
            }
            if (errors.Count != 0) throw new AggregateException("退出上下文检查或收尾失败。", errors);
            Console.WriteLine("[通过] 退出固定原Runtime、迟到拒绝、只撤回原门禁及禁止未核验保存");
        }
    }
}
