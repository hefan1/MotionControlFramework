using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Clearance;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    /// <summary>十三轴样例中 A 夹位单处有料时的固定清料方案；坐标仅适用于隔离测试布局。</summary>
    public static class RotarySingleAClearance
    {
        /// <summary>示例方案的稳定标识，不允许操作员临时改写动作或条件。</summary>
        public const string ProfileId = "RotarySingleA";
        /// <summary>测试动作使用的线性轴速度、加速度及减速度，单位为毫米和秒。</summary>
        private static readonly AxisMotionProfile Profile = new AxisMotionProfile(2, 10, 10);
        /// <summary>各步超时包含动作前后反馈检查时间。</summary>
        private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
        /// <summary>双夹位设备的全部逻辑轴；未移动轴也作为避让条件核查。</summary>
        private static readonly string[] AxisNames = { "Unload_X", "Load_X", "Press_Z", "Transfer_Y", "Camera_X", "Camera_Z", "Feeder_X", "Feeder_Z", "Buffer_X", "Buffer_Y", "Reject_Z", "Conveyor_R", "Weld_Z" };

        /// <summary>生成固定方案：A 夹位交接到下料、缓存，再按不良出口剔除。</summary>
        public static ControlledClearanceProfile CreateProfile()
        {
            var actions = new List<ControlledClearanceAction>();
            // 六处物料必须明确：只有 A 有料；夹具状态和两个区域许可均按本测试布局核查。
            var initial = Inputs(true, false, false, true, false, false, false);
            var receiveClosed = Inputs(true, false, false, true, true, false, false);
            var shared = Inputs(true, false, true, true, true, false, false);
            var inUnload = Inputs(false, false, true, false, true, false, false);
            var bufferReady = Inputs(false, false, true, false, true, true, false);
            var inBuffer = Inputs(false, true, false, false, false, true, false);
            var bufferHeld = Inputs(false, true, false, false, false, false, false);
            var discharged = Inputs(false, false, false, false, false, false, true);
            var empty = Inputs(false, false, false, false, false, false, false);
            var atTable = Axes(0, 0, 0, 0);
            var atPick = Axes(1, 0, 0, 0);

            actions.Add(Output("下料夹具夹紧到位", "下料夹紧", true, initial, atTable,
                InputsOnly(receiveClosed, "下料夹紧到位", "下料松开到位"), "下料夹紧到位", "下料松开到位"));
            actions.Add(Move("下料轴进入接料位", "Unload_X", 1, receiveClosed, atTable,
                Without(receiveClosed, "下料位有料", "下料位无料")));
            actions.Add(Output("确认承接后松开 A 夹具", "A夹紧", false, shared, atPick,
                InputsOnly(inUnload, "A夹紧到位", "A松开到位", "A夹位有料", "A夹位无料"),
                "A夹紧到位", "A松开到位", "A夹位有料", "A夹位无料"));
            actions.Add(Move("下料轴退出旋转区", "Unload_X", 0, inUnload, atPick, inUnload));
            actions.Add(Output("请求缓存接收", "缓存交接", true, inUnload, atTable,
                InputsOnly(bufferReady, "缓存载具到位"), "缓存载具到位"));
            actions.Add(Output("确认缓存接收后松开下料夹具", "下料夹紧", false, bufferReady, atTable,
                InputsOnly(inBuffer, "下料夹紧到位", "下料松开到位", "下料位有料", "下料位无料", "缓存位有料", "缓存位无料"),
                "下料夹紧到位", "下料松开到位", "下料位有料", "下料位无料", "缓存位有料", "缓存位无料"));
            actions.Add(Output("结束缓存交接", "缓存交接", false, inBuffer, atTable,
                InputsOnly(bufferHeld, "缓存载具到位"), "缓存载具到位"));
            actions.Add(Move("缓存 X 轴送至不良位", "Buffer_X", 2, bufferHeld, atTable, bufferHeld));
            var xAtReject = Axes(0, 2, 0, 0);
            actions.Add(Move("缓存 Y 轴送至不良位", "Buffer_Y", 2, bufferHeld, xAtReject, bufferHeld));
            var xyAtReject = Axes(0, 2, 2, 0);
            actions.Add(Move("剔除轴到不良位", "Reject_Z", 1, bufferHeld, xyAtReject, bufferHeld));
            var atReject = Axes(0, 2, 2, 1);
            actions.Add(Output("剔除并确认缓存无料", "剔除启动", true, bufferHeld, atReject,
                InputsOnly(discharged, "剔除完成", "缓存位有料", "缓存位无料"), "剔除完成", "缓存位有料", "缓存位无料"));
            actions.Add(Output("复位剔除请求", "剔除启动", false, discharged, atReject,
                InputsOnly(empty, "剔除完成"), "剔除完成"));
            actions.Add(Move("剔除轴退出", "Reject_Z", 0, empty, atReject, empty));
            actions.Add(Move("缓存 Y 轴返回", "Buffer_Y", 0, empty, xyAtReject, empty));
            actions.Add(Move("缓存 X 轴返回", "Buffer_X", 0, empty, xAtReject, empty));

            var areas = new[] { "Zone:RotaryWeld", "Zone:RotaryPress", "Zone:Buffer" };
            var plan = new ControlledClearancePlan(actions, areas);
            var outputs = ExpectedOutputs(empty).Select(item => new ClearanceRecoveryOutput(item.Name, item.Expected)).ToArray();
            var recovery = new ControlledClearanceRecovery(empty, atTable, outputs);
            var limits = AxisNames.Select(name => name == "Conveyor_R"
                ? new ClearanceAxisLimit(name, 89.98, 90.02, 2, 10, 10)
                : new ClearanceAxisLimit(name, -0.02, 2.02, 2, 10, 10)).ToArray();
            var feedback = new[] {
                new ClearanceSharedFeedback("Zone:RotaryWeld", InputsOnly(empty, "旋转区清料允许")),
                new ClearanceSharedFeedback("Zone:RotaryPress", InputsOnly(empty, "旋转区清料允许")),
                new ClearanceSharedFeedback("Zone:Buffer", InputsOnly(empty, "缓存区清料允许")) };
            return new ControlledClearanceProfile(ProfileId, plan, recovery, limits, feedback);
        }

        /// <summary>创建一次固定轴定位，运动轴以外的全部轴持续保持避让位置。</summary>
        private static ClearanceAbsoluteMove Move(string name, string axis, double target, ClearanceInputCondition[] inputs,
            ClearanceAxisCondition[] axes, ClearanceInputCondition[] maintain)
        {
            return new ClearanceAbsoluteMove(name, axis, target, 0.01, Profile, inputs, axes, StepTimeout,
                maintain, axes.Where(item => item.Name != axis), ExpectedOutputs(inputs));
        }

        /// <summary>输出动作要求独立完成输入；变化的反馈不作为动作期间固定条件。</summary>
        private static ClearanceSetOutput Output(string name, string output, bool value, ClearanceInputCondition[] inputs,
            ClearanceAxisCondition[] axes, ClearanceInputCondition[] completion, params string[] changing)
        {
            var conflicts = new List<ClearanceInputConflict>();
            foreach (var pair in new[] { new[] { "A夹紧到位", "A松开到位" }, new[] { "下料夹紧到位", "下料松开到位" },
                new[] { "A夹位有料", "A夹位无料" }, new[] { "下料位有料", "下料位无料" }, new[] { "缓存位有料", "缓存位无料" } })
                if (completion.Any(item => item.Name == pair[0]) && completion.Any(item => item.Name == pair[1]))
                    conflicts.Add(new ClearanceInputConflict(pair[0], pair[1]));
            bool beforeValue = ExpectedOutputs(inputs).Single(item => string.Equals(item.Name, output, StringComparison.OrdinalIgnoreCase)).Expected;
            return new ClearanceSetOutput(name, output, beforeValue, value, inputs, axes, completion, StepTimeout,
                conflicts, Without(inputs, changing), axes,
                ExpectedOutputs(inputs).Where(item => !string.Equals(item.Name, output, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>按当前阶段生成六处物料、夹具及独立区域许可的互斥反馈。</summary>
        private static ClearanceInputCondition[] Inputs(bool a, bool buffer, bool unload, bool aClamped, bool unloadClamped, bool bufferReady, bool rejectDone)
        {
            var values = new List<ClearanceInputCondition>();
            Material(values, "取料夹爪", false); Material(values, "上料滑台", false);
            Material(values, "A夹位", a); Material(values, "B夹位", false);
            Material(values, "缓存位", buffer); Material(values, "下料位", unload);
            Pair(values, "A夹紧到位", "A松开到位", aClamped);
            Pair(values, "B夹紧到位", "B松开到位", true);
            Pair(values, "取料夹紧到位", "取料松开到位", false);
            Pair(values, "下料夹紧到位", "下料松开到位", unloadClamped);
            values.Add(new ClearanceInputCondition("上料到位", false));
            values.Add(new ClearanceInputCondition("旋转区清料允许", true));
            values.Add(new ClearanceInputCondition("缓存区清料允许", true));
            values.Add(new ClearanceInputCondition("不良出口允许", true));
            foreach (string name in new[] { "焊接模组空闲", "压合模组空闲", "上料模组空闲", "视觉模组空闲", "出料模组空闲" })
                values.Add(new ClearanceInputCondition(name, true));
            values.Add(new ClearanceInputCondition("缓存载具到位", bufferReady));
            values.Add(new ClearanceInputCondition("剔除完成", rejectDone));
            return values.ToArray();
        }

        /// <summary>一处物料只允许有料与无料两路之一有效。</summary>
        private static void Material(List<ClearanceInputCondition> values, string name, bool present)
        { Pair(values, name + "有料", name + "无料", present); }
        /// <summary>添加互斥的两路独立反馈。</summary>
        private static void Pair(List<ClearanceInputCondition> values, string first, string second, bool firstActive)
        { values.Add(new ClearanceInputCondition(first, firstActive)); values.Add(new ClearanceInputCondition(second, !firstActive)); }
        /// <summary>选择指定反馈作为独立动作完成条件。</summary>
        private static ClearanceInputCondition[] InputsOnly(IEnumerable<ClearanceInputCondition> inputs, params string[] names)
        { return inputs.Where(item => names.Contains(item.Name, StringComparer.OrdinalIgnoreCase)).ToArray(); }
        /// <summary>移除允许在当前步骤内变化的输入，其他条件持续监控。</summary>
        private static ClearanceInputCondition[] Without(IEnumerable<ClearanceInputCondition> inputs, params string[] names)
        { return inputs.Where(item => !names.Contains(item.Name, StringComparer.OrdinalIgnoreCase)).ToArray(); }

        /// <summary>逐步核查不参与动作的输出电平；保持输出与夹具反馈分别判定。</summary>
        private static ClearanceOutputCondition[] ExpectedOutputs(IEnumerable<ClearanceInputCondition> inputs)
        {
            var values = inputs.ToDictionary(item => item.Name, item => item.Expected, StringComparer.OrdinalIgnoreCase);
            return new[] {
                new ClearanceOutputCondition("A夹紧", values["A夹紧到位"]),
                new ClearanceOutputCondition("B夹紧", true),
                new ClearanceOutputCondition("下料夹紧", values["下料夹紧到位"]),
                new ClearanceOutputCondition("取料夹紧", false),
                new ClearanceOutputCondition("缓存交接", values["缓存载具到位"]),
                new ClearanceOutputCondition("剔除启动", values["剔除完成"]),
                new ClearanceOutputCondition("焊接启动", false),
                new ClearanceOutputCondition("压装启动", false),
                new ClearanceOutputCondition("相机触发", false),
                new ClearanceOutputCondition("相机结果复位", false),
                new ClearanceOutputCondition("合格出料", false) };
        }

        /// <summary>固定十三轴的测试位置；转台保持 +90°，其他轴按阶段指定。</summary>
        private static ClearanceAxisCondition[] Axes(double unload, double bufferX, double bufferY, double reject)
        {
            return AxisNames.Select(name => {
                double position = name == "Conveyor_R" ? 90 : name == "Unload_X" ? unload
                    : name == "Buffer_X" ? bufferX : name == "Buffer_Y" ? bufferY : name == "Reject_Z" ? reject : 0;
                return new ClearanceAxisCondition(name, position - 0.01, position + 0.01);
            }).ToArray();
        }
    }
}
