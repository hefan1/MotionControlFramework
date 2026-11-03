using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;

namespace MotionControl.Equipment.Demo.WeldingExample
{
    /// <summary>夹具焊接测试线的代码流程定义；轴和 IO 均为测试映射，不代表参考设备接线。</summary>
    public sealed class WeldingExampleEquipmentDefinition : IEquipmentDefinition, IEquipmentPreparationProvider
    {
        /// <summary>正式 Host 配置中选择的设备定义名。</summary>
        public string EquipmentName { get { return "WeldingExample"; } }

        /// <summary>新设备目录需要的默认停机报警；已有报警库仍由设备配置提供。</summary>
        public IReadOnlyList<MachineAlarmDefinition> CreateDefaultAlarms()
        {
            return new[] { new MachineAlarmDefinition(DemoStationFlow.SensorTimeoutAlarmCode,
                "等待传感器超时", MachineAlarmKind.StopMachine,
                "检查当前工位、输入和输出状态。", "确认设备安全后复位并从当前步骤继续。") };
        }

        /// <summary>核对五轴行程与逻辑 IO 后创建四个并行工位及其共享载具线。</summary>
        public IReadOnlyList<IStationFlow> CreateStationFlows(IEquipmentContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!context.FileExists("Parameters/ControllerBackend.json"))
                throw new InvalidOperationException("焊接测试线仅可使用软件控制器，缺少控制器后端配置。");
            WeldingTestBackend backend = context.LoadJson<WeldingTestBackend>("Parameters/ControllerBackend.json");
            if (backend == null || !string.Equals(backend.Backend, "Virtual", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("焊接测试线仅用于软件控制器，不允许连接实际设备执行。");
            string[] names = { "上料工位", "焊接工位", "压合工位", "复检下料工位" };
            string[] axes = { "Load_X", "Weld_Z", "Press_Z", "Camera_X", "Unload_X" };
            string[] inputs = { "上料到位", "焊接完成", "压装完成", "相机触发反馈", "夹具夹紧到位", "夹具松开到位", "检测完成", "检测OK", "检测NG", "下料到位" };
            string[] outputs = { "夹具夹紧", "焊接启动", "压头动作", "相机触发", "相机结果复位", "下料电磁阀" };
            var line = new WeldingCarrierLine();
            var flows = new List<IStationFlow>();
            MachineAlarmDefinition alarm = context.ResolveAlarm(DemoStationFlow.SensorTimeoutAlarmCode);
            if (alarm.Kind != MachineAlarmKind.StopMachine)
                throw new InvalidOperationException("焊接测试线的传感器超时报警必须为停机报警。");
            foreach (string axisName in axes)
            {
                AxisHardwareDefinition axis = context.AxisDefinitions.SingleOrDefault(item =>
                    string.Equals(item.Name, axisName, StringComparison.OrdinalIgnoreCase));
                if (axis == null || axis.MinimumPosition > 0 || axis.MaximumPosition < 1)
                    throw new InvalidOperationException("焊接测试线轴未映射或目标 0–1 超出行程：" + axisName);
            }
            foreach (string input in inputs)
                if (!context.InputDefinitions.Any(item => string.Equals(item.Name, input, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("焊接测试线缺少逻辑输入：" + input);
            foreach (string output in outputs)
                if (!context.OutputDefinitions.Any(item => string.Equals(item.Name, output, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("焊接测试线缺少逻辑输出：" + output);
            for (int index = 0; index < names.Length; index++)
            {
                flows.Add(new WeldingStationFlow(index, names[index], context.GetAxis(axes[index]), context.LogicalIo,
                    inputs[index], outputs[index], line, context.AlarmService, alarm,
                    index == 3 ? context.GetAxis("Unload_X") : null));
            }
            return flows.AsReadOnly();
        }

        /// <summary>提供本设备的焊接和压合回零，通用框架只读取工位名、依赖及执行结果。</summary>
        public IReadOnlyList<IStationPreparation> CreatePreparations(IEquipmentContext context, IReadOnlyList<IStationFlow> stationFlows)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (stationFlows == null) throw new ArgumentNullException(nameof(stationFlows));
            WeldingTestBackend backend = context.LoadJson<WeldingTestBackend>("Parameters/ControllerBackend.json");
            if (backend == null || !string.Equals(backend.Backend, "Virtual", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("焊接测试准备仅允许软件控制器，不允许连接实际设备执行。");
            WeldingStationFlow[] flows = stationFlows.OfType<WeldingStationFlow>().ToArray();
            if (flows.Length != 4 || flows.Select(flow => flow.StationIndex).Distinct().Count() != 4
                || flows.Any(flow => !ReferenceEquals(flow.CarrierLine, flows[0].CarrierLine)))
                throw new InvalidOperationException("焊接准备必须绑定当前四个工位的同一载具状态。");
            var welding = new WeldingStationPreparation("ClipWeld", "焊接工位", context.GetAxis("Weld_Z"),
                context.LogicalIo, "焊接完成", "焊接启动", flows[0].CarrierLine, null);
            var pressing = new WeldingStationPreparation("ClipPress", "压合工位", context.GetAxis("Press_Z"),
                context.LogicalIo, "压装完成", "压头动作", flows[0].CarrierLine, welding);
            return new IStationPreparation[] { welding, pressing };
        }

        /// <summary>只读取已有的设备后端名称，用于防止测试工艺误接实际控制器。</summary>
        public sealed class WeldingTestBackend
        {
            /// <summary>设备选择的控制器后端；只有 Virtual 允许本示例运行。</summary>
            public string Backend { get; set; }
        }
    }
}
