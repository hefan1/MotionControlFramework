using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.TaskEngine.MachineDevice;

namespace MotionControl.MachineDevice.Checks
{
    internal static partial class Program
    {
        /// <summary>验证正式任务设备在接管、注册、预读、下发与关闭期间的会话边界，全部使用内存卡。</summary>
        private static async Task CheckBoundTaskDeviceAsync()
        {
            var axis = new FakeAxis("X");
            var io = new FakeLogicalIo();
            var legacy = CreateDevice(axis, io, out var variables, out var runtime);
            var token = CancellationToken.None;
            legacy.WriteOutput("Vacuum", false);
            int initialWrites = io.OutputWrites;
            await runtime.ConnectAsync(token);
            Guid sessionA = runtime.ConnectionSessionId.Value;
            // 正式构造只从已捕获的连接生成实例。
            MachineTaskDevice Bound(Guid session) => new MachineTaskDevice(legacy.Catalog, runtime.MotionCards, io, variables, runtime, session);
            var stale = Bound(sessionA);
            await runtime.DisconnectAsync(token);
            await runtime.ConnectAsync(token);
            Guid sessionB = runtime.ConnectionSessionId.Value;
            ExpectMessage(() => Bound(sessionA), "连接会话");
            ExpectMessage(() => stale.ClaimMachine(new object()), "连接会话");
            ExpectMessage(() => stale.RegisterRun("old", null), "连接会话");
            ExpectMessage(() => stale.WriteOutput("Vacuum", true), "控制权");
            ExpectMessage(() => stale.BeginMove("X", 1, false, false, Profile()), "控制权");
            Ensure(!runtime.IsTaskEngineControlClaimed && io.OutputWrites == initialWrites && axis.MoveCalls == 0,
                "旧连接任务获得设备或影响新连接。");

            var first = Bound(sessionB);
            var second = Bound(sessionB);
            object owner = new object();
            object nextController = new object();
            legacy.ClaimMachine(owner);
            legacy.ReleaseMachine(owner);
            legacy.ClaimMachine(nextController);
            legacy.ReleaseMachine(nextController);
            // 公开兼容 API 先占用同一对象；正式接管必须将 owner 升级为受控归还，后续兼容调用不能降级。
            await runtime.AcquireTaskEngineControlAsync(first, token);
            first.ClaimMachine(owner);
            await runtime.AcquireTaskEngineControlAsync(first, token);
            ExpectMessage(() => runtime.ReleaseTaskEngineControl(first), "正式任务设备");
            first.ReleaseMachine(owner);
            ExpectMessage(() => first.ClaimMachine(nextController), "已绑定其他控制器");
            ExpectMessage(() => first.ClaimMachine(null), "控制器身份");
            Ensure(!runtime.IsTaskEngineControlClaimed, "拒绝其他控制器时仍取得 Runtime 命令权。");
            var allowClaim = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task activeCommand = runtime.RunConnectedConfigurationChangeAsync(() => allowClaim.Task, token);
            Task claiming = Task.Run(() => first.ClaimMachine(owner));
            try
            {
                var transitioning = typeof(MachineTaskDevice).GetField("_changingOwner",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Ensure(SpinWait.SpinUntil(() => (bool)transitioning.GetValue(first), 3000), "未进入锁外申请任务控制权的窗口。");
                ExpectMessage(() => first.ClaimMachine(new object()), "切换控制权");
                ExpectMessage(() => first.ReleaseMachine(owner), "切换控制权");
                ExpectMessage(() => first.WriteOutput("Vacuum", false), "控制权");
                Ensure(!runtime.IsTaskEngineControlClaimed && io.OutputWrites == initialWrites, "切换中的设备错误地下发了命令。");
            }
            finally { allowClaim.TrySetResult(true); }
            await Task.WhenAll(activeCommand, claiming);
            first.RegisterRun("current", owner);
            ExpectMessage(() => first.ReleaseMachine(owner), "运行未收尾");
            first.UnregisterRun("current");
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task busy = runtime.RunConnectedConfigurationChangeAsync(() => release.Task, token);
            try { ExpectMessage(() => first.ReleaseMachine(owner), "设备命令"); }
            finally { release.TrySetResult(true); }
            await busy;
            first.WriteOutput("Vacuum", true);
            Ensure(io.OutputWrites == initialWrites + 1, "归还失败后丢失实际 owner。");
            // 首次请求被当前 owner 拒绝，不应将另一实例永久绑定给这次失败请求的调用者。
            ExpectMessage(() => second.ClaimMachine(new object()), "其他会话");

            // 卡已经进入写方法但尚未返回原始 Task，pending 集合仍为空。
            using (var issuing = new ManualResetEventSlim())
            using (var releaseIssue = new ManualResetEventSlim())
            {
                io.BeforeReturnOutputTask = () =>
                {
                    issuing.Set();
                    if (!releaseIssue.Wait(4000)) throw new TimeoutException("同步驱动下发窗口未释放。");
                };
                Task issuingWrite = Task.Run(() => first.WriteOutput("Vacuum", false));
                try
                {
                    Ensure(issuing.Wait(3000), "未进入同步 DO 下发窗口。");
                    Ensure(first.GetResourceRecoveryState().ToString() == "Held", "同步 DO 下发时资源被报告为空闲。");
                    var resource = first.Resources.Enqueue("recover-guard", "Idle", "工位", "node", "等待", "call", new[] { "Axis:X" });
                    Ensure(first.Resources.TryAcquire(resource, out var reason), reason);
                    first.Resources.Block(resource, "测试隔离");
                    Ensure(!first.TryRecoverResources(first.ReadResources()[0].RequestId, "现场已确认", out reason) && reason.Contains("输出"),
                        "驱动仍在同步下发时允许解除隔离。");
                    ExpectMessage(() => first.ReleaseMachine(owner), "资源");
                }
                finally { releaseIssue.Set(); }
                await issuingWrite;
                io.BeforeReturnOutputTask = null;
                var blocked = first.Resources.FindBlocked(first.ReadResources()[0].RequestId);
                Ensure(first.Resources.Recover(blocked), "测试资源在驱动完成后无法回收。");
            }
            int issuedBeforePending = io.OutputWrites;
            io.PendingOutputWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task writing = Task.Run(() => first.WriteOutput("Vacuum", false));
            try
            {
                Ensure(SpinWait.SpinUntil(() => Volatile.Read(ref io.OutputWrites) == issuedBeforePending + 1, 3000), "未进入测试 DO 驱动。");
                ExpectMessage(() => first.ReleaseMachine(owner), "输出");
                ExpectMessage(() => second.ClaimMachine(new object()), "其他会话");
            }
            finally { io.PendingOutputWrite.TrySetResult(true); }
            await writing;
            io.PendingOutputWrite = null;

            var oldMove = first.BeginMove("X", 1, false, false, Profile());
            ExpectMessage(() => first.ReleaseMachine(owner), "轴动作");
            await first.EndMoveAsync(oldMove, false, token);
            first.ReleaseMachine(owner);
            second.ClaimMachine(nextController);
            int readsBefore = axis.StatusReadCount, writesBefore = io.OutputWrites;
            ExpectMessage(() => first.RegisterRun("previous", owner), "控制权");
            ExpectMessage(() => first.WriteOutput("Vacuum", true), "控制权");
            ExpectMessage(() => first.BeginMove("X", 2, false, false, Profile()), "控制权");
            Ensure(axis.StatusReadCount == readsBefore && io.OutputWrites == writesBefore, "同连接的旧实例触碰了新 owner 的驱动。");
            second.WriteOutput("Vacuum", true);
            await runtime.BeginShutdownAsync(token);
            try
            {
                ExpectMessage(() => second.RegisterRun("after-shutdown", nextController), "安全退出");
                ExpectMessage(() => second.WriteOutput("Vacuum", false), "安全退出");
                ExpectMessage(() => second.BeginMove("X", 3, false, false, Profile()), "安全退出");
                await first.EndMoveAsync(oldMove, false, token);
            }
            finally { second.ReleaseMachine(nextController); }
            await runtime.DisconnectAsync(token);
        }
    }
}
