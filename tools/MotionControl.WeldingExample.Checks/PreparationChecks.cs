using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Equipment.Demo.WeldingExample;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Alarms;

namespace MotionControl.WeldingExample.Checks
{
    /// <summary>检查设备示例的准备工艺，不连接控制器或替代正式界面验收。</summary>
    internal static class PreparationChecks
    {
        /// <summary>执行准备依赖、现场前置、结果验收和中止停止检查。</summary>
        internal static async Task RunAsync()
        {
            await CheckDependencyAndReadyAsync();
            await CheckBlockedConditionsAsync();
            await CheckCancelAndFailedHomeAsync();
            CheckBackendBoundary();
            Console.WriteLine("[通过] 焊接压合准备：依赖、现场阻塞、失败取消停稳、准备后供料及软件后端边界");
        }

        /// <summary>验证压合不能越过焊接让位，完成后启动供料不使准备结果失效。</summary>
        private static async Task CheckDependencyAndReadyAsync()
        {
            var context = new PreparationContext();
            IStationFlow[] flows;
            IStationPreparation[] preparations = CreatePreparations(context, out flows);
            Require(preparations[1].Dependencies.SequenceEqual(new[] { "ClipWeld" }), "压合缺少焊接依赖。");
            await ExpectFailureAsync(() => preparations[1].ExecuteAsync(null, CancellationToken.None), "焊接工位");
            Require(context.Axes["Camera_X"].HomeCount == 0, "焊接未准备时压合已发起回零。");
            var progress = new RecordingProgress();
            await preparations[0].ExecuteAsync(progress, CancellationToken.None);
            await preparations[1].ExecuteAsync(null, CancellationToken.None);
            Require(progress.Actions.Any(action => action.Contains("Press_Z 回零"))
                && progress.Actions.Last() == "准备完成", "准备动作没有提供可定位的进度。");
            foreach (string name in new[] { "Press_Z", "Camera_X" })
                Require(context.Axes[name].Homed && context.Axes[name].HomeCount == 1
                    && context.Axes[name].MoveCount == 1 && context.Axes[name].Position == 0, "准备未执行真实回零与待机定位。");
            context.Io.Inputs["上料到位"] = true;
            await preparations[0].ValidateReadyAsync(CancellationToken.None);
            await preparations[1].ValidateReadyAsync(CancellationToken.None);
            await ExpectFailureAsync(() => preparations[0].ValidateStartAsync(CancellationToken.None), "物料");
            context.Io.Inputs["上料到位"] = false;
            context.Axes["Press_Z"].Position = 1;
            await ExpectFailureAsync(() => preparations[1].ExecuteAsync(null, CancellationToken.None), "待机位");
            Require(context.Axes["Camera_X"].HomeCount == 1, "邻站离开待机位置后仍重复执行压合回零。");
        }

        /// <summary>核对反馈、输出和已执行生产时拒绝准备，并且不清空载具或工步。</summary>
        private static async Task CheckBlockedConditionsAsync()
        {
            var context = new PreparationContext();
            IStationFlow[] flows;
            IStationPreparation[] preparations = CreatePreparations(context, out flows);
            context.Io.Inputs["压装完成"] = true;
            await ExpectFailureAsync(() => preparations[0].ValidateStartAsync(CancellationToken.None), "压装完成");
            context.Io.Inputs["压装完成"] = false;
            context.Io.Outputs["压头动作"] = true;
            await ExpectFailureAsync(() => preparations[0].ValidateStartAsync(CancellationToken.None), "压头动作");
            Require(context.Io.Outputs["压头动作"] && context.Axes["Press_Z"].HomeCount == 0,
                "准备检查擅自释放输出或发起了回零。");
            context.Io.Outputs["压头动作"] = false;
            context.Io.Inputs["上料到位"] = true;
            await flows[0].ExecuteCurrentStepAsync(CancellationToken.None);
            string productionStep = flows[0].CurrentStepName;
            context.Io.Inputs["上料到位"] = false;
            await ExpectFailureAsync(() => preparations[0].ValidateStartAsync(CancellationToken.None), "在制品");
            Require(productionStep == flows[0].CurrentStepName && productionStep.Contains("测试产品"),
                "拒绝准备时改写了生产步骤或载具。");
            var emptyProductionContext = new PreparationContext();
            preparations = CreatePreparations(emptyProductionContext, out flows);
            await flows[0].ExecuteCurrentStepAsync(CancellationToken.None);
            await ExpectFailureAsync(() => preparations[0].ValidateStartAsync(CancellationToken.None), "执行生产");
        }

