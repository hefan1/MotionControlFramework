using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.MachineDevice;

namespace MotionControl.MachineDevice.Checks
{
    /// <summary>检查真机任务设备的行为边界；不连接控制器，用假轴和假 IO 验证语义。</summary>
    internal static class Program
    {
        /// <summary>逐项运行检查并统计失败组数。</summary>
        private static int Main()
        {
            int failures = 0;
            Run("移动到位由轴反馈决定", MotionCompletesFromFeedback, ref failures);
            Run("取消时让轴受控停止并确认停稳", CancelStopsAxis, ref failures);
            Run("未使能、未回零和越行程拒绝动作", MotionPreconditionsAreRejected, ref failures);
            Run("轴组插补明确拒绝", GroupMoveIsRejected, ref failures);
            Run("IO 读取失败向上抛出", IoFailureIsReported, ref failures);
            Run("变量按目录类型写入", VariablesAreWrittenByType, ref failures);
            Run("接管被已配置工位流程拒绝", ClaimIsRejectedWhenStationFlowsExist, ref failures);

            Console.WriteLine("真机任务设备检查：共 7 组，失败 " + failures + "。未连接控制器。");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>运行一项检查，捕获异常并计入失败。</summary>
        private static void Run(string name, Action check, ref int failures)
        {
            try
            {
                check();
                Console.WriteLine("[通过] " + name);
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("[失败] " + name + "：" + error.Message);
            }
        }

        /// <summary>确认 BeginMove 只启动动作，到位由轴反馈完成情况决定。</summary>
        private static void MotionCompletesFromFeedback()
        {
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _);
            ITaskAxisMove move = device.BeginMove("X", 10, false, false, Profile());
            Ensure(!device.IsMoveComplete(move, 0.5), "轴还没到位就报告已完成。");
            Ensure(axis.MoveCalls == 1, "BeginMove 没有启动真实轴动作。");

            axis.Complete();
            Ensure(device.IsMoveComplete(move, 0.5), "轴已到位但设备仍报告未完成。");

            device.EndMoveAsync(move, true, CancellationToken.None).GetAwaiter().GetResult();
            Ensure(device.ReadAxis("X").Position == 10, "到位后轴位置不是目标值。");
        }

        /// <summary>确认取消时即使驱动器不响应取消，设备也会补发受控停止并确认停稳。</summary>
        private static void CancelStopsAxis()
        {
            var axis = new FakeAxis("X") { IgnoresCancellation = true };
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _);
            ITaskAxisMove move = device.BeginMove("X", 10, false, false, Profile());
            Ensure(axis.Moving, "动作接受后轴没有进入运动状态。");

            device.EndMoveAsync(move, false, CancellationToken.None).GetAwaiter().GetResult();
            Ensure(axis.StopCalls == 1, "未完成的动作没有触发受控停止。实际停止次数：" + axis.StopCalls + "。");
            Ensure(!axis.Moving, "收尾后轴仍在运动。");
            Ensure(axis.Position != 10, "取消时把中途位置当成了到位位置。");
        }

