using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;

namespace MotionControl.Equipment.Demo.WeldingExample
{
    /// <summary>夹具焊接测试线的代码流程定义；四轴、四组 IO 均为测试映射，不代表参考设备接线。</summary>
    public sealed class WeldingExampleEquipmentDefinition : IEquipmentDefinition
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

        /// <summary>核对四轴行程与逻辑 IO 后创建四个并行工位及其共享载具线。</summary>
        public IReadOnlyList<IStationFlow> CreateStationFlows(IEquipmentContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!context.FileExists("Parameters/ControllerBackend.json"))
                throw new InvalidOperationException("焊接测试线仅可使用软件控制器，缺少控制器后端配置。");
            WeldingTestBackend backend = context.LoadJson<WeldingTestBackend>("Parameters/ControllerBackend.json");
            if (backend == null || !string.Equals(backend.Backend, "Virtual", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("焊接测试线仅用于软件控制器，不允许连接实际设备执行。");
            string[] names = { "扫码工位", "焊接工位", "压合工位", "复检工位" };
            string[] axes = { "Load_X", "Press_Z", "Camera_X", "Unload_X" };
            string[] inputs = { "上料到位", "压装完成", "检测完成", "下料到位" };
            string[] outputs = { "夹具夹紧", "压头动作", "相机触发", "下料电磁阀" };
            var line = new WeldingCarrierLine();
            var flows = new List<IStationFlow>();
            MachineAlarmDefinition alarm = context.ResolveAlarm(DemoStationFlow.SensorTimeoutAlarmCode);
            if (alarm.Kind != MachineAlarmKind.StopMachine)
                throw new InvalidOperationException("焊接测试线的传感器超时报警必须为停机报警。");
            for (int index = 0; index < names.Length; index++)
            {
                AxisHardwareDefinition axis = context.AxisDefinitions.SingleOrDefault(item =>
                    string.Equals(item.Name, axes[index], StringComparison.OrdinalIgnoreCase));
                if (axis == null || axis.MinimumPosition > 0 || axis.MaximumPosition < 1)
                    throw new InvalidOperationException("焊接测试线轴未映射或目标 0–1 超出行程：" + axes[index]);
                if (!context.InputDefinitions.Any(item => string.Equals(item.Name, inputs[index], StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("焊接测试线缺少逻辑输入：" + inputs[index]);
                if (!context.OutputDefinitions.Any(item => string.Equals(item.Name, outputs[index], StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("焊接测试线缺少逻辑输出：" + outputs[index]);
                flows.Add(new WeldingStationFlow(index, names[index], context.GetAxis(axes[index]), context.LogicalIo,
                    inputs[index], outputs[index], line, context.AlarmService, alarm));
            }
            return flows.AsReadOnly();
        }

        /// <summary>只读取已有的设备后端名称，用于防止测试工艺误接实际控制器。</summary>
        public sealed class WeldingTestBackend
        {
            /// <summary>设备选择的控制器后端；只有 Virtual 允许本示例运行。</summary>
            public string Backend { get; set; }
        }
    }
}
