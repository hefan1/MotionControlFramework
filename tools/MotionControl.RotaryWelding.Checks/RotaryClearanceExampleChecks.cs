using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Equipment.Demo.RotaryWelding;
using MotionControl.Framework.Contracts.Clearance;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.RotaryWelding.Checks
{
    /// <summary>设备清料样例的隔离内存检查；不代表正式 Host 或实机验收。</summary>
    internal static class RotaryClearanceExampleChecks
    {
        /// <summary>验证固定方案、实际顺序执行及未知物料和承接反馈失败的拒绝。</summary>
        internal static async Task RunAsync()
        {
            ControlledClearanceProfile profile = RotarySingleAClearance.CreateProfile();
            if (profile.Plan.RequiredResources.Count(name => name.StartsWith("Axis:", StringComparison.Ordinal)) != 13
                || !profile.Plan.RequiredResources.Contains("Zone:RotaryWeld") || !profile.Plan.RequiredResources.Contains("Zone:RotaryPress")
                || !profile.Plan.RequiredResources.Contains("Zone:Buffer"))
                throw new InvalidOperationException("清料方案遗漏轴或共享区域。");
            foreach (string output in new[] { "A夹紧", "B夹紧", "下料夹紧", "取料夹紧", "缓存交接", "剔除启动", "焊接启动", "压装启动", "相机触发", "相机结果复位", "合格出料" })
                if (!profile.Plan.RequiredResources.Contains("Output:" + output)) throw new InvalidOperationException("清料方案遗漏输出资源：" + output);
            if (new RotaryWeldingEquipmentDefinition().CreateClearanceProfiles().Any()
                || new RotaryWeldingEquipmentDefinition(false).CreateClearanceProfiles().Single().Id != RotarySingleAClearance.ProfileId)
                throw new InvalidOperationException("清料方案设备选择错误。");
            var model = new RotaryPlantFake();
            await RotaryMaterialInspection.VerifySingleAClearanceAsync(model, CancellationToken.None);
            var executor = new ControlledClearanceExecutor(model.MotionAxes, model);
            await executor.ExecuteAsync(profile.Plan, CancellationToken.None);
            if (executor.HasUnsettledAction || model.Input("A夹位有料") || model.Input("缓存位有料")
                || model.Input("下料位有料") || !model.Input("缓存位无料") || !model.Rejected
                || model.Axes["Unload_X"].Position != 0 || model.Axes["Buffer_X"].Position != 0
                || model.Axes["Buffer_Y"].Position != 0 || model.Axes["Reject_Z"].Position != 0)
                throw new InvalidOperationException("清料后反馈、剔除或轴返回不符合预期。");

            foreach (string position in new[] { "取料夹爪", "上料滑台", "B夹位", "缓存位", "下料位" })
            {
                var unknown = new RotaryPlantFake();
                unknown.SetInput(position + "无料", false);
                bool failed = false;
                try { await new ControlledClearanceExecutor(unknown.MotionAxes, unknown).ExecuteAsync(profile.Plan, CancellationToken.None); }
                catch (InvalidOperationException) { failed = true; }
                if (!failed || unknown.Commands != 0) throw new InvalidOperationException("未知物料状态错误地下发了清料动作：" + position);
            }
            var lostPermit = new RotaryPlantFake(); lostPermit.SetInput("旋转区清料允许", false);
            bool permitRejected = false;
            try { await new ControlledClearanceExecutor(lostPermit.MotionAxes, lostPermit).ExecuteAsync(profile.Plan, CancellationToken.None); }
            catch (InvalidOperationException) { permitRejected = true; }
            if (!permitRejected || lostPermit.Commands != 0) throw new InvalidOperationException("区域许可无效时未拒绝清料。");
            foreach (string output in new[] { "A夹紧", "B夹紧", "下料夹紧", "取料夹紧", "缓存交接", "剔除启动", "焊接启动", "压装启动", "相机触发", "相机结果复位", "合格出料" })
            {
                var unsafeOutput = new RotaryPlantFake(); unsafeOutput.SetOutput(output, output != "A夹紧" && output != "B夹紧");
                bool rejected = false;
                try { await new ControlledClearanceExecutor(unsafeOutput.MotionAxes, unsafeOutput).ExecuteAsync(profile.Plan, CancellationToken.None); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || unsafeOutput.Commands != 0) throw new InvalidOperationException("其他机构输出有效时仍清料：" + output);
            }
            foreach (string busy in new[] { "焊接模组空闲", "压合模组空闲", "上料模组空闲", "视觉模组空闲", "出料模组空闲" })
            {
                var unsafeModule = new RotaryPlantFake(); unsafeModule.SetInput(busy, false);
                bool rejected = false;
                try { await new ControlledClearanceExecutor(unsafeModule.MotionAxes, unsafeModule).ExecuteAsync(profile.Plan, CancellationToken.None); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || unsafeModule.Commands != 0) throw new InvalidOperationException("其他机构未确认空闲仍清料：" + busy);
            }
            var clampContradiction = new RotaryPlantFake();
            clampContradiction.SetInput("A夹紧到位", false); clampContradiction.SetInput("A松开到位", true);
            bool clampRejected = false;
            try { await new ControlledClearanceExecutor(clampContradiction.MotionAxes, clampContradiction).ExecuteAsync(profile.Plan, CancellationToken.None); }
            catch (InvalidOperationException) { clampRejected = true; }
            if (!clampRejected || clampContradiction.Commands != 0)
                throw new InvalidOperationException("A 夹紧输出有效但独立反馈报松开时未拒绝清料。");
            var unreadableOutput = new RotaryPlantFake { UnreadableOutput = "焊接启动" };
            bool readRejected = false;
            try { await new ControlledClearanceExecutor(unreadableOutput.MotionAxes, unreadableOutput).ExecuteAsync(profile.Plan, CancellationToken.None); }
            catch (InvalidOperationException) { readRejected = true; }
            if (!readRejected || unreadableOutput.Commands != 0) throw new InvalidOperationException("输出读取失败时仍清料。");
            var changedOutput = new RotaryPlantFake { TriggerWeldOnUnloadMove = true };
            var changedExecutor = new ControlledClearanceExecutor(changedOutput.MotionAxes, changedOutput);
            bool stoppedOnOutput = false;
            try { await changedExecutor.ExecuteAsync(profile.Plan, CancellationToken.None); }
            catch (Exception) { stoppedOnOutput = true; }
            if (!stoppedOnOutput || !changedExecutor.HasUnsettledAction || !changedOutput.Input("A夹位有料"))
                throw new InvalidOperationException("清料时其他机构输出变化未锁存或错误松开 A 夹具。");
            var noClamp = new RotaryPlantFake { SuppressUnloadClampFeedback = true };
            var refused = new ControlledClearanceExecutor(noClamp.MotionAxes, noClamp);
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                try { await refused.ExecuteAsync(profile.Plan, cancel.Token); }
                catch (OperationCanceledException) { }
                catch (AggregateException error) when (cancel.IsCancellationRequested
                    && error.Flatten().InnerExceptions.All(item => item is OperationCanceledException)) { }
            }
            if (!refused.HasUnsettledAction || !noClamp.Input("A夹位有料") || noClamp.Axes["Unload_X"].Position != 0)
                throw new InvalidOperationException("下料夹紧反馈未到位时没有保留 A 夹位并停止后续动作。");
        }

        /// <summary>用于检查顺序与双路反馈的设备模型；只有对应位置及夹具满足才发生交接。</summary>
        private sealed class RotaryPlantFake : ILogicalIo
        {
            /// <summary>保护并行读取和设备状态变化。</summary>
            private readonly object _sync = new object();
            /// <summary>独立输入反馈，未声明名称视为测试错误。</summary>
            private readonly Dictionary<string, bool> _inputs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            /// <summary>独立输出电平。</summary>
            private readonly Dictionary<string, bool> _outputs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            /// <summary>十三轴测试模型。</summary>
            internal readonly Dictionary<string, FakeAxis> Axes;
            /// <summary>供执行器使用的逻辑轴契约视图。</summary>
            internal readonly IReadOnlyDictionary<string, IAxis> MotionAxes;
            /// <summary>是否抑制下料夹具反馈，用于模拟执行失败。</summary>
            internal bool SuppressUnloadClampFeedback;
            /// <summary>下料轴动作中注入焊接输出，验证持续条件。</summary>
            internal bool TriggerWeldOnUnloadMove;
            /// <summary>注入指定输出读取失败。</summary>
            internal string UnreadableOutput;
            /// <summary>已执行的轴和输出命令数。</summary>
            internal int Commands;
            /// <summary>不良出口是否实际收到一件料。</summary>
            internal bool Rejected;

            /// <summary>建立 A 有料、其余五处空料、A/B 锁紧和区域许可有效的测试初态。</summary>
            internal RotaryPlantFake()
            {
                Axes = new[] { "Unload_X", "Load_X", "Press_Z", "Transfer_Y", "Camera_X", "Camera_Z", "Feeder_X", "Feeder_Z", "Buffer_X", "Buffer_Y", "Reject_Z", "Conveyor_R", "Weld_Z" }
                    .ToDictionary(name => name, name => new FakeAxis(name, this, name == "Conveyor_R" ? 90 : 0), StringComparer.OrdinalIgnoreCase);
                MotionAxes = Axes.ToDictionary(item => item.Key, item => (IAxis)item.Value, StringComparer.OrdinalIgnoreCase);
                foreach (string place in new[] { "取料夹爪", "上料滑台", "A夹位", "B夹位", "缓存位", "下料位" }) Material(place, place == "A夹位");
                Pair("A夹紧到位", "A松开到位", true); Pair("B夹紧到位", "B松开到位", true);
                Pair("取料夹紧到位", "取料松开到位", false);
                Pair("下料夹紧到位", "下料松开到位", false);
                foreach (string name in new[] { "旋转区清料允许", "缓存区清料允许", "不良出口允许", "焊接模组空闲", "压合模组空闲", "上料模组空闲", "视觉模组空闲", "出料模组空闲" }) _inputs[name] = true;
                foreach (string name in new[] { "上料到位", "缓存载具到位", "剔除完成" }) _inputs[name] = false;
                foreach (string name in new[] { "下料夹紧", "取料夹紧", "缓存交接", "剔除启动", "焊接启动", "压装启动", "相机触发", "相机结果复位", "合格出料" }) _outputs[name] = false;
                _outputs["A夹紧"] = true; _outputs["B夹紧"] = true;
            }
            /// <summary>设置互斥的两路传感器。</summary>
            private void Pair(string first, string second, bool value) { _inputs[first] = value; _inputs[second] = !value; }
            /// <summary>改变模拟位置的独立有无料反馈。</summary>
            private void Material(string place, bool present) { Pair(place + "有料", place + "无料", present); }
            /// <summary>测试中注入指定反馈。</summary>
            internal void SetInput(string name, bool value) { lock (_sync) _inputs[name] = value; }
            /// <summary>独立注入输出电平，不替代机构到位和空闲反馈。</summary>
            internal void SetOutput(string name, bool value) { lock (_sync) _outputs[name] = value; }
            /// <summary>同步读取指定反馈。</summary>
            internal bool Input(string name) { lock (_sync) return _inputs[name]; }
            /// <summary>读取独立输入。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token)
            { token.ThrowIfCancellationRequested(); lock (_sync) return Task.FromResult(_inputs[name]); }
            /// <summary>读取输出电平。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (string.Equals(name, UnreadableOutput, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("输出反馈读取失败：" + name);
                    return Task.FromResult(_outputs[name]);
                }
            }
            /// <summary>根据夹具和到位条件模拟交接，不能由单个输出直接推断物料有无。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    Commands++; _outputs[name] = value;
                    if (name == "A夹紧")
                    {
                        Pair("A夹紧到位", "A松开到位", value);
                        if (!value && _inputs["下料位有料"] && _inputs["下料夹紧到位"] && Axes["Unload_X"].Position == 1) Material("A夹位", false);
                    }
                    if (name == "下料夹紧" && !SuppressUnloadClampFeedback)
                    {
                        Pair("下料夹紧到位", "下料松开到位", value);
                        if (!value && _inputs["下料位有料"] && _inputs["缓存载具到位"] && Axes["Unload_X"].Position == 0)
                        { Material("下料位", false); Material("缓存位", true); }
                    }
                    if (name == "缓存交接") _inputs["缓存载具到位"] = value;
                    if (name == "剔除启动")
                    {
                        _inputs["剔除完成"] = value;
                        if (value && _inputs["缓存位有料"] && _inputs["不良出口允许"] && Axes["Buffer_X"].Position == 2
                            && Axes["Buffer_Y"].Position == 2 && Axes["Reject_Z"].Position == 1)
                        { Material("缓存位", false); Rejected = true; }
                    }
                }
                return Task.CompletedTask;
            }
            /// <summary>模拟轴到位；共同夹持成立后才产生下料位有料反馈。</summary>
            internal void Move(FakeAxis axis, double position)
            {
                lock (_sync)
                {
                    Commands++; axis.Position = position;
                    if (TriggerWeldOnUnloadMove && axis.Name == "Unload_X") _outputs["焊接启动"] = true;
                    if (axis.Name == "Unload_X" && position == 1 && _inputs["A夹位有料"] && _inputs["A夹紧到位"]
                        && _inputs["下料夹紧到位"] && _inputs["下料位无料"] && Axes["Conveyor_R"].Position == 90)
                        Material("下料位", true);
                }
            }
        }

        /// <summary>能报告明确停止及位置的设备测试轴。</summary>
        private sealed class FakeAxis : IAxis
        {
            /// <summary>轴名称。</summary>
            public string Name { get; }
            /// <summary>受测试设备维护的位置。</summary>
            internal double Position;
            /// <summary>所属设备模型。</summary>
            private readonly RotaryPlantFake _plant;
            /// <summary>创建初始位置。</summary>
            internal FakeAxis(string name, RotaryPlantFake plant, double position) { Name = name; _plant = plant; Position = position; }
            /// <summary>返回已回零、无报警和无触发限位的停止状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken token)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(new AxisStatus(Position, 0, true, false, true, false, false, false, false, false, null, false)); }
            /// <summary>仅执行计划允许的固定定位。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken token)
            { token.ThrowIfCancellationRequested(); _plant.Move(this, position); return Task.CompletedTask; }
            /// <summary>模拟受控停止。</summary>
            public Task StopAsync(AxisStopMode mode, CancellationToken token) { return Task.CompletedTask; }
            /// <summary>本样例不执行使能。</summary>
            public Task EnableServoAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本样例不执行断使能。</summary>
            public Task DisableServoAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本样例不执行回零。</summary>
            public Task HomeAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本样例不执行相对定位。</summary>
            public Task MoveRelativeAsync(double position, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本样例不执行点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本样例不执行报警复位。</summary>
            public Task ResetAlarmAsync(CancellationToken token) { throw new NotSupportedException(); }
        }
    }
}
