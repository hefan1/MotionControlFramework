using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.MachineDevice.Checks
{
    internal static partial class Program
    {
        /// <summary>在模拟卡上跨重连排队清料，并制造断卡失败；拒绝时不得写记录或启动轴。</summary>
        private static async Task CheckClearanceConnectionAsync()
        {
            var axis = new FakeAxis("X");
            var token = CancellationToken.None;
            CreateDevice(axis, new FakeLogicalIo(), out _, out MachineRuntime runtime);
            var card = (FakeMotionCard)System.Linq.Enumerable.Single(runtime.MotionCards.Cards);
            int markers = 0;
            runtime.ConfigureControlledClearanceProfiles(new[] { TestClearanceProfile() });
            runtime.SetClearanceRecordCallbacks(() => markers++, () => { }, value => { });
            await runtime.ConnectAsync(token);
            var initial = runtime.ConnectionSessionId;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task holding = runtime.RunConnectedConfigurationChangeAsync(() => release.Task, token);
            Task disconnect = runtime.DisconnectAsync(token);
            Task reconnect = runtime.ConnectAsync(token);
            Task oldClearance = runtime.StartControlledClearanceAsync("test", token);
            release.SetResult(true);
            await Task.WhenAll(holding, disconnect, reconnect);
            Ensure(runtime.ConnectionSessionId.HasValue && initial != runtime.ConnectionSessionId, "未建立新的连接。");
            ExpectMessage(() => oldClearance.GetAwaiter().GetResult(), "连接会话");
            card.DisconnectError = new InvalidOperationException("测试断卡失败");
            ExpectMessage(() => runtime.DisconnectAsync(token).GetAwaiter().GetResult(), "断开失败");
            ExpectMessage(() => runtime.StartControlledClearanceAsync("test", token).GetAwaiter().GetResult(), "连接会话");
            Ensure(runtime.ConnectionSessionId == null && markers == 0 && axis.MoveCalls == 0,
                "旧会话清料写入了持久标记或下发轴动作。");
            card.DisconnectError = null;
            await runtime.DisconnectAsync(token);
            await runtime.ConnectAsync(token);
            // 清料已立标记、正在发布准备状态，但尚未发给执行器任何动作。
            runtime.StatusChanged += (sender, args) =>
            {
                if (runtime.State == MachineRunState.Preparing) card.Offline = true;
            };
            await runtime.StartControlledClearanceAsync("test", token);
            Task completion = runtime.ClearanceCompletion;
            Ensure(await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(4))) == completion,
                "通知中掉线后清料任务悬挂。");
            ExpectMessage(() => completion.GetAwaiter().GetResult(), "连接会话");
            Ensure(markers == 1 && axis.MoveCalls == 0 && runtime.IsClearing && runtime.State == MachineRunState.Alarmed
                && !string.IsNullOrWhiteSpace(runtime.RecoveryBlockReason), "清料执行前掉线仍有动作或丢失隔离。");
        }
    }
}