        /// <summary>回零中取消必须等待受控停止；厂商未建立参考或回零报错不能标记准备完成。</summary>
        private static async Task CheckCancelAndFailedHomeAsync()
        {
            var context = new PreparationContext();
            IStationFlow[] flows;
            IStationPreparation[] preparations = CreatePreparations(context, out flows);
            TestAxis axis = context.Axes["Press_Z"];
            axis.BlockHome = true;
            using (var cancel = new CancellationTokenSource())
            {
                Task execution = preparations[0].ExecuteAsync(null, cancel.Token);
                Task entered = await Task.WhenAny(axis.HomeEntered.Task, Task.Delay(5000));
                Require(entered == axis.HomeEntered.Task, "未进入回零测试动作。");
                cancel.Cancel();
                try { await execution; throw new InvalidOperationException("回零取消没有传播。"); }
                catch (OperationCanceledException) { }
                Require(!axis.Moving && !axis.Homed && axis.StopCount == 1, "取消回零后未受控停稳或误标回零成功。");
            }
            axis.BlockHome = false;
            axis.ReportHomeSuccess = false;
            await ExpectFailureAsync(() => preparations[0].ExecuteAsync(null, CancellationToken.None), "参考");
            Require(axis.MoveCount == 0 && !axis.Moving, "回零失败后仍执行待机定位。");
            axis.ReportHomeSuccess = true;
            axis.FailHome = true;
            await ExpectFailureAsync(() => preparations[0].ExecuteAsync(null, CancellationToken.None), "测试回零错误");
            Require(!axis.Moving && axis.StopCount == 3, "动作失败未确认轴停止。");
            axis.FailHome = false;
            await preparations[0].ExecuteAsync(null, CancellationToken.None);
            await preparations[0].ValidateReadyAsync(CancellationToken.None);
            Require(axis.Homed && axis.MoveCount == 1, "初次准备失败后无法重试成功。");
        }

        /// <summary>测试设备禁止被误用于真实运动控制器。</summary>
        private static void CheckBackendBoundary()
        {
            var context = new PreparationContext();
            var definition = new WeldingExampleEquipmentDefinition();
            IReadOnlyList<IStationFlow> flows = definition.CreateStationFlows(context);
            context.Backend = "ZMotion";
            try { definition.CreatePreparations(context, flows); }
            catch (InvalidOperationException error)
            {
                Require(error.Message.Contains("软件控制器"), "后端拒绝没有说明测试边界。");
                return;
            }
            throw new InvalidOperationException("测试准备允许接入真实后端。");
        }

        /// <summary>用同一设备实例创建生产和准备入口，避免准备读到另一套载具状态。</summary>
        private static IStationPreparation[] CreatePreparations(PreparationContext context, out IStationFlow[] flows)
        {
            var definition = new WeldingExampleEquipmentDefinition();
            flows = definition.CreateStationFlows(context).ToArray();
            return definition.CreatePreparations(context, flows).ToArray();
        }

        /// <summary>要求异步操作失败且保留具体现场信息。</summary>
        private static async Task ExpectFailureAsync(Func<Task> action, string expected)
        {
            try { await action(); }
            catch (InvalidOperationException error)
            {
                Require(error.Message.Contains(expected), "错误未包含预期信息“" + expected + "”：" + error.Message);
                return;
            }
            throw new InvalidOperationException("预期拒绝但操作成功：" + expected);
        }

        /// <summary>不满足检查条件时报告原因。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>同步采集工位准备动作，避免测试依赖 UI 调度器。</summary>
        private sealed class RecordingProgress : IProgress<StationPreparationProgress>
        {
            /// <summary>已报告动作的顺序。</summary>
            internal readonly List<string> Actions = new List<string>();
            /// <summary>记录当前动作。</summary>
            public void Report(StationPreparationProgress value) { Actions.Add(value.Action); }
        }

        /// <summary>准备检查使用的独立设备上下文，轴实例在生产与准备间共用。</summary>
        private sealed class PreparationContext : IEquipmentContext
        {
            /// <summary>各逻辑轴的实际测试状态。</summary>
            internal readonly Dictionary<string, TestAxis> Axes = new Dictionary<string, TestAxis>();
            /// <summary>测试输入与输出。</summary>
            internal readonly TestIo Io = new TestIo();
            /// <summary>仅用于验证后端限制的配置名称。</summary>
            internal string Backend = "Virtual";
            /// <summary>建立未使能、未回零的四个测试轴。</summary>
            internal PreparationContext()
            {
                foreach (string name in new[] { "Load_X", "Press_Z", "Camera_X", "Unload_X" }) Axes.Add(name, new TestAxis(name));
            }
            /// <summary>测试设备名。</summary>
            public string EquipmentId { get { return "PreparationCheck"; } }
            /// <summary>样例允许的毫米行程。</summary>
            public IReadOnlyList<AxisHardwareDefinition> AxisDefinitions { get { return Axes.Keys.Select(name => new AxisHardwareDefinition { Name = name, MinimumPosition = 0, MaximumPosition = 1 }).ToArray(); } }
            /// <summary>样例使用的逻辑输入。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> InputDefinitions { get { return new[] { "上料到位", "压装完成", "检测完成", "下料到位" }.Select(name => new DigitalIoHardwareDefinition { Name = name }).ToArray(); } }
            /// <summary>样例使用的逻辑输出。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> OutputDefinitions { get { return new[] { "夹具夹紧", "压头动作", "相机触发", "下料电磁阀" }.Select(name => new DigitalIoHardwareDefinition { Name = name }).ToArray(); } }
            /// <summary>逻辑 IO 测试入口。</summary>
            public ILogicalIo LogicalIo { get { return Io; } }
            /// <summary>独立报警服务。</summary>
            public IMachineAlarmService AlarmService { get; } = new MachineAlarmService();
            /// <summary>只提供控制器选择配置。</summary>
            public bool FileExists(string path) { return path == "Parameters/ControllerBackend.json"; }
            /// <summary>读取测试后端配置。</summary>
            public T LoadJson<T>(string path) where T : class { return new WeldingExampleEquipmentDefinition.WeldingTestBackend { Backend = Backend } as T; }
            /// <summary>返回传感器停机报警定义。</summary>
            public MachineAlarmDefinition ResolveAlarm(string code) { return new MachineAlarmDefinition(code, "测试超时", MachineAlarmKind.StopMachine); }
            /// <summary>取得固定轴实例。</summary>
            public IAxis GetAxis(string name) { return Axes[name]; }
        }

