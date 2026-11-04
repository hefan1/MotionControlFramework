using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Equipment.Demo.RotaryWelding;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;

namespace MotionControl.RotaryWelding.Checks
{
    /// <summary>十三轴双夹位设备的受控软件检查，不连接正式 Host 或运动卡。</summary>
    internal static class Program
    {
        /// <summary>执行并行、堵料、暂停和扫码失败等检查。</summary>
        private static int Main()
        {
            try { CheckMaterialInspectionAsync().GetAwaiter().GetResult(); RotaryClearanceExampleChecks.RunAsync().GetAwaiter().GetResult(); CheckAsync().GetAwaiter().GetResult(); CheckMotionOnlyAsync().GetAwaiter().GetResult(); CheckScannerFailureAsync().GetAwaiter().GetResult(); CheckClampConflictAsync().GetAwaiter().GetResult(); CheckEvidenceWriteFailureAsync().GetAwaiter().GetResult(); CheckInterruptedOutputsAsync().GetAwaiter().GetResult(); CheckPreparationClampsAsync().GetAwaiter().GetResult(); CheckPreparationFeedbackAsync().GetAwaiter().GetResult(); CheckRecordFailureAsync(false).GetAwaiter().GetResult(); CheckRecordFailureAsync(true).GetAwaiter().GetResult(); Console.WriteLine("十三轴代码流程：独立物料反馈、纯运动、50件、准备反馈、夹具联锁、逐件记录及故障、在途输出恢复等内部检查通过。"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        /// <summary>独立反馈必须明确区分有料、空料、未知、冲突、变化及读取失败。</summary>
        private static async Task CheckMaterialInspectionAsync()
        {
            var io = new MaterialReadFake();
            io.Values["A夹位有料"] = true;
            io.Values["B夹位无料"] = true;
            io.Values["缓存位有料"] = true;
            io.Values["缓存位无料"] = true;
            var states = await RotaryMaterialInspection.ReadAsync(io, CancellationToken.None);
            if (states.Count != 4 || states[0].Condition != RotaryMaterialCondition.Present
                || states[1].Condition != RotaryMaterialCondition.Empty || states[2].Condition != RotaryMaterialCondition.Conflict
                || states[3].Condition != RotaryMaterialCondition.Unknown)
                throw new InvalidOperationException("未区分有料、空料、冲突和未知。 ");
            io.ChangeName = "A夹位有料";
            states = await RotaryMaterialInspection.ReadAsync(io, CancellationToken.None);
            if (states[0].Condition != RotaryMaterialCondition.Unstable) throw new InvalidOperationException("反馈变化被误判为稳定。 ");
            io.ChangeName = null; io.FailName = "B夹位有料";
            bool rejected = false;
            try { await RotaryMaterialInspection.ReadAsync(io, CancellationToken.None); }
            catch (InvalidOperationException error) { rejected = error.Message.Contains("模拟读取失败"); }
            if (!rejected) throw new InvalidOperationException("读取失败被误判为无料。 ");

            var clearance = new MaterialReadFake();
            clearance.Values["A夹位有料"] = true;
            foreach (string position in new[] { "取料夹爪", "上料滑台", "B夹位", "缓存位", "下料位" })
                clearance.Values[position + "无料"] = true;
            await RotaryMaterialInspection.VerifySingleAClearanceAsync(clearance, CancellationToken.None);
            foreach (string position in new[] { "取料夹爪", "上料滑台", "B夹位", "缓存位", "下料位" })
            {
                // 无料输入断开、有无料冲突或确认有料，三种情况都不能作为本例可处理状态。
                clearance.Values[position + "无料"] = false;
                await AssertClearanceMaterialRejected(clearance, position);
                clearance.Values[position + "有料"] = true;
                await AssertClearanceMaterialRejected(clearance, position);
                clearance.Values[position + "无料"] = true;
                await AssertClearanceMaterialRejected(clearance, position);
                clearance.Values[position + "有料"] = false;
            }
            clearance.Values["A夹位无料"] = true;
            await AssertClearanceMaterialRejected(clearance, "A夹位");
            clearance.Values["A夹位无料"] = false;
            clearance.FailName = "取料夹爪有料";
            rejected = false;
            try { await RotaryMaterialInspection.VerifySingleAClearanceAsync(clearance, CancellationToken.None); }
            catch (InvalidOperationException error) { rejected = error.Message.Contains("模拟读取失败"); }
            if (!rejected) throw new InvalidOperationException("清料夹爪反馈读取失败未拒绝。 ");
        }

        /// <summary>确认本例不接受指定位置的未知、冲突或额外带料。</summary>
        private static async Task AssertClearanceMaterialRejected(MaterialReadFake io, string position)
        {
            try { await RotaryMaterialInspection.VerifySingleAClearanceAsync(io, CancellationToken.None); }
            catch (InvalidOperationException error) { if (error.Message.Contains(position)) return; throw; }
            throw new InvalidOperationException("清料错误接受了位置“" + position + "”的不受支持状态。");
        }

        /// <summary>只读测试 IO，可指定反馈变化或读取失败，不接受输出操作。</summary>
        private sealed class MaterialReadFake : ILogicalIo
        {
            /// <summary>按逻辑名称保存反馈电平，未设置的输入默认为无效。</summary>
            internal readonly Dictionary<string, bool> Values = new Dictionary<string, bool>();
            /// <summary>指定第二次读取时反转的输入名称。</summary>
            internal string ChangeName;
            /// <summary>指定读取失败的输入名称。</summary>
            internal string FailName;
            /// <summary>跟踪变化输入的本轮读取次数。</summary>
            private int _changeReads;
            /// <summary>读取测试输入；对变化通道第二次读取返回相反电平。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (name == FailName) throw new InvalidOperationException("模拟读取失败：" + name);
                bool value;
                Values.TryGetValue(name, out value);
                if (name == ChangeName && ++_changeReads == 2) value = !value;
                return Task.FromResult(value);
            }
            /// <summary>只读核查不应读取输出。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>只读核查不应切换输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token) { throw new NotSupportedException(); }
        }

        /// <summary>运行五个代码工位，核对全部轴动作、载具顺序和堵料恢复。</summary>
        private static async Task CheckAsync()
        {
            var context = new FakeContext();
            var flows = new RotaryWeldingEquipmentDefinition().CreateStationFlows(context).ToArray();
            context.Io.BlockExit = true;
            using (var cancel = new CancellationTokenSource())
            {
                Task[] runs = flows.Select(flow => RunAsync(flow, cancel.Token)).ToArray();
                await WaitForAsync(() => flows[4].CurrentStepName.Contains("等待对应出料口允许"), TimeSpan.FromSeconds(15));
                await Task.Delay(250);
                if (((IStationCycleStatistics)flows[4]).CompletedCycles != 0) throw new InvalidOperationException("出口堵塞时产品被错误放行。");
                string[] before = flows.Select(flow => flow.CurrentStepName).ToArray();
                cancel.Cancel();
                try { await Task.WhenAll(runs); } catch (OperationCanceledException) { }
                await Task.Delay(50);
                if (!before.SequenceEqual(flows.Select(flow => flow.CurrentStepName))) throw new InvalidOperationException("暂停后步骤仍变化。");
                RotaryEvidence saved = context.Evidence;
                if (saved == null || !saved.ProductionStarted || saved.Buffer == null || !saved.Inspecting || !saved.Buffer.Id.StartsWith("SCAN-", StringComparison.Ordinal))
                    throw new InvalidOperationException("堵塞中的缓存产品及不确定状态没有保存为证据。");
                IStationFlow reopened = new RotaryWeldingEquipmentDefinition().CreateStationFlows(context)[0];
                try { await reopened.ExecuteCurrentStepAsync(CancellationToken.None); throw new InvalidOperationException("旧会话有生产证据时错误允许继续。"); }
                catch (InvalidOperationException error) { if (!error.Message.Contains("前次双夹位生产证据")) throw; }
                if (!ReferenceEquals(saved, context.Evidence)) throw new InvalidOperationException("新会话覆盖了旧生产证据。");
            }
            context.Io.BlockExit = false;
            using (var cancel = new CancellationTokenSource())
            {
                Task[] runs = flows.Select(flow => RunAsync(flow, cancel.Token)).ToArray();
                try { await WaitForAsync(() => ((IStationCycleStatistics)flows[4]).CompletedCycles >= 50 || runs.Any(task => task.IsFaulted), TimeSpan.FromSeconds(90)); }
                catch (TimeoutException) { throw new InvalidOperationException("50件超时：" + string.Join("；", flows.Select(flow => flow.StationName + "=" + flow.CurrentStepName))); }
                Task failed = runs.FirstOrDefault(task => task.IsFaulted);
                if (failed != null) throw new InvalidOperationException("工位流程失败：" + string.Join("；", flows.Select(flow => flow.StationName + "=" + flow.CurrentStepName)), failed.Exception);
                cancel.Cancel();
                try { await Task.WhenAll(runs); } catch (OperationCanceledException) { }
            }
            foreach (FakeAxis axis in context.Axes.Values)
                if (axis.Targets.Count < 2) throw new InvalidOperationException("轴未经过两个测试位置：" + axis.Name);
            if (!context.Axes["Conveyor_R"].Targets.Contains(-90) || !context.Axes["Conveyor_R"].Targets.Contains(90))
                throw new InvalidOperationException("旋转台未交换两个夹位。");
            if (context.Io.NgCount == 0 || context.Io.OkCount == 0 || context.Io.ScanCount < 50)
                throw new InvalidOperationException("扫码或 OK/NG 出口未覆盖。");
            if (context.Io.MaxProcessConcurrent < 2) throw new InvalidOperationException("未观察到焊接与压合并行。");
            var records = context.Records.Values.ToArray();
            if (records.Length < 50 || records.Select(x => x.Id).Distinct().Count() != records.Length
                || !records.Any(x => x.Nest == "A") || !records.Any(x => x.Nest == "B")
                || !records.Any(x => x.Result == "OK" && x.Outlet == "合格出口")
                || !records.Any(x => x.Result == "NG" && x.Outlet == "不良出口")
                || records.Any(x => string.IsNullOrEmpty(x.SessionId) || x.CompletedUtc == default(DateTime)
                    || x.Station != "复检分拣" || x.Stage != "已完成" || (x.Nest != "A" && x.Nest != "B")))
                throw new InvalidOperationException("逐件记录缺失、重复或未覆盖夹位与出料结果。");
        }

        /// <summary>检查每组夹具的闭合、冲突、未到位与读取失败都在使能回零之前拒绝。</summary>
        private static async Task CheckPreparationClampsAsync()
        {
            foreach (string clamp in new[] { "取料", "A", "B" })
                foreach (string fault in new[] { "closed", "conflict", "neither", "unknown" })
                {
                    var context = new FakeContext(); context.Io.NoMaterial = true; context.Io.PreparationClamp = clamp; context.Io.PreparationFault = fault;
                    var definition = new RotaryWeldingEquipmentDefinition();
                    var flows = definition.CreateStationFlows(context);
                    var preparation = definition.CreatePreparations(context, flows)[0];
                    bool rejected = false;
                    try { await preparation.ExecuteAsync(null, CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
                    if (!rejected || context.Axes.Values.Any(axis => axis.EnabledCalls != 0 || axis.HomeCalls != 0))
                        throw new InvalidOperationException("夹具异常未在回零前拦截：" + clamp + " " + fault);
                }
            var disabled = new FakeContext(); disabled.Io.NoMaterial = true;
            disabled.Axes["Conveyor_R"].Enabled = false;
            var disabledEquipment = new RotaryWeldingEquipmentDefinition();
            bool servoRejected = false;
            try { await disabledEquipment.CreatePreparations(disabled, disabledEquipment.CreateStationFlows(disabled))[0].ExecuteAsync(null, CancellationToken.None); }
            catch (InvalidOperationException error) { servoRejected = error.Message.Contains("使能"); }
            if (!servoRejected || disabled.Axes.Values.Any(axis => axis.HomeCalls != 0 || axis.EnabledCalls != 0))
                throw new InvalidOperationException("最后轴未使能时准备未在任何轴动作前拒绝。");
            var lostServo = new FakeContext(); lostServo.Io.NoMaterial = true;
            lostServo.Axes["Feeder_Z"].AfterHome = () => lostServo.Axes["Camera_Z"].Enabled = false;
            var lostEquipment = new RotaryWeldingEquipmentDefinition();
            bool lostRejected = false;
            try { await lostEquipment.CreatePreparations(lostServo, lostEquipment.CreateStationFlows(lostServo))[0].ExecuteAsync(null, CancellationToken.None); }
            catch (InvalidOperationException error) { lostRejected = error.Message.Contains("使能"); }
            if (!lostRejected || lostServo.Axes["Camera_Z"].HomeCalls != 0 || lostServo.Axes.Values.Any(axis => axis.EnabledCalls != 0)
                || lostServo.Axes.Values.Sum(axis => axis.StopCalls) == 0)
                throw new InvalidOperationException("逐轴准备期间失能未阻止后续回零并停止收尾。");
            var good = new FakeContext(); good.Io.NoMaterial = true;
            var equipment = new RotaryWeldingEquipmentDefinition();
            await equipment.CreatePreparations(good, equipment.CreateStationFlows(good))[0].ExecuteAsync(null, CancellationToken.None);
            if (good.Axes.Values.Any(axis => axis.EnabledCalls != 0 || axis.HomeCalls != 1)) throw new InvalidOperationException("正常夹具反馈未完成逐轴准备。");
        }

        /// <summary>位置或速度未知及回零后仍有速度时，准备不能继续或误报就绪。</summary>
        private static async Task CheckPreparationFeedbackAsync()
        {
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                foreach (bool position in new[] { true, false })
                {
                    var context = new FakeContext(); context.Io.NoMaterial = true;
                    if (position) context.Axes["Feeder_Z"].ReportedPosition = invalid;
                    else context.Axes["Feeder_Z"].ReportedVelocity = invalid;
                    var equipment = new RotaryWeldingEquipmentDefinition();
                    var preparation = equipment.CreatePreparations(context, equipment.CreateStationFlows(context))[0];
                    bool rejected = false;
                    try { await preparation.ExecuteAsync(null, CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
                    if (!rejected || context.Axes.Values.Any(axis => axis.EnabledCalls != 0 || axis.HomeCalls != 0))
                        throw new InvalidOperationException("轴反馈未知时不能开始使能或回零。 ");
                    rejected = false;
                    try { await preparation.ValidateReadyAsync(CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
                    if (!rejected) throw new InvalidOperationException("轴反馈未知时不能标记准备就绪。 ");
                }
            }
            var moving = new FakeContext(); moving.Io.NoMaterial = true;
            moving.Axes["Feeder_Z"].VelocityAfterHome = 0.1;
            var definition = new RotaryWeldingEquipmentDefinition();
            var prepare = definition.CreatePreparations(moving, definition.CreateStationFlows(moving))[0];
            bool blocked = false;
            try { await prepare.ExecuteAsync(null, CancellationToken.None); } catch (InvalidOperationException) { blocked = true; }
            if (!blocked || moving.Axes["Camera_Z"].HomeCalls != 0)
                throw new InvalidOperationException("上一轴回零后仍有速度时不能继续回零其他轴。 ");
        }

        /// <summary>没有扫码映射和记录服务时，纯运动样例仍须完成准备与多工位循环。</summary>
        private static async Task CheckMotionOnlyAsync()
        {
            var context = new MotionOnlyContext();
            context.Inner.Io.NoMaterial = true;
            context.Inner.Io.FailScan = true;
            var equipment = new RotaryWeldingEquipmentDefinition(false);
            var flows = equipment.CreateStationFlows(context).ToArray();
            await equipment.CreatePreparations(context, flows)[0].ExecuteAsync(null, CancellationToken.None);
            context.Inner.Io.NoMaterial = false;
            using (var cancel = new CancellationTokenSource())
            {
                Task[] runs = flows.Select(flow => RunAsync(flow, cancel.Token)).ToArray();
                await WaitForAsync(() => ((IStationCycleStatistics)flows[4]).CompletedCycles >= 6 || runs.Any(task => task.IsFaulted), TimeSpan.FromSeconds(35));
                Task failed = runs.FirstOrDefault(task => task.IsFaulted);
                if (failed != null) throw new InvalidOperationException("纯运动工位运行失败。", failed.Exception);
                cancel.Cancel();
                try { await Task.WhenAll(runs); } catch (OperationCanceledException) { }
            }
            if (context.Inner.Io.ScanCount != 0 || context.Inner.Io.RisingCount("扫码触发") != 0 || context.Inner.Records.Count != 0)
                throw new InvalidOperationException("纯运动流程意外调用扫码或逐件记录。 ");
            var incomplete = context.Inner.Axes.Values.Where(axis => axis.Targets.Count < 2).Select(axis => axis.Name).ToArray();
            if (context.Inner.Io.OkCount == 0 || context.Inner.Io.NgCount == 0 || incomplete.Length != 0)
                throw new InvalidOperationException("纯运动流程未覆盖检测分流或轴动作：OK=" + context.Inner.Io.OkCount
                    + "，NG=" + context.Inner.Io.NgCount + "，轴=" + string.Join("、", incomplete));
            try { new RotaryWeldingEquipmentDefinition().CreateStationFlows(context); throw new InvalidOperationException("启用追溯的旧样例错误接受了缺失的扫码和记录配置。 "); }
            catch (InvalidOperationException error) { if (!error.Message.Contains("缺少测试输入")) throw; }
        }

        /// <summary>逐件记录写入失败或重名时，保留缓存证据并禁止后续继续。</summary>
        private static async Task CheckRecordFailureAsync(bool duplicate)
        {
            var context = new FakeContext(); context.FailRecord = !duplicate; context.DuplicateRecord = duplicate;
            var flows = new RotaryWeldingEquipmentDefinition().CreateStationFlows(context).ToArray();
            using (var cancel = new CancellationTokenSource())
            {
                Task[] runs = flows.Select(flow => RunAsync(flow, cancel.Token)).ToArray();
                await WaitForAsync(() => runs.Any(task => task.IsFaulted), TimeSpan.FromSeconds(20));
                Task failed = runs.First(task => task.IsFaulted);
                if (!failed.Exception.ToString().Contains("逐件记录写入失败")) throw new InvalidOperationException("记录异常未报告。", failed.Exception);
                cancel.Cancel();
                try { await Task.WhenAll(runs); } catch (Exception) { }
                if (context.Evidence == null || context.Evidence.Buffer == null || !context.Evidence.Inspecting || context.Evidence.Buffer.Result == null)
                    throw new InvalidOperationException("记录失败时未保留待人工核对的缓存证据。");
                try { await flows[4].ExecuteCurrentStepAsync(CancellationToken.None); throw new InvalidOperationException("记录失败后允许继续。"); }
                catch (InvalidOperationException error) { if (!error.Message.Contains("逐件记录写入失败")) throw; }
            }
        }

        /// <summary>循环调用设备工位的单步入口。</summary>
        private static async Task RunAsync(IStationFlow flow, CancellationToken token)
        { while (true) { token.ThrowIfCancellationRequested(); await flow.ExecuteCurrentStepAsync(token); } }

        /// <summary>等待可观察的步骤或计数，超时明确失败。</summary>
        private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
        {
            DateTime end = DateTime.UtcNow + timeout;
            while (!predicate()) { if (DateTime.UtcNow >= end) throw new TimeoutException("等待十三轴流程超时。"); await Task.Delay(25); }
        }

        /// <summary>扫码设备报告失败时不夹紧载具；排除故障后先复位并重新读取。</summary>
        private static async Task CheckScannerFailureAsync()
        {
            var context = new FakeContext(); context.Io.FailScan = true;
            IStationFlow load = new RotaryWeldingEquipmentDefinition().CreateStationFlows(context)[0];
            using (var cancel = new CancellationTokenSource())
            {
                Task run = RunAsync(load, cancel.Token);
                await WaitForAsync(() => run.IsCompleted, TimeSpan.FromSeconds(10));
                if (!run.IsFaulted || !run.Exception.ToString().Contains("扫码设备报告失败")) throw new InvalidOperationException("扫码失败未正确阻止流程。");
                if (await context.Io.ReadOutputAsync("A夹紧", CancellationToken.None)) throw new InvalidOperationException("扫码失败仍夹紧了载具。");
                context.Io.FailScan = false;
                await load.ResetAsync(CancellationToken.None);
                Task retry = RunAsync(load, cancel.Token);
                await WaitForAsync(() => ((IStationCycleStatistics)load).CompletedCycles >= 1, TimeSpan.FromSeconds(10));
                cancel.Cancel();
                try { await retry; } catch (OperationCanceledException) { }
                if (context.Io.ScanAttempts < 2) throw new InvalidOperationException("扫码失败后未重新触发读码器。");
            }
        }

        /// <summary>夹具两路到位同时有效时拒绝上料轴进入夹位。</summary>
        private static async Task CheckClampConflictAsync()
        {
            var context = new FakeContext(); context.Io.ClampConflict = true;
            IStationFlow load = new RotaryWeldingEquipmentDefinition().CreateStationFlows(context)[0];
            using (var cancel = new CancellationTokenSource())
            {
                Task run = RunAsync(load, cancel.Token);
                await WaitForAsync(() => run.IsCompleted, TimeSpan.FromSeconds(10));
                if (!run.IsFaulted || !run.Exception.ToString().Contains("同时有效")) throw new InvalidOperationException("夹具反馈冲突未阻止上料。");
                if (context.Axes["Load_X"].Targets.Contains(1)) throw new InvalidOperationException("夹具反馈冲突时上料轴已进入夹位。");
            }
        }

        /// <summary>证据写入失败必须在取料运动前停止，并且后续不能继续。</summary>
        private static async Task CheckEvidenceWriteFailureAsync()
        {
            var context = new FakeContext(); context.FailEvidence = true;
            IStationFlow load = new RotaryWeldingEquipmentDefinition().CreateStationFlows(context)[0];
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { await load.ExecuteCurrentStepAsync(CancellationToken.None); throw new InvalidOperationException("证据写入失败未拦截动作。"); }
                catch (InvalidOperationException error) { if (!error.Message.Contains("证据")) throw; }
            }
            if (context.Axes.Values.Any(axis => axis.Targets.Count != 0)) throw new InvalidOperationException("证据写入失败仍发出轴动作。");
        }

        /// <summary>逐一中断已写入但尚未返回的输出，确认恢复不会再次触发且不一致时拒绝继续。</summary>
        private static async Task CheckInterruptedOutputsAsync()
        {
            foreach (var item in new[] { (Name: "扫码触发", Station: 0), (Name: "焊接启动", Station: 1), (Name: "压装启动", Station: 2), (Name: "缓存交接", Station: 3), (Name: "相机触发", Station: 4) })
                await CheckInterruptedOutputAsync(item.Name, item.Station, false);
            await CheckInterruptedOutputAsync("扫码触发", 0, true);
        }

        /// <summary>中断指定输出，读取回报并检查触发沿数量及不确定状态处理。</summary>
        private static async Task CheckInterruptedOutputAsync(string output, int station, bool mismatch)
        {
            var context = new FakeContext();
            var flows = new RotaryWeldingEquipmentDefinition().CreateStationFlows(context).ToArray();
            context.Io.HoldOutput = output;
            using (var cancel = new CancellationTokenSource())
            {
                Task[] runs = flows.Select(flow => RunAsync(flow, cancel.Token)).ToArray();
                await WaitForAsync(() => context.Io.HeldOutput || runs.Any(task => task.IsFaulted), TimeSpan.FromSeconds(20));
                Task failed = runs.FirstOrDefault(task => task.IsFaulted);
                if (failed != null) throw new InvalidOperationException("等待在途输出失败：" + output, failed.Exception);
                cancel.Cancel();
                try { await Task.WhenAll(runs); } catch (OperationCanceledException) { }
            }
            if (!await context.Io.ReadOutputAsync(output, CancellationToken.None) || context.Io.RisingCount(output) != 1)
                throw new InvalidOperationException("未捕获已置位的在途输出：" + output);
            context.Io.HoldOutput = null;
            if (mismatch) context.Io.ForceOutputOff(output);
            await flows[station].ResetAsync(CancellationToken.None);
            if (mismatch)
            {
                try { await flows[station].ExecuteCurrentStepAsync(CancellationToken.None); throw new InvalidOperationException("不一致的输出回读未阻止继续。"); }
                catch (InvalidOperationException error) { if (!error.Message.Contains("不确定")) throw; }
            }
            else await flows[station].ExecuteCurrentStepAsync(CancellationToken.None);
            if (context.Io.RisingCount(output) != 1) throw new InvalidOperationException("恢复时重复触发输出：" + output);
        }

        /// <summary>仅提供安全现场证据，不暴露扫码 IO 或逐件记录能力。</summary>
        private sealed class MotionOnlyContext : IEquipmentContext, IEquipmentEvidenceStore
        {
            /// <summary>复用测试轴和设备反馈，不提供产品记录接口。</summary>
            internal readonly FakeContext Inner = new FakeContext();
            /// <summary>设备标识。</summary>
            public string EquipmentId { get { return Inner.EquipmentId; } }
            /// <summary>保留全部测试轴映射。</summary>
            public IReadOnlyList<AxisHardwareDefinition> AxisDefinitions { get { return Inner.AxisDefinitions; } }
            /// <summary>不配置任何扫码输入。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> InputDefinitions { get { return Inner.InputDefinitions.Where(x => !x.Name.StartsWith("扫码", StringComparison.Ordinal)).ToArray(); } }
            /// <summary>不配置扫码输出。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> OutputDefinitions { get { return Inner.OutputDefinitions.Where(x => x.Name != "扫码触发").ToArray(); } }
            /// <summary>返回独立测试 IO。</summary>
            public ILogicalIo LogicalIo { get { return new NoScannerIo(Inner.LogicalIo); } }
            /// <summary>返回测试报警服务。</summary>
            public IMachineAlarmService AlarmService { get { return Inner.AlarmService; } }
            /// <summary>读取设备文件是否存在。</summary>
            public bool FileExists(string path) { return Inner.FileExists(path); }
            /// <summary>读取测试设备配置。</summary>
            public T LoadJson<T>(string path) where T : class { return Inner.LoadJson<T>(path); }
            /// <summary>按逻辑名称读取测试轴。</summary>
            public IAxis GetAxis(string name) { return Inner.GetAxis(name); }
            /// <summary>解析测试报警。</summary>
            public MachineAlarmDefinition ResolveAlarm(string code) { return Inner.ResolveAlarm(code); }
            /// <summary>保存不可自动恢复的设备现场证据。</summary>
            public void SaveEvidence<T>(string name, T document) where T : class { Inner.SaveEvidence(name, document); }
        }

        /// <summary>模拟完全没有扫码 IO 的设备，任何误访问均直接失败。</summary>
        private sealed class NoScannerIo : ILogicalIo
        {
            /// <summary>转发非扫码信号的测试设备。</summary>
            private readonly ILogicalIo _inner;
            /// <summary>包装设备逻辑 IO。</summary>
            internal NoScannerIo(ILogicalIo inner) { _inner = inner; }
            /// <summary>拒绝未配置的扫码信号。</summary>
            private static void CheckName(string name)
            {
                if (name.StartsWith("扫码", StringComparison.Ordinal)) throw new InvalidOperationException("测试设备未配置扫码 IO：" + name);
            }
            /// <summary>读取已配置的非扫码输入。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token) { CheckName(name); return _inner.ReadInputAsync(name, token); }
            /// <summary>读取已配置的非扫码输出。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token) { CheckName(name); return _inner.ReadOutputAsync(name, token); }
            /// <summary>写入已配置的非扫码输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token) { CheckName(name); return _inner.WriteOutputAsync(name, value, token); }
        }

        /// <summary>提供十三根内存轴与独立的测试 IO 定义。</summary>
        private sealed class FakeContext : IEquipmentContext, IEquipmentEvidenceStore, IEquipmentRecordStore
        {
            /// <summary>所有逻辑轴共用同一测试实例。</summary>
            internal readonly Dictionary<string, FakeAxis> Axes;
            /// <summary>软件反馈状态。</summary>
            internal readonly FakeIo Io = new FakeIo();
            /// <summary>本测试保存的证据以及写失败注入。</summary>
            internal RotaryEvidence Evidence;
            /// <summary>模拟证据存储不可写。</summary>
            internal bool FailEvidence;
            /// <summary>逐件记录及写失败和重复名称注入。</summary>
            internal readonly Dictionary<string, RotaryCompletedProduct> Records = new Dictionary<string, RotaryCompletedProduct>();
            internal bool FailRecord, DuplicateRecord;
            /// <summary>创建每根轴，旋转台初始为准备位置。</summary>
            internal FakeContext() { Axes = Names.ToDictionary(name => name, name => new FakeAxis(name, Io)); }
            /// <summary>测试轴名称。</summary>
            private static readonly string[] Names = { "Feeder_X", "Feeder_Z", "Load_X", "Transfer_Y", "Conveyor_R", "Weld_Z", "Press_Z", "Unload_X", "Buffer_X", "Buffer_Y", "Camera_X", "Camera_Z", "Reject_Z" };
            /// <summary>所有相关输入，包括 16 位扫码数据。</summary>
            private static readonly string[] Inputs = { "上料到位", "取料夹紧到位", "取料松开到位", "A夹紧到位", "A松开到位", "B夹紧到位", "B松开到位", "扫码完成", "扫码成功", "焊接完成", "焊接合格", "压装完成", "压装合格", "相机触发反馈", "检测完成", "检测OK", "检测NG", "缓存接收允许", "缓存载具到位", "合格出口允许", "合格出料完成", "不良出口允许", "剔除完成" };
            /// <summary>测试输出名称。</summary>
            private static readonly string[] Outputs = { "取料夹紧", "A夹紧", "B夹紧", "扫码触发", "焊接启动", "压装启动", "相机触发", "相机结果复位", "剔除启动", "缓存交接", "合格出料" };
            /// <summary>设备标识。</summary>
            public string EquipmentId { get { return "test"; } }
            /// <summary>测试坐标范围。</summary>
            public IReadOnlyList<AxisHardwareDefinition> AxisDefinitions { get { return Names.Select(name => new AxisHardwareDefinition { Name = name, MinimumPosition = -100, MaximumPosition = 100 }).ToArray(); } }
            /// <summary>数字输入列表。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> InputDefinitions { get { return Inputs.Concat(Enumerable.Range(0, 16).Select(i => "扫码数据" + i.ToString("D2"))).Select(name => new DigitalIoHardwareDefinition { Name = name }).ToArray(); } }
            /// <summary>数字输出列表。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> OutputDefinitions { get { return Outputs.Select(name => new DigitalIoHardwareDefinition { Name = name }).ToArray(); } }
            /// <summary>逻辑 IO。</summary>
            public ILogicalIo LogicalIo { get { return Io; } }
            /// <summary>测试报警服务。</summary>
            public IMachineAlarmService AlarmService { get; } = new MachineAlarmService();
            /// <summary>只提供测试后端配置。</summary>
            public bool FileExists(string path) { return path == "Parameters/ControllerBackend.json" || (path == "Runtime/Evidence/RotaryWelding.json" && Evidence != null); }
            /// <summary>后端始终为软件测试控制器。</summary>
            public T LoadJson<T>(string path) where T : class { return path == "Runtime/Evidence/RotaryWelding.json" ? Evidence as T : new RotaryWeldingEquipmentDefinition.TestBackend { Backend = "Virtual" } as T; }
            /// <summary>保存设备快照，或模拟失败；测试不写实际文件。</summary>
            public void SaveEvidence<T>(string evidenceName, T document) where T : class
            { if (FailEvidence) throw new InvalidOperationException("测试存储不可写"); Evidence = document as RotaryEvidence ?? throw new InvalidOperationException("无效测试证据"); }
            /// <summary>模拟独立记录的排他创建。</summary>
            public void CreateRecord<T>(string name, T document) where T : class
            { if (FailRecord || DuplicateRecord || Records.ContainsKey(name)) throw new InvalidOperationException("测试记录不可写或已存在"); Records.Add(name, document as RotaryCompletedProduct ?? throw new InvalidOperationException("无效产品记录")); }
            /// <summary>返回停机报警定义。</summary>
            public MachineAlarmDefinition ResolveAlarm(string code) { return new MachineAlarmDefinition(code, "测试超时", MachineAlarmKind.StopMachine); }
            /// <summary>返回同名轴的稳定实例。</summary>
            public IAxis GetAxis(string name) { return Axes[name]; }
        }

        /// <summary>记录目标的独立测试轴，实时返回停稳和有效参考。</summary>
        private sealed class FakeAxis : IAxis
        {
            /// <summary>轴位置和目标的同步锁。</summary>
            private readonly object _sync = new object();
            /// <summary>共享反馈控制。</summary>
            private readonly FakeIo _io;
            /// <summary>轴反馈位置。</summary>
            private double _position;
            /// <summary>曾经访问的目标位置。</summary>
            internal readonly HashSet<double> Targets = new HashSet<double>();
            /// <summary>记录准备过程中使能及回零请求次数。</summary>
            internal int EnabledCalls, HomeCalls, StopCalls; internal Action AfterHome; internal bool Enabled = true;
            /// <summary>可选位置、速度覆盖，用于模拟设备返回无效反馈。</summary>
            internal double? ReportedPosition, ReportedVelocity, VelocityAfterHome;
            /// <summary>创建指定名称的轴。</summary>
            internal FakeAxis(string name, FakeIo io) { Name = name; _io = io; _position = name == "Conveyor_R" ? -90 : 0; }
            /// <summary>逻辑轴名称。</summary>
            public string Name { get; private set; }
            /// <summary>实际到达目标后更新反馈位置。</summary>
            public async Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken token)
            { await Task.Delay(5, token); lock (_sync) { _position = position; Targets.Add(position); } }
            /// <summary>报告测试轴状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken token)
            { lock (_sync) return Task.FromResult(new AxisStatus(ReportedPosition ?? _position, HomeCalls > 0 ? (VelocityAfterHome ?? ReportedVelocity ?? 0) : (ReportedVelocity ?? 0), Enabled, false, true, false, false, false, false, false, null, false)); }
            /// <summary>测试轴无需额外使能。</summary>
            public Task EnableServoAsync(CancellationToken token) { EnabledCalls++; return Task.CompletedTask; }
            /// <summary>测试不执行断使能。</summary>
            public Task DisableServoAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>测试回零。</summary>
            public Task HomeAsync(CancellationToken token) { HomeCalls++; lock (_sync) _position = 0; AfterHome?.Invoke(); return Task.CompletedTask; }
            /// <summary>样例不使用相对运动。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>样例不使用点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>测试停止直接完成。</summary>
            public Task StopAsync(AxisStopMode mode, CancellationToken token) { StopCalls++; return Task.CompletedTask; }
            /// <summary>样例不执行轴报警复位。</summary>
            public Task ResetAlarmAsync(CancellationToken token) { throw new NotSupportedException(); }
        }

        /// <summary>只用于流程检查的逻辑反馈；扫码和检测结果由测试设备生成。</summary>
        private sealed class FakeIo : ILogicalIo
        {
            /// <summary>保护测试输出、扫码及检测结果。</summary>
            private readonly object _sync = new object();
            /// <summary>当前逻辑输出。</summary>
            private readonly Dictionary<string, bool> _outputs = new Dictionary<string, bool>();
            /// <summary>记录各输出的真实上升沿数量。</summary>
            private readonly Dictionary<string, int> _rising = new Dictionary<string, int>();
            /// <summary>指定等待取消的在途输出及其到达标志。</summary>
            internal volatile string HoldOutput;
            internal volatile bool HeldOutput;
            /// <summary>本测试设备产生的扫码值，零表示无结果。</summary>
            private int _scan;
            /// <summary>扫码完成与失败注入保持独立。</summary>
            private bool _scanDone;
            /// <summary>相机完成状态与当前结果。</summary>
            private bool _cameraDone, _cameraNg;
            /// <summary>无扫码场景独立交替产生检测结果。</summary>
            private int _cameraCount;
            /// <summary>出口堵塞时拒绝下料。</summary>
            internal volatile bool BlockExit;
            /// <summary>模拟读码失败及 A 夹具双反馈冲突。</summary>
            internal volatile bool FailScan, ClampConflict;
            /// <summary>准备测试使用的无料状态及指定夹具反馈故障。</summary>
            internal bool NoMaterial;
            internal string PreparationClamp, PreparationFault;
            /// <summary>本测试设备产生的扫码数量。</summary>
            internal int ScanCount;
            /// <summary>读码器收到的触发次数，失败也计入。</summary>
            internal int ScanAttempts;
            /// <summary>合格和不良出口动作次数。</summary>
            internal int OkCount, NgCount;
            /// <summary>同时持有焊接和压合输出的最大动作数。</summary>
            internal int MaxProcessConcurrent;

            /// <summary>读取软件模型生成的反馈和扫码数据位。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (name == "上料到位") return Task.FromResult(!NoMaterial);
                    if (name == "缓存接收允许") return Task.FromResult(true);
                    if (name == "合格出口允许" || name == "不良出口允许") return Task.FromResult(!BlockExit);
                    foreach (string prefix in new[] { "取料", "A", "B" })
                    {
                        if (prefix == PreparationClamp && (name == prefix + "夹紧到位" || name == prefix + "松开到位"))
                        {
                            if (PreparationFault == "unknown") throw new InvalidOperationException("测试反馈未知");
                            if (PreparationFault == "conflict") return Task.FromResult(true);
                            if (PreparationFault == "neither") return Task.FromResult(false);
                            if (PreparationFault == "closed") return Task.FromResult(name == prefix + "夹紧到位");
                        }
                        if (prefix == "A" && ClampConflict && (name == "A夹紧到位" || name == "A松开到位")) return Task.FromResult(true);
                        if (name == prefix + "夹紧到位") return Task.FromResult(Active(prefix + "夹紧"));
                        if (name == prefix + "松开到位") return Task.FromResult(!Active(prefix + "夹紧"));
                    }
                    if (name == "扫码完成") return Task.FromResult(_scanDone);
                    if (name == "扫码成功") return Task.FromResult(_scanDone && _scan != 0);
                    if (name.StartsWith("扫码数据", StringComparison.Ordinal))
                    {
                        int bit = int.Parse(name.Substring("扫码数据".Length));
                        return Task.FromResult((_scan & (1 << bit)) != 0);
                    }
                    if (name == "焊接完成" || name == "焊接合格") return Task.FromResult(Active("焊接启动"));
                    if (name == "压装完成" || name == "压装合格") return Task.FromResult(Active("压装启动"));
                    if (name == "相机触发反馈") return Task.FromResult(Active("相机触发"));
                    if (name == "检测完成") return Task.FromResult(_cameraDone);
                    if (name == "检测OK") return Task.FromResult(_cameraDone && !_cameraNg);
                    if (name == "检测NG") return Task.FromResult(_cameraDone && _cameraNg);
                    if (name == "缓存载具到位") return Task.FromResult(Active("缓存交接"));
                    if (name == "合格出料完成") return Task.FromResult(Active("合格出料"));
                    if (name == "剔除完成") return Task.FromResult(Active("剔除启动"));
                    throw new InvalidOperationException("未知测试输入：" + name);
                }
            }

            /// <summary>返回模型当前保存的输出电平。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token)
            { token.ThrowIfCancellationRequested(); lock (_sync) return Task.FromResult(Active(name)); }

            /// <summary>设置输出并按模型生成扫码、相机和出料反馈。</summary>
            public async Task WriteOutputAsync(string name, bool value, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    bool previous = Active(name);
                    _outputs[name] = value;
                    if (value && !previous) { int count; _rising.TryGetValue(name, out count); _rising[name] = count + 1; if (name == HoldOutput) HeldOutput = true; }
                    if (name == "扫码触发" && value && !previous) { ScanAttempts++; _scanDone = true; _scan = FailScan ? 0 : ++ScanCount; }
                    if (name == "扫码触发" && !value) { _scan = 0; _scanDone = false; }
                    if (name == "相机触发" && value && !previous) { _cameraDone = true; _cameraNg = ScanCount == 0 ? ++_cameraCount % 2 == 0 : ScanCount % 2 == 0; }
                    if (name == "相机结果复位" && value) _cameraDone = false;
                    if (name == "合格出料" && value && !previous) OkCount++;
                    if (name == "剔除启动" && value && !previous) NgCount++;
                    int concurrent = (Active("焊接启动") ? 1 : 0) + (Active("压装启动") ? 1 : 0);
                    if (concurrent > MaxProcessConcurrent) MaxProcessConcurrent = concurrent;
                }
                if (value && name == HoldOutput) await Task.Delay(Timeout.Infinite, token);
                else if (value && (name == "焊接启动" || name == "压装启动")) await Task.Delay(200, token);
            }

            /// <summary>获取指定输出上升沿次数。</summary>
            internal int RisingCount(string name) { lock (_sync) { int count; return _rising.TryGetValue(name, out count) ? count : 0; } }

            /// <summary>注入外部状态改变，模拟命令返回后输出回读不一致。</summary>
            internal void ForceOutputOff(string name) { lock (_sync) _outputs[name] = false; }

            /// <summary>同步区域内读取不存在时默认为关闭的测试输出。</summary>
            private bool Active(string name) { bool value; return _outputs.TryGetValue(name, out value) && value; }
        }
    }
}
