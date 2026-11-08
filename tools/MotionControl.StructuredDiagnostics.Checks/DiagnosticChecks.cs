using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Host.Wpf.Operation;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.StructuredDiagnostics.Checks
{
    /// <summary>直接检查不可变证据和只读投影，防止语言文案重新承担运行语义。</summary>
    internal static class DiagnosticChecks
    {
        /// <summary>同一个等待改成中文、英文或误导性原文，分类和对象仍由结构字段决定。</summary>
        internal static void TextDoesNotChooseKind()
        {
            foreach (string text in new[] { "等待轴到位：X=0", "Waiting for resources; fake=timeout", "活动时限999999ms" })
            {
                TaskNodeSnapshot node = Fixtures.Node("n", null, Fixtures.Diagnostic(text, Fixtures.Input(Fixtures.FirstInput)), text);
                Fixtures.Require(TaskOperatorText.WaitingStateKey(node) == "等待输入", "说明文字改变了等待类别。");
                Fixtures.Require(TaskOperatorText.Detail(node, Fixtures.Text).Contains(Fixtures.FirstInput), "显示丢失了结构化输入对象。");
                Fixtures.Require(!node.Diagnostics.IsTimeout && !node.Diagnostics.TimeoutMilliseconds.HasValue,
                    "普通说明被当成数值时限或已发生超时。");
            }
        }

        /// <summary>缺少诊断时保留原文，不能凭“等待输入”或“活动时限”猜测状态。</summary>
        internal static void MissingDiagnosticStaysMissing()
        {
            const string raw = "等待输入：原文;Input=Ready；活动时限123ms";
            TaskNodeSnapshot node = Fixtures.Node("old", null, null, raw);
            Fixtures.Require(TaskOperatorText.WaitingStateKey(node) == "等待类别未提供", "缺失类别被原文推断。");
            string detail = TaskOperatorText.Detail(node, Fixtures.Text);
            Fixtures.Require(detail.Contains("等待诊断未提供") && detail.Contains(raw), "未提供提示或原始说明丢失。");
            var legacyDiagnostic = new TaskStepDiagnostics(raw);
            Fixtures.Require(!legacyDiagnostic.HasWaitInformation && legacyDiagnostic.WaitItems.Count == 0,
                "旧诊断被误认为明确提供了空等待集合。");
        }

        /// <summary>名称中的分号等号原样显示；多输入、多资源或集合不完整时不任意启用唯一输入导航。</summary>
        internal static void PunctuationAndMultipleWaits()
        {
            StationWaitItem first = Fixtures.Input(Fixtures.FirstInput);
            StationWaitItem second = Fixtures.Input(Fixtures.SecondInput);
            var resource = new StationWaitItem(StationWaitKind.Resource, "resource.acquire_group", StationObjectKind.Resource,
                "区域;Camera=1", "区域;Camera=1", "已取得", null, StationValueQuality.NotProvided);
            var waits = new[] { first, second, resource };
            string rendered = StationOperatorPresentation.DescribeWaitItems(waits, true, Fixtures.Text);
            Fixtures.Require(waits.All(item => rendered.Contains(item.ObjectId)), "多项等待没有全部显示，或对象名称被拆分。");
            Fixtures.Require(OperationSnapshot.FindSingleWaitingInput(waits, true) == null,
                "多对象等待错误地选择了唯一输入。");
            Fixtures.Require(OperationSnapshot.FindSingleWaitingInput(new[] { first }, false) == null,
                "不完整集合错误地启用了唯一输入导航。");
            Fixtures.Require(OperationSnapshot.FindSingleWaitingInput(new[] { first }, true) == Fixtures.FirstInput,
                "单一明确输入的原始标识未被完整保留。");
        }

        /// <summary>未知、读取失败和未提供不会被显示为 false 或零；旧快照不跟随来源列表改变。</summary>
        internal static void QualityAndImmutableSnapshot()
        {
            var source = new List<StationWaitItem> { Fixtures.Input(Fixtures.FirstInput) };
            var status = new StationOperationStatus("S", "工位", "step", "工步", StationOperationState.Waiting, source);
            source.Add(Fixtures.Input(Fixtures.SecondInput));
            Fixtures.Require(status.WaitItems.Count == 1, "外部列表改变了已经发布的工位快照。");
            StationWaitItem unknown = Fixtures.Input("U", "False", StationValueQuality.Unknown);
            StationWaitItem failed = Fixtures.Input("F", "False", StationValueQuality.ReadFailed);
            Fixtures.Require(StationOperatorPresentation.DescribeValue(unknown, unknown.Actual, true, Fixtures.Text) == "未知",
                "未知输入被显示成有效布尔值。");
            Fixtures.Require(StationOperatorPresentation.DescribeValue(failed, failed.Actual, true, Fixtures.Text) == "未知",
                "读取失败被显示成有效布尔值。");
            Fixtures.Require(StationOperatorPresentation.DescribeValue(source[0], null, true, Fixtures.Text) == "未提供",
                "未采样被伪装为已知输入。");
            StationWaitItem noQuality = Fixtures.Input("N", "False", StationValueQuality.NotProvided);
            Fixtures.Require(StationOperatorPresentation.DescribeValue(noQuality, noQuality.Actual, true, Fixtures.Text) == "未提供",
                "没有采样质量的残留文本被伪装成有效实际值。");
            var axis = new StationWaitItem(StationWaitKind.Axis, "axis.position_settled", StationObjectKind.Axis,
                "R", "R", "90", "45", StationValueQuality.Good, unit: "deg");
            Fixtures.Require(StationOperatorPresentation.DescribeValue(axis, axis.Actual, true, Fixtures.Text) == "45 deg",
                "轴的来源单位丢失或被固定成毫米。");
        }

        /// <summary>英文失败说明不影响超时类别、实际父级时限、原始原因或采样时间。</summary>
        internal static void NumericTimeoutAndReason()
        {
            TaskStepDiagnostics original = Fixtures.Diagnostic("中文可以改写", Fixtures.Input(Fixtures.FirstInput));
            var error = new TaskExecutionException("task", "parent", "parent:1", "Deadline reached; actual=unknown",
                timeoutMilliseconds: 375, failureKind: TaskExecutionFailureKind.Timeout);
            TaskStepDiagnostics failed = original.WithTiming(9000, "child").WithFailure(error);
            Fixtures.Require(failed.IsTimeout && failed.TimeoutMilliseconds == 375 && failed.TimeoutNodeId == "parent",
                "显示使用了当前节点配置时限，而不是实际触发的父级时限。");
            Fixtures.Require(failed.ReasonCode == "execution.timeout" && failed.FailureMessage == "Deadline reached; actual=unknown",
                "失败原因代码或原始英文说明没有保留。");
            Fixtures.Require(failed.CapturedAtUtc == original.CapturedAtUtc && failed.WaitItems[0].ObjectId == Fixtures.FirstInput,
                "故障覆盖了最后一次采样对象或伪造了采样时间。");
            var ordinary = new TaskExecutionException("task", "n", "n:1", "等待超时，活动时限 1ms");
            Fixtures.Require(!original.WithFailure(ordinary).IsTimeout, "普通异常被中文说明误判成超时。");
        }

        /// <summary>C# 工位只显示显式提供的状态；没有契约或契约异常时保留未提供。</summary>
        internal static void OptionalCSharpStatus()
        {
            var legacy = new LegacyFlow();
            StationOverviewData absent = OperationSnapshot.ReadStation(legacy);
            Fixtures.Require(absent.IndependentState == StationOperationState.NotProvided && !absent.HasWaitInformation,
                "旧 C# 流程的工步文字被猜测为运行状态。");
            legacy.WaitingInputName = Fixtures.FirstInput;
            StationOverviewData compatible = OperationSnapshot.ReadStation(legacy);
            Fixtures.Require(compatible.IndependentState == StationOperationState.NotProvided
                && compatible.WaitItems.Count == 1 && compatible.WaitItems[0].Actual == null
                && compatible.WaitItems[0].Quality == StationValueQuality.NotProvided,
                "旧输入导航被伪装成已采样的工位状态。");
            var flow = new DescribedFlow { Status = new StationOperationStatus("S2", "显式工位", "step", "等待夹具",
                StationOperationState.Waiting, new[] { Fixtures.Input(Fixtures.FirstInput), Fixtures.Input(Fixtures.SecondInput) },
                description: "raw;state=waiting", nextAction: "确认夹具反馈", completionCondition: "两个输入均有效") };
            StationOverviewData supplied = OperationSnapshot.ReadStation(flow);
            Fixtures.Require(supplied.IndependentState == StationOperationState.Waiting && supplied.WaitItems.Count == 2
                && supplied.WaitingInputName == null && supplied.NextAction == "确认夹具反馈", "C# 显式状态没有完整投影。");
            flow.ThrowOnRead = true;
            StationOverviewData error = OperationSnapshot.ReadStation(flow);
            Fixtures.Require(error.StatusReadError != null && error.IndependentState == StationOperationState.NotProvided
                && !error.HasWaitInformation && error.WaitItems.Count == 0, "状态读取失败保留了看似正常的旧状态。");
        }

        /// <summary>并行工位只列活动末端；缺失一支时保留其他支，交接等待不混入上次周期。</summary>
        internal static void ParallelStationProjection()
        {
            var root = Fixtures.Node("root", null, Fixtures.Diagnostic("父级等待", Fixtures.Input("不应重复的父项")));
            var first = Fixtures.Node("first", "root", Fixtures.Diagnostic("第一支", Fixtures.Input(Fixtures.FirstInput)));
            var second = Fixtures.Node("second", "root", Fixtures.Diagnostic("第二支", Fixtures.Input(Fixtures.SecondInput)));
            StationOperationStatus complete = TaskStationOperationProjection.Read(Fixtures.Station(StationTaskState.Executing,
                new[] { root, first, second }));
            Fixtures.Require(complete.WaitItems.Count == 2 && complete.HasWaitInformation && complete.StepId == null,
                "并行投影重复父节点，或把多个活动步骤选成唯一工步。");
            var missing = Fixtures.Node("missing", "root", null, "custom wait text");
            StationOperationStatus partial = TaskStationOperationProjection.Read(Fixtures.Station(StationTaskState.Executing,
                new[] { root, first, missing }));
            Fixtures.Require(!partial.HasWaitInformation && partial.WaitItems.Count == 1,
                "缺失一支时伪装成完整集合，或丢弃了已知支的证据。");
            string partialText = StationOperatorPresentation.DescribeWaitItems(partial.WaitItems, partial.HasWaitInformation, Fixtures.Text);
            Fixtures.Require(partialText.Contains(Fixtures.FirstInput) && partialText.Contains("部分等待诊断未提供"), "部分诊断提示不完整。");
            var branchItems = new[] {
                new StationWaitItem(StationWaitKind.Parallel, "parallel.children", StationObjectKind.Step,
                    "first", "第一支", null, null, StationValueQuality.NotProvided),
                new StationWaitItem(StationWaitKind.Parallel, "parallel.children", StationObjectKind.Step,
                    "second", "第二支", null, null, StationValueQuality.NotProvided)
            };
            var registeringParent = Fixtures.Node("root", null, new TaskStepDiagnostics("并行分支登记中",
                waitKind: StationWaitKind.Parallel, reasonCode: "parallel.children", waitItems: branchItems));
            StationOperationStatus registering = TaskStationOperationProjection.Read(Fixtures.Station(StationTaskState.Executing,
                new[] { registeringParent, first }));
            Fixtures.Require(!registering.HasWaitInformation && registering.StepId == null
                && registering.WaitItems.Count == 2 && registering.WaitItems.Any(item => item.ObjectId == "second"),
                "尚未登记的并行分支从投影消失，或第一支被误认为完整工位状态。");
            var finishedSecond = Fixtures.Node("second", "root", null, state: TaskNodeState.Completed);
            StationOperationStatus oneRemaining = TaskStationOperationProjection.Read(Fixtures.Station(StationTaskState.Executing,
                new[] { registeringParent, first, finishedSecond }));
            Fixtures.Require(oneRemaining.HasWaitInformation && oneRemaining.WaitItems.Count == 1
                && oneRemaining.WaitItems[0].ObjectId == Fixtures.FirstInput,
                "已经登记并完成的分支被重新显示为未登记等待。");
            StationOperationStatus handshake = TaskStationOperationProjection.Read(Fixtures.Station(StationTaskState.WaitingRequest,
                new[] { first, second }, Fixtures.Diagnostic("新周期交接", Fixtures.Input("本周期交接输入"))));
            Fixtures.Require(handshake.WaitItems.Count == 1 && handshake.WaitItems[0].ObjectId == "本周期交接输入",
                "交接等待混入了最近完成周期的旧节点。");
        }

        /// <summary>组合条件中尚未求值的变量仍有引用；常量左侧不能遮蔽右侧未知变量。</summary>
        internal static void UnreadConditionReferences()
        {
            var device = new InMemoryTaskDevice(Fixtures.Catalog());
            var all = new TaskCondition { Kind = TaskConditionKind.All, Conditions = new List<TaskCondition> {
                Fixtures.Compare(Fixtures.Variable("A;未知=1"), Fixtures.True()),
                Fixtures.Compare(Fixtures.Variable("B=未读取;2"), Fixtures.True())
            } };
            bool decision;
            string reason;
            var observations = new List<TaskObservedValue>();
            bool known = TaskConditionEvaluator.TryEvaluate(all, device.Catalog, device.ReadVariables(), out decision, out reason, observations);
            Fixtures.Require(!known && observations.Any(value => value.ObjectId == "A;未知=1" && value.Quality == StationValueQuality.Unknown)
                && observations.Any(value => value.ObjectId == "B=未读取;2" && value.Quality == StationValueQuality.NotProvided),
                "组合条件的未知或尚未读取变量被遗漏，或后续变量被伪装成已采样。");
            observations.Clear();
            known = TaskConditionEvaluator.TryEvaluate(Fixtures.Compare(Fixtures.True(), Fixtures.Variable("B=未读取;2")),
                device.Catalog, device.ReadVariables(), out decision, out reason, observations);
            Fixtures.Require(!known && observations.Any(value => value.ObjectId == "B=未读取;2"
                && value.ObjectKind == StationObjectKind.Variable && value.Quality == StationValueQuality.Unknown),
                "常量左侧遮蔽了右侧未知变量的稳定对象引用。");
            IReadOnlyList<TaskObservedValue> interlockValues = TaskNodeExecutor.DescribeInterlockWaits(
                new TaskInterlock { Condition = all },
                Fixtures.Compare(Fixtures.True(), Fixtures.Variable("B=未读取;2")), false);
            Fixtures.Require(interlockValues.Any(value => value.ObjectId == "A;未知=1")
                && interlockValues.Any(value => value.ObjectId == "B=未读取;2")
                && interlockValues.All(value => value.Actual == null && value.Quality == StationValueQuality.NotProvided),
                "互锁条件没有列出完整变量引用，或把设备的整组结论伪装成逐项采样。");
        }
    }
}