        /// <summary>能注入取消、虚假回零结果和驱动错误的轴。</summary>
        private sealed class TestAxis : IAxis
        {
            /// <summary>当前逻辑轴名。</summary>
            public string Name { get; private set; }
            /// <summary>当前实际位置。</summary>
            internal double Position = 0.5;
            /// <summary>当前运动状态。</summary>
            internal bool Moving;
            /// <summary>当前伺服状态。</summary>
            internal bool Enabled;
            /// <summary>当前坐标参考状态。</summary>
            internal bool Homed;
            /// <summary>是否让回零停留至取消。</summary>
            internal bool BlockHome;
            /// <summary>回零返回后是否提供已建立参考的结果。</summary>
            internal bool ReportHomeSuccess = true;
            /// <summary>是否在回零动作中模拟驱动异常。</summary>
            internal bool FailHome;
            /// <summary>已接受的回零命令数。</summary>
            internal int HomeCount;
            /// <summary>已接受的定位命令数。</summary>
            internal int MoveCount;
            /// <summary>已接受的受控停止数。</summary>
            internal int StopCount;
            /// <summary>通知测试已进入回零。</summary>
            internal readonly TaskCompletionSource<bool> HomeEntered = new TaskCompletionSource<bool>();
            /// <summary>记录逻辑轴名称。</summary>
            internal TestAxis(string name) { Name = name; }
            /// <summary>读取当前实际状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new AxisStatus(Position, Moving ? 1 : 0, Enabled, Moving, Homed,
                    false, false, false, false, false, null, false));
            }
            /// <summary>模拟伺服使能。</summary>
            public Task EnableServoAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Enabled = true; return Task.CompletedTask; }
            /// <summary>本检查不关闭伺服。</summary>
            public Task DisableServoAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>模拟回零；取消时保留运动状态以检验上层停止确认。</summary>
            public async Task HomeAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                HomeCount++; Homed = false; Moving = true; HomeEntered.TrySetResult(true);
                if (FailHome) throw new InvalidOperationException("测试回零错误");
                if (BlockHome) await Task.Delay(Timeout.Infinite, token);
                await Task.Delay(20, token);
                Position = 0; Homed = ReportHomeSuccess; Moving = false;
            }
            /// <summary>只在有效参考下执行绝对定位。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (!Enabled || !Homed) throw new InvalidOperationException("测试轴尚未准备。");
                MoveCount++; Position = position; return Task.CompletedTask;
            }
            /// <summary>本检查不使用相对运动。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本检查不使用点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>确认收到受控停止并清除运动状态。</summary>
            public Task StopAsync(AxisStopMode mode, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Require(mode == AxisStopMode.Controlled, "准备中止未使用受控停止。");
                StopCount++; Moving = false; return Task.CompletedTask;
            }
            /// <summary>本检查不直接清除轴报警。</summary>
            public Task ResetAlarmAsync(CancellationToken token) { throw new NotSupportedException(); }
        }

        /// <summary>显式输入与输出，不自动伪造完成反馈。</summary>
        private sealed class TestIo : ILogicalIo
        {
            /// <summary>当前逻辑输入值。</summary>
            internal readonly Dictionary<string, bool> Inputs = new Dictionary<string, bool>();
            /// <summary>当前逻辑输出值。</summary>
            internal readonly Dictionary<string, bool> Outputs = new Dictionary<string, bool>();
            /// <summary>读取输入值。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Inputs.ContainsKey(name) && Inputs[name]); }
            /// <summary>读取输出值。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Outputs.ContainsKey(name) && Outputs[name]); }
            /// <summary>为生产样例写入输出，准备流程自身不写输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token) { token.ThrowIfCancellationRequested(); Outputs[name] = value; return Task.CompletedTask; }
        }
    }
}
