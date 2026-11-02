using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>针对异步确认和写出故障的暂停竞态检查。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>模拟后台日志已失败或取消但完成回调尚未运行，复位不能跨过该结算窗口。</summary>
        private static async Task JournalFailureSettlementBlocksReset()
        {
            foreach (bool cancelled in new[] { false, true })
            {
                var device = Device();
                var machine = Machine(device, new[] { TaskDefinition("Wait", TaskFixtures.Delay("wait")) },
                    Station("wait", "Wait", StationTaskMode.Once));
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var coordinator = device.Resources;
                var request = (TaskResourceRequest)typeof(TaskResourceCoordinator).GetMethod("Enqueue", flags).Invoke(coordinator,
                    new object[] { "run", "task", "任务", "node", "步骤", "execution", new[] { "Output:Vacuum" }, null, null });
                Task write = cancelled
                    ? Task.FromCanceled(new CancellationToken(true))
                    : Task.FromException(new InvalidOperationException("测试日志失败"));
                typeof(TaskResourceRequest).GetProperty("JournalWrite", flags).SetValue(request, write);

                CheckAssert.True(device.ReadResources().Single().State == TaskResourceState.Waiting,
                    "测试必须停在后台写入已结束、隔离回调未执行的窗口。");
                CheckAssert.Throws(machine.Reset, cancelled ? "已取消但尚未隔离的日志允许复位。" : "已失败但尚未隔离的日志允许复位。");

                var settle = (Task)typeof(TaskResourceCoordinator).GetMethod("SettleJournalWritesAsync", flags)
                    .Invoke(coordinator, new object[0]);
                await Wait(settle);
                CheckAssert.True(device.ReadResources().Single().State == TaskResourceState.Blocked,
                    "日志结算后必须保持资源待确认。");
                CheckAssert.Throws(machine.Reset, "日志隔离后允许直接复位。");
            }
        }

        /// <summary>确认尚在途时不能完成暂停；写出失败和报警重入时先隔离再宣布停机。</summary>
        private static async Task PauseWaitsForDeviceOperations()
        {
            var inner = Device();
            var delayed = new DelayedDevice(inner);
            var stop = new AxisCommandNode { Id = "confirm", Name = "确认停轴", AxisName = "X", Command = TaskAxisCommand.Stop, StopMode = AxisStopMode.Controlled };
            var configuration = new TaskMachineConfiguration { MachineId = "Test01", Name = "在途检查",
                Stations = new List<StationTaskConfiguration> { Station("confirm", "Confirm", StationTaskMode.Once) } };
            var machine = new TaskMachineController(configuration, new[] { TaskDefinition("Confirm", stop) }, delayed);
            Machines.Add(machine);
            machine.Reset(); machine.Start();
            await Wait(delayed.ConfirmationStarted.Task);
            Task pause = machine.PauseAsync();
            CheckAssert.True(!pause.IsCompleted && machine.GetSnapshot().State == TaskMachineState.Pausing,
                "轴确认尚在途时，暂停不能完成。");
            CheckAssert.Throws(machine.Reset, "在途确认未完成时允许复位。");
            delayed.FinishConfirmation.TrySetResult(true);
            await Wait(pause);
            machine.Reset(); machine.Start();
            await Until(() => State(machine, "confirm").CompletedCycles == 1, "确认完成后未从原步骤继续。");

            var stuck = new DelayedDevice(Device());
            var stuckConfig = new TaskMachineConfiguration { MachineId = "Test01", Name = "确认超时检查",
                Stations = new List<StationTaskConfiguration> { Station("confirm", "Confirm", StationTaskMode.Once) } };
            var stuckMachine = new TaskMachineController(stuckConfig, new[] { TaskDefinition("Confirm", stop) }, stuck);
            Machines.Add(stuckMachine);
            stuckMachine.Reset(); stuckMachine.Start();
            await Wait(stuck.ConfirmationStarted.Task);
            try
            {
                Task stuckPause = stuckMachine.PauseAsync();
                CheckAssert.True(await Task.WhenAny(stuckPause, Task.Delay(16000)) == stuckPause,
                    "设备不返回时暂停命令无限等待，界面无法退出等待状态。");
                bool reported = false;
                try { await stuckPause; }
                catch (InvalidOperationException) { reported = true; }
                CheckAssert.True(reported && stuckMachine.GetSnapshot().State == TaskMachineState.Faulted,
                    "设备不返回时必须报故障，不能把超时当作停稳。");
                CheckAssert.Throws(stuckMachine.Reset, "设备确认仍未返回时允许复位。");
            }
            finally { stuck.FinishConfirmation.TrySetResult(true); }

            var lateMove = new DelayedDevice(Device()) { DelayBeginMove = true };
            var lateDefinition = TaskFixtures.Move("late"); lateDefinition.Target.Constant = "1"; lateDefinition.Profile.Speed = 100;
            var lateConfig = new TaskMachineConfiguration { MachineId = "Test01", Name = "迟到轴动作检查",
                Stations = new List<StationTaskConfiguration> { Station("late", "Late", StationTaskMode.Once) } };
            var lateMachine = new TaskMachineController(lateConfig, new[] { TaskDefinition("Late", lateDefinition) }, lateMove);
            Machines.Add(lateMachine);
            lateMachine.Reset(); lateMachine.Start();
            await Wait(lateMove.BeginMoveStarted.Task);
            Task latePause = lateMachine.PauseAsync();
            CheckAssert.True(!latePause.IsCompleted, "轴命令尚在途时暂停不能完成。");
            lateMove.FinishBeginMove.TrySetResult(true);
            await Wait(latePause);
            CheckAssert.True(!lateMove.ReadAxis("X").IsBusy && lateMove.ReadAxis("X").AcceptedCommands == 1,
                "暂停期间迟到登记的轴动作没有受控停止。");
            lateMachine.Reset(); lateMachine.Start();
            await Until(() => State(lateMachine, "late").CompletedCycles == 1, "迟到轴动作停稳后未能继续。");

            var failing = new DelayedDevice(Device()) { FailConfirmation = true };
            var failConfig = new TaskMachineConfiguration { MachineId = "Test01", Name = "确认失败检查",
                Stations = new List<StationTaskConfiguration> { Station("confirm", "Confirm", StationTaskMode.Once) } };
            var failMachine = new TaskMachineController(failConfig, new[] { TaskDefinition("Confirm", stop) }, failing);
            Machines.Add(failMachine);
            failMachine.Reset(); failMachine.Start();
            await Wait(failing.ConfirmationStarted.Task);
            Task failedPause = failMachine.PauseAsync();
            CheckAssert.True(!failedPause.IsCompleted, "轴确认尚未返回，暂停不能完成。");
            failing.FinishConfirmation.TrySetResult(true);
            await Wait(failedPause);
            CheckAssert.True(failMachine.GetSnapshot().State == TaskMachineState.Faulted,
                "在途确认发生异常时，暂停完成前必须锁存故障。");

            var moving = new DelayedDevice(Device()) { DelayMoveFinish = true };
            var move = TaskFixtures.Move("move"); move.Target.Constant = "1"; move.Profile.Speed = 100;
            var moveConfig = new TaskMachineConfiguration { MachineId = "Test01", Name = "轴收尾失败检查",
                Stations = new List<StationTaskConfiguration> { Station("move", "Move", StationTaskMode.Once) } };
            var moveMachine = new TaskMachineController(moveConfig, new[] { TaskDefinition("Move", move) }, moving);
            Machines.Add(moveMachine);
            moveMachine.Reset(); moveMachine.Start();
            await Wait(moving.MoveFinishStarted.Task);
            Task movePause = moveMachine.PauseAsync();
            CheckAssert.True(!movePause.IsCompleted, "轴正常收尾尚未完成，暂停不能完成。");
            moving.FinishMove.TrySetResult(true);
            try { await Wait(movePause); }
            catch (InvalidOperationException) { /* 收尾失败必须让暂停报告失败。 */ }
            CheckAssert.True(moveMachine.GetSnapshot().State == TaskMachineState.Faulted
                && moving.ReadResources().Any(item => item.State == TaskResourceState.Blocked),
                "轴收尾失败时，暂停完成前必须锁存故障并隔离轴资源。");
            CheckAssert.Throws(moveMachine.Reset, "轴收尾状态未知时允许复位。");

            var innerOutput = Device();
            var output = new DelayedDevice(innerOutput);
            var write = new SetOutputNode { Id = "write", Name = "写出测试", OutputName = "Vacuum", Value = true };
            var outputConfig = new TaskMachineConfiguration { MachineId = "Test01", Name = "写出竞态检查",
                Stations = new List<StationTaskConfiguration> { Station("write", "Write", StationTaskMode.Once) } };
            var outputMachine = new TaskMachineController(outputConfig, new[] { TaskDefinition("Write", write) }, output);
            Machines.Add(outputMachine);
            var enteredWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowAlarm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            output.BeforeWrite = () => { enteredWrite.TrySetResult(true); allowAlarm.Task.GetAwaiter().GetResult(); };
            output.OnWrite = () => output.AlarmTask = outputMachine.SetStopAlarmAsync("RACE_WRITE", "写出期间收到停机报警", true);
            outputMachine.Reset(); outputMachine.Start();
            await Wait(enteredWrite.Task);
            var pauseReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() =>
            {
                try { pauseReturned.TrySetResult(outputMachine.PauseAsync()); }
                catch (Exception error) { pauseReturned.TrySetException(error); }
            });
            Task outputPause;
            try { await Wait(pauseReturned.Task); outputPause = pauseReturned.Task.Result; }
            finally { allowAlarm.TrySetResult(true); }
            await Wait(output.WriteStarted.Task);
            CheckAssert.True(!outputPause.IsCompleted, "输出写入在途时，暂停不应完成。");
            CheckAssert.True(output.AlarmTask != null && !output.AlarmTask.IsCompleted,
                "输出仍在写入时，停机报警不能报告收尾完成。");
            output.FinishWrite.TrySetResult(true);
            await Wait(Task.WhenAll(outputPause, output.AlarmTask));
            CheckAssert.True(outputMachine.GetSnapshot().State == TaskMachineState.Faulted,
                "写出报警没有保持整机故障状态。");
            CheckAssert.True(output.ReadResources().Any(item => item.State == TaskResourceState.Blocked),
                "输出状态未知时，报警返回前必须先隔离输出资源。");
            await outputMachine.SetStopAlarmAsync("RACE_WRITE", "", false);
            CheckAssert.Throws(outputMachine.Reset, "输出待确认时允许复位。");
        }

        /// <summary>在内存设备外包装可控的异步确认和写出中断，不访问实物。</summary>
        private sealed class DelayedDevice : ITaskDevice
        {
            /// <summary>承担其余设备语义的内存设备。</summary>
            private readonly InMemoryTaskDevice _inner;
            /// <summary>确认调用已进入。</summary>
            internal readonly TaskCompletionSource<bool> ConfirmationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>允许确认返回。</summary>
            internal readonly TaskCompletionSource<bool> FinishConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>写出调用已进入。</summary>
            internal readonly TaskCompletionSource<bool> WriteStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>允许写出抛错。</summary>
            internal readonly TaskCompletionSource<bool> FinishWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>写出期间同步触发的报警。</summary>
            internal Action OnWrite;
            /// <summary>写出后端内部触发报警之前可控暂停，用于复现双线程锁反转。</summary>
            internal Action BeforeWrite;
            /// <summary>写出期间发起的报警操作。</summary>
            internal Task AlarmTask;
            /// <summary>确认返回时是否注入异常。</summary>
            internal bool FailConfirmation;
            /// <summary>是否延迟并失败正常的轴收尾。</summary>
            internal bool DelayMoveFinish;
            /// <summary>是否阻塞轴动作接受，模拟停轴回调迟到登记。</summary>
            internal bool DelayBeginMove;
            /// <summary>轴动作开始接受。</summary>
            internal readonly TaskCompletionSource<bool> BeginMoveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>允许轴动作接受结束。</summary>
            internal readonly TaskCompletionSource<bool> FinishBeginMove = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>轴收尾已进入。</summary>
            internal readonly TaskCompletionSource<bool> MoveFinishStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>允许轴收尾报错。</summary>
            internal readonly TaskCompletionSource<bool> FinishMove = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>绑定内存设备。</summary>
            internal DelayedDevice(InMemoryTaskDevice inner) { _inner = inner; }
            /// <summary>返回设备目录。</summary>
            public TaskResourceCatalog Catalog => _inner.Catalog;
            /// <summary>返回同一资源协调器。</summary>
            public TaskResourceCoordinator Resources => _inner.Resources;
            /// <summary>读取输入。</summary>
            public bool? ReadInput(string name) => _inner.ReadInput(name);
            /// <summary>读取输出。</summary>
            public bool? ReadOutput(string name) => _inner.ReadOutput(name);
            /// <summary>模拟写出期间报警，随后抛出不能确认实际输出的错误。</summary>
            public void WriteOutput(string name, bool value)
            {
                BeforeWrite?.Invoke();
                OnWrite?.Invoke();
                WriteStarted.TrySetResult(true);
                FinishWrite.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("测试写出结果未知。");
            }
            /// <summary>写变量。</summary>
            public void WriteVariable(string name, object value) => _inner.WriteVariable(name, value);
            /// <summary>读取变量快照。</summary>
            public IReadOnlyList<DeviceVariableSnapshot> ReadVariables() => _inner.ReadVariables();
            /// <summary>读取轴快照。</summary>
            public TaskAxisState ReadAxis(string name) => _inner.ReadAxis(name);
            /// <summary>检查互锁。</summary>
            public bool CheckInterlock(TaskInterlock interlock, TaskCondition condition, out string reason) => _inner.CheckInterlock(interlock, condition, out reason);
            /// <summary>读取资源快照。</summary>
            public IReadOnlyList<TaskResourceSnapshot> ReadResources() => _inner.ReadResources();
            /// <summary>按条件恢复资源。</summary>
            public bool TryRecoverResources(string id, string confirmation, out string reason) => _inner.TryRecoverResources(id, confirmation, out reason);
            /// <summary>接受轴运动。</summary>
            public ITaskAxisMove BeginMove(string name, double target, bool relative, bool home, AxisMotionProfile profile)
            {
                if (DelayBeginMove)
                {
                    BeginMoveStarted.TrySetResult(true);
                    FinishBeginMove.Task.GetAwaiter().GetResult();
                    DelayBeginMove = false;
                }
                return _inner.BeginMove(name, target, relative, home, profile);
            }
            /// <summary>更新轴运动。</summary>
            public void AdvanceMove(ITaskAxisMove move, double progress) => _inner.AdvanceMove(move, progress);
            /// <summary>判断轴动作是否完成。</summary>
            public bool IsMoveComplete(ITaskAxisMove move, double progress) => _inner.IsMoveComplete(move, progress);
            /// <summary>结束轴动作。</summary>
            public async Task EndMoveAsync(ITaskAxisMove move, bool completed, CancellationToken token)
            {
                if (completed && DelayMoveFinish)
                {
                    MoveFinishStarted.TrySetResult(true);
                    await FinishMove.Task.ConfigureAwait(false);
                    throw new InvalidOperationException("测试轴收尾失败。");
                }
                await _inner.EndMoveAsync(move, completed, token).ConfigureAwait(false);
            }
            /// <summary>接受组轴运动。</summary>
            public IReadOnlyList<ITaskAxisGroupMove> BeginGroupMove(IReadOnlyList<TaskAxisGroupTarget> targets) => _inner.BeginGroupMove(targets);
            /// <summary>更新组轴动作。</summary>
            public bool AdvanceGroupMove(IReadOnlyList<ITaskAxisGroupMove> moves, double elapsed, out string detail) => _inner.AdvanceGroupMove(moves, elapsed, out detail);
            /// <summary>结束组轴动作。</summary>
            public Task EndGroupMoveAsync(IReadOnlyList<ITaskAxisGroupMove> moves, CancellationToken token) => _inner.EndGroupMoveAsync(moves, token);
            /// <summary>在测试许可后才确认轴已停止。</summary>
            public async Task ConfirmStoppedAsync(string name, CancellationToken token)
            {
                ConfirmationStarted.TrySetResult(true);
                await FinishConfirmation.Task.ConfigureAwait(false);
                if (FailConfirmation) throw new InvalidOperationException("测试确认失败。");
                await _inner.ConfirmStoppedAsync(name, token).ConfigureAwait(false);
            }
            /// <summary>读取握手状态。</summary>
            public void ReadHandshake(string request, string completed, out bool? requested, out bool? done) => _inner.ReadHandshake(request, completed, out requested, out done);
            /// <summary>发布握手完成。</summary>
            public bool TryPublishCompletion(string request, string completed) => _inner.TryPublishCompletion(request, completed);
            /// <summary>复归握手完成。</summary>
            public bool TryClearCompletion(string request, string completed) => _inner.TryClearCompletion(request, completed);
            /// <summary>登记整机所有权。</summary>
            public void ClaimMachine(object owner) => ((ITaskDevice)_inner).ClaimMachine(owner);
            /// <summary>归还整机所有权。</summary>
            public void ReleaseMachine(object owner) => ((ITaskDevice)_inner).ReleaseMachine(owner);
            /// <summary>登记运行。</summary>
            public void RegisterRun(string id, object owner) => ((ITaskDevice)_inner).RegisterRun(id, owner);
            /// <summary>结束运行登记。</summary>
            public void UnregisterRun(string id) => ((ITaskDevice)_inner).UnregisterRun(id);
        }
    }
}
