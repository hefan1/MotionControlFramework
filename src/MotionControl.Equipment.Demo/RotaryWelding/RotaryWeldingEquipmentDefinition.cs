using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    /// <summary>十三轴双夹位设备的代码样例；仅允许本机软件控制器。</summary>
    public sealed class RotaryWeldingEquipmentDefinition : IEquipmentDefinition, IEquipmentPreparationProvider
    {
        /// <summary>是否按原焊接样例要求扫码和逐件记录；运动验证示例不依赖这两项业务能力。</summary>
        private readonly bool _traceProducts;
        /// <summary>创建原有扫码和逐件记录样例，保持已有设备配置行为。</summary>
        public RotaryWeldingEquipmentDefinition() : this(true) { }
        /// <summary>显式选择是否执行产品追溯，不能通过运行时故障自动切换。</summary>
        public RotaryWeldingEquipmentDefinition(bool traceProducts) { _traceProducts = traceProducts; }
        /// <summary>Host 显式选择本设备示例的配置名称。</summary>
        public string EquipmentName { get { return _traceProducts ? "RotaryWelding" : "RotaryMotionValidation"; } }

        /// <summary>新设备目录所需的停机报警，既有设备仍采用报警库配置。</summary>
        public IReadOnlyList<MachineAlarmDefinition> CreateDefaultAlarms()
        {
            return new[] { new MachineAlarmDefinition("DEMO_SENSOR_TIMEOUT", "等待传感器超时", MachineAlarmKind.StopMachine,
                "检查工位和输入状态。", "排除故障并确认设备状态后复位。") };
        }

        /// <summary>检查后端、十三轴行程及独立 IO，创建共享一条载具线的工位。</summary>
        public IReadOnlyList<IStationFlow> CreateStationFlows(IEquipmentContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!context.FileExists("Parameters/ControllerBackend.json")) throw new InvalidOperationException("缺少控制器后端配置。");
            TestBackend backend = context.LoadJson<TestBackend>("Parameters/ControllerBackend.json");
            if (backend == null || !string.Equals(backend.Backend, "Virtual", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("双夹位测试设备仅可使用软件控制器。");
            var axes = new Dictionary<string, IAxis>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in AxisNames)
            {
                var axis = context.AxisDefinitions.SingleOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                double min = name == "Conveyor_R" ? -90 : 0;
                double max = name == "Conveyor_R" ? 90 : (name == "Buffer_X" || name == "Buffer_Y" ? 2 : 1);
                if (axis == null || axis.MinimumPosition > min || axis.MaximumPosition < max)
                    throw new InvalidOperationException("测试轴未映射或目标超出行程：" + name);
                axes.Add(name, context.GetAxis(name));
            }
            foreach (string input in InputNames)
            {
                if (!_traceProducts && (input == "扫码完成" || input == "扫码成功")) continue;
                if (!context.InputDefinitions.Any(item => string.Equals(item.Name, input, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("缺少测试输入：" + input);
            }
            for (int bit = 0; _traceProducts && bit < 16; bit++)
            {
                string name = "扫码数据" + bit.ToString("D2");
                if (!context.InputDefinitions.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("缺少扫码数据输入：" + name);
            }
            foreach (string output in OutputNames)
            {
                if (!_traceProducts && output == "扫码触发") continue;
                if (!context.OutputDefinitions.Any(item => string.Equals(item.Name, output, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("缺少测试输出：" + output);
            }
            MachineAlarmDefinition alarm = context.ResolveAlarm("DEMO_SENSOR_TIMEOUT");
            if (alarm.Kind != MachineAlarmKind.StopMachine) throw new InvalidOperationException("等待反馈报警必须是停机报警。");
            var evidenceStore = context as IEquipmentEvidenceStore;
            if (evidenceStore == null) throw new InvalidOperationException("双夹位流程需要设备现场证据存储；未配置时禁止启动。");
            var recordStore = context as IEquipmentRecordStore;
            if (_traceProducts && recordStore == null) throw new InvalidOperationException("双夹位流程需要逐件记录存储；未配置时禁止启动。");
            bool hasEvidence = context.FileExists("Runtime/Evidence/RotaryWelding.json");
            RotaryEvidence previous = hasEvidence ? context.LoadJson<RotaryEvidence>("Runtime/Evidence/RotaryWelding.json") : null;
            if (hasEvidence && previous == null) throw new InvalidOperationException("双夹位现场证据为空，禁止运行，请核对证据文件。");
            if (previous != null && (previous.SchemaVersion != 1 || string.IsNullOrWhiteSpace(previous.SessionId)))
                throw new InvalidOperationException("双夹位现场证据不完整或版本无效，禁止运行，请核对证据文件。");
            var line = new RotaryCarrierLine(evidenceStore, recordStore, previous, _traceProducts);
            var result = new List<IStationFlow>();
            foreach (string role in new[] { "上料工位", "焊接工位", "压合工位", "旋转与下料", "复检分拣" })
                result.Add(new RotaryStationFlow(role, axes, context.LogicalIo, line, context.AlarmService, alarm));
            return result.AsReadOnly();
        }

        /// <summary>所有测试轴使用统一的明确准备顺序；运行时只识别准备接口。</summary>
        public IReadOnlyList<IStationPreparation> CreatePreparations(IEquipmentContext context, IReadOnlyList<IStationFlow> stationFlows)
        {
            if (context == null || stationFlows == null) throw new ArgumentNullException(context == null ? nameof(context) : nameof(stationFlows));
            RotaryStationFlow[] flows = stationFlows.OfType<RotaryStationFlow>().ToArray();
            if (flows.Length != 5 || flows.Any(item => !ReferenceEquals(item.Line, flows[0].Line)))
                throw new InvalidOperationException("双夹位准备需要绑定当前五个工位。");
            return new IStationPreparation[] { new RotaryPreparation(context, flows[0].Line) };
        }

        /// <summary>十三根实际参与动作的逻辑轴。</summary>
        internal static readonly string[] AxisNames = { "Feeder_X", "Feeder_Z", "Load_X", "Transfer_Y", "Conveyor_R", "Weld_Z", "Press_Z", "Unload_X", "Buffer_X", "Buffer_Y", "Camera_X", "Camera_Z", "Reject_Z" };
        /// <summary>模型提供的各工艺输入；扫码数据位按编号单独核验。</summary>
        internal static readonly string[] InputNames = { "上料到位", "取料夹紧到位", "取料松开到位", "A夹紧到位", "A松开到位", "B夹紧到位", "B松开到位", "扫码完成", "扫码成功", "焊接完成", "焊接合格", "压装完成", "压装合格", "相机触发反馈", "检测完成", "检测OK", "检测NG", "缓存接收允许", "缓存载具到位", "合格出口允许", "合格出料完成", "不良出口允许", "剔除完成" };
        /// <summary>模型接收的设备动作输出。</summary>
        internal static readonly string[] OutputNames = { "取料夹紧", "A夹紧", "B夹紧", "扫码触发", "焊接启动", "压装启动", "相机触发", "相机结果复位", "剔除启动", "缓存交接", "合格出料" };

        /// <summary>读取后端配置以拒绝测试流程接管实体卡。</summary>
        public sealed class TestBackend
        {
            /// <summary>设备配置的控制器后端，只有 Virtual 可运行本示例。</summary>
            public string Backend { get; set; }
        }
    }
}