        /// <summary>确认设备在启动动作前检查使能、回零和行程，不把错误留到运动阶段。</summary>
        private static void MotionPreconditionsAreRejected()
        {
            var disabled = new FakeAxis("X") { ServoEnabled = false };
            MachineTaskDevice disabledDevice = CreateDevice(disabled, new FakeLogicalIo(), out _);
            ExpectMessage(() => disabledDevice.BeginMove("X", 1, false, false, Profile()), "未使能");

            var notHomed = new FakeAxis("X") { Homed = false };
            MachineTaskDevice notHomedDevice = CreateDevice(notHomed, new FakeLogicalIo(), out _);
            ExpectMessage(() => notHomedDevice.BeginMove("X", 1, false, false, Profile()), "尚未回零");

            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _);
            ExpectMessage(() => device.BeginMove("X", 500, false, false, Profile()), "超出配置行程");
            Ensure(axis.MoveCalls == 0, "前置检查失败后仍向轴发出了运动命令。");
        }

        /// <summary>确认轴组动作在真机适配器上明确拒绝，不退化成逐轴移动。</summary>
        private static void GroupMoveIsRejected()
        {
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _);
            var targets = new List<TaskAxisGroupTarget>
            {
                new TaskAxisGroupTarget("X", 5, false, 10)
            };
            ExpectMessage(() => device.BeginGroupMove(targets), "轴组插补");
        }

        /// <summary>确认 IO 读取失败不会被吞掉成 false。</summary>
        private static void IoFailureIsReported()
        {
            var io = new FakeLogicalIo { ReadError = new InvalidOperationException("DI 读取失败：Sensor") };
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, io, out _);
            try
            {
                device.ReadInput("Sensor");
            }
            catch (InvalidOperationException error)
            {
                Ensure(error.Message.Contains("DI 读取失败"), "IO 失败信息没有保留原始原因。" + error.Message);
                return;
            }

            throw new InvalidOperationException("IO 读取失败被静默吞掉了。");
        }

        /// <summary>确认变量写入按目录中的类型落到变量存储。</summary>
        private static void VariablesAreWrittenByType()
        {
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out DeviceVariableStore store);
            device.WriteVariable("Ready", true);
            Ensure(store.GetBoolean("Ready"), "布尔变量没有写入存储。");
        }

        /// <summary>确认配置了 C# 工位流程的设备拒绝任务入口接管。</summary>
        private static void ClaimIsRejectedWhenStationFlowsExist()
        {
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _, withStationFlow: true);
            try
            {
                device.ClaimMachine(null);
            }
            catch (InvalidOperationException error)
            {
                Ensure(error.Message.Contains("工位流程"), "接管被拒但没有说明工位流程占用。" + error.Message);
                return;
            }

            throw new InvalidOperationException("配置了工位流程的设备仍允许任务入口接管。");
        }

        /// <summary>创建一套最小设备环境；可选加入一个工位流程用于验证互斥。</summary>
        private static MachineTaskDevice CreateDevice(
            FakeAxis axis,
            FakeLogicalIo io,
            out DeviceVariableStore variables,
            bool withStationFlow = false)
        {
            TaskResourceCatalog catalog = new TaskResourceCatalog(
                new[] { new TaskAxisInfo("X", "CardA", "mm", -100, 100) },
                new[] { "Sensor" },
                new[] { "Vacuum" },
                new Dictionary<string, DeviceVariableType> { { "Ready", DeviceVariableType.Boolean } },
                new TaskTeachPoint[0],
                null,
                null,
                new Dictionary<string, string> { { "Ready", "false" } });

            variables = new DeviceVariableStore(new[]
            {
                new DeviceVariableDefinition
                {
                    Name = "Ready",
                    DataType = DeviceVariableType.Boolean.ToString(),
                    InitialValue = "false",
                    AllowOperatorWrite = false
                }
            });

            var card = new FakeMotionCard("CardA", axis);
            var group = new MotionCardGroup(new IMotionCard[] { card });
            var flows = withStationFlow
                ? new IStationFlow[] { new WaitingStationFlow() }
                : new IStationFlow[0];
            var runtime = new MachineRuntime(group, flows, new MachineAlarmService(), new FakeSafetyStatus());
            return new MachineTaskDevice(catalog, group, io, variables, runtime);
        }

        /// <summary>检查使用的固定速度曲线。</summary>
        private static AxisMotionProfile Profile()
        {
            return new AxisMotionProfile(10, 100, 100);
        }

        /// <summary>要求调用抛出异常且信息包含指定片段。</summary>
        private static void ExpectMessage(Action action, string expectedFragment)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Ensure(error.Message.Contains(expectedFragment),
                    "错误信息缺少预期片段“" + expectedFragment + "”： " + error.Message);
                return;
            }

            throw new InvalidOperationException("预期拒绝的操作没有抛出异常。");
        }

        /// <summary>断言条件成立，否则抛出检查失败。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>可控的单轴替身：动作完成与停止都由检查显式触发。</summary>
        private sealed class FakeAxis : IAxis
        {
            /// <summary>未完成的动作任务。</summary>
            private TaskCompletionSource<bool> _motion;

            /// <summary>本次动作的目标位置。</summary>
            private double _target;

            /// <summary>建立默认已使能、已回零的测试轴。</summary>
            internal FakeAxis(string name)
            {
                Name = name;
                ServoEnabled = true;
                Homed = true;
            }

            /// <summary>逻辑轴名称。</summary>
            public string Name { get; }

            /// <summary>当前反馈位置。</summary>
            internal double Position { get; private set; }

            /// <summary>是否正在运动。</summary>
            internal bool Moving { get; private set; }

            /// <summary>伺服是否使能。</summary>
            internal bool ServoEnabled { get; set; }

            /// <summary>是否已回零。</summary>
            internal bool Homed { get; set; }

            /// <summary>是否模拟驱动器不响应取消，用于验证补发受控停止。</summary>
            internal bool IgnoresCancellation { get; set; }

            /// <summary>已收到的运动命令次数。</summary>
            internal int MoveCalls { get; private set; }

            /// <summary>已收到的停止命令次数。</summary>
            internal int StopCalls { get; private set; }

            /// <summary>让当前动作正常完成并把位置落到目标。</summary>
            internal void Complete()
            {
                Position = _target;
                Moving = false;
                _motion?.TrySetResult(true);
            }

            /// <summary>读取当前状态快照。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(new AxisStatus(Position, 0, ServoEnabled, Moving, Homed,
                    false, false, false, false, false, null, false));
            }

            /// <summary>测试替身不模拟伺服使能，直接返回。</summary>
            public Task EnableServoAsync(CancellationToken cancellationToken)
            {
                ServoEnabled = true;
                return Task.CompletedTask;
            }

            /// <summary>测试替身不模拟伺服断使能，直接返回。</summary>
            public Task DisableServoAsync(CancellationToken cancellationToken)
            {
                ServoEnabled = false;
                return Task.CompletedTask;
            }

            /// <summary>测试替身把回零作为一次普通动作处理。</summary>
            public Task HomeAsync(CancellationToken cancellationToken)
            {
                MoveCalls++;
                Homed = true;
                return Task.CompletedTask;
            }

            /// <summary>接受绝对定位并保持运动状态，直到检查显式完成或停止。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                MoveCalls++;
                _target = position;
                Moving = true;
                _motion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!IgnoresCancellation)
                {
                    cancellationToken.Register(() => { Moving = false; _motion.TrySetCanceled(); });
                }

                return _motion.Task;
            }

            /// <summary>相对定位按当前位置换算成绝对目标。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                return MoveAbsoluteAsync(Position + distance, profile, cancellationToken);
            }

            /// <summary>测试替身不模拟点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                throw new NotSupportedException("检查不支持点动。");
            }

            /// <summary>停止动作：结束当前运动任务并保留中途位置。</summary>
            public Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken)
            {
                StopCalls++;
                Moving = false;
                _motion?.TrySetCanceled();
                return Task.CompletedTask;
            }

            /// <summary>测试替身没有轴报警可清。</summary>
            public Task ResetAlarmAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>可控的逻辑 IO 替身，可注入读取失败。</summary>
        private sealed class FakeLogicalIo : ILogicalIo
        {
            /// <summary>读取 DI 时抛出的异常；为空表示正常返回假值。</summary>
            internal Exception ReadError { get; set; }

            /// <summary>读取 DI；注入错误时抛出原始异常。</summary>
            public Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken)
            {
                if (ReadError != null)
                {
                    throw ReadError;
                }

                return Task.FromResult(false);
            }

            /// <summary>读取 DO，固定返回假值。</summary>
            public Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
            {
                return Task.FromResult(false);
            }

            /// <summary>写入 DO，检查中不保存状态。</summary>
            public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>只有一个逻辑轴的测试控制卡。</summary>
        private sealed class FakeMotionCard : IMotionCard
        {
            /// <summary>建立只包含一根轴的控制卡。</summary>
            internal FakeMotionCard(string controllerId, IAxis axis)
            {
                ControllerId = controllerId;
                Axes = new[] { axis };
            }

            /// <summary>控制卡标识。</summary>
            public string ControllerId { get; }

            /// <summary>当前连接状态；检查不建立真实连接。</summary>
            public bool IsConnected
            {
                get { return true; }
            }

            /// <summary>本卡的逻辑轴。</summary>
            public IReadOnlyCollection<IAxis> Axes { get; }

            /// <summary>检查不需要真实连接。</summary>
            public Task ConnectAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            /// <summary>检查不需要真实断开。</summary>
            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>急停回路恒为释放状态的测试替身。</summary>
        private sealed class FakeSafetyStatus : IConfigurableMachineSafetyStatus
        {
            /// <summary>视为已配置，允许运行时启动检查继续。</summary>
            public bool IsConfigured
            {
                get { return true; }
            }

            /// <summary>急停始终未触发。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>永远等待输入的工位流程，用于制造"设备已配置自动流程"的状态。</summary>
        private sealed class WaitingStationFlow : IStationFlow
        {
            /// <summary>工位名称。</summary>
            public string StationName
            {
                get { return "检查工位"; }
            }

            /// <summary>当前步骤名称。</summary>
            public string CurrentStepName
            {
                get { return "等待输入"; }
            }

            /// <summary>一直等待，直到运行被取消。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            /// <summary>清除可复位报警，不改变步骤。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }
    }
}
