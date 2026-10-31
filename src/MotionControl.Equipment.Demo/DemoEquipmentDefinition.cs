using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;

namespace MotionControl.Equipment.Demo
{
    /// <summary>
    /// 示例设备定义：按设备目录中的工位配置生成示例工位流程。工位引用的逻辑轴、DI、DO
    /// 必须在设备硬件映射中存在，目标位置必须落在轴软限位内，否则报告错误而不创建工位。
    /// </summary>
    public sealed class DemoEquipmentDefinition : IEquipmentDefinition
    {
        /// <summary>并行工位清单配置的相对路径。</summary>
        public const string StationSetPath = "Parameters/DemoStations.json";

        /// <summary>旧版单工位配置的相对路径；没有工位清单时继续兼容。</summary>
        public const string LegacyStationPath = "Parameters/DemoStation.json";

        /// <summary>设备定义名称，设备配置中用此名称选择本定义。</summary>
        public string EquipmentName
        {
            get { return "Demo"; }
        }

        /// <summary>
        /// 示例设备首次生成报警库时预置的传感器超时报警。只有本设备会得到这条定义，
        /// 其他设备按自己的工艺提供各自的默认报警，框架不内置任何设备的报警代码。
        /// </summary>
        public IReadOnlyList<MachineAlarmDefinition> CreateDefaultAlarms()
        {
            return new[]
            {
                new MachineAlarmDefinition(
                    DemoStationFlow.SensorTimeoutAlarmCode,
                    "等待传感器超时",
                    MachineAlarmKind.StopMachine,
                    "检查传感器安装、工件到位状态和逻辑 DI 映射，再核对当前输入状态。",
                    "条件恢复后，确认机构无碰撞风险并执行整机复位；流程从等待步骤重新开始。")
            };
        }

        /// <summary>
        /// 读取工位配置并创建工位流程。存在工位清单时按清单创建（清单关闭则不创建任何工位）；
        /// 没有清单时回退到旧版单工位配置。
        /// </summary>
        public IReadOnlyList<IStationFlow> CreateStationFlows(IEquipmentContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.FileExists(StationSetPath))
            {
                DemoStationSetSettings stationSet = context.LoadJson<DemoStationSetSettings>(StationSetPath);
                if (stationSet == null || !stationSet.Enabled)
                {
                    return new IStationFlow[0];
                }

                stationSet.Validate();
                return stationSet.Stations
                    .Where(station => station != null && station.Enabled)
                    .Select(station => CreateStationFlow(context, station))
                    .ToArray();
            }

            if (!context.FileExists(LegacyStationPath))
            {
                return new IStationFlow[0];
            }

            DemoStationSettings settings = context.LoadJson<DemoStationSettings>(LegacyStationPath);
            if (settings == null || !settings.Enabled)
            {
                return new IStationFlow[0];
            }

            settings.Validate();
            return new IStationFlow[] { CreateStationFlow(context, settings) };
        }

        /// <summary>核对单个工位引用的逻辑硬件和行程后创建工位流程。</summary>
        private static IStationFlow CreateStationFlow(IEquipmentContext context, DemoStationSettings settings)
        {
            AxisHardwareDefinition axisDefinition = context.AxisDefinitions.FirstOrDefault(
                axis => string.Equals(axis.Name, settings.AxisName, StringComparison.OrdinalIgnoreCase));
            if (axisDefinition == null)
            {
                throw new InvalidOperationException("示例工位找不到已映射逻辑轴：" + settings.AxisName + "。");
            }

            if (settings.TargetPosition < axisDefinition.MinimumPosition
                || settings.TargetPosition > axisDefinition.MaximumPosition)
            {
                throw new InvalidOperationException(
                    "示例工位目标位置 " + settings.TargetPosition + " 超出逻辑轴 '"
                    + settings.AxisName + "' 的配置行程 [" + axisDefinition.MinimumPosition
                    + ", " + axisDefinition.MaximumPosition + "]。");
            }

            if (!context.InputDefinitions.Any(
                point => string.Equals(point.Name, settings.SensorInputName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("示例工位找不到已映射逻辑 DI：" + settings.SensorInputName + "。");
            }

            if (!context.OutputDefinitions.Any(
                point => string.Equals(point.Name, settings.OutputName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("示例工位找不到已映射逻辑 DO：" + settings.OutputName + "。");
            }

            IReadOnlyList<MachineAlarmDefinition> timeoutAlarms =
                LoadTimeoutAlarms(context, settings.TimeoutAlarmCodes);
            return new DemoStationFlow(
                context.GetAxis(settings.AxisName),
                context.LogicalIo,
                context.AlarmService,
                settings,
                timeoutAlarms);
        }

        /// <summary>按工位显式配置的代码，从设备报警库读取一条或多条超时报警。</summary>
        private static IReadOnlyList<MachineAlarmDefinition> LoadTimeoutAlarms(
            IEquipmentContext context,
            IEnumerable<string> alarmCodes)
        {
            if (alarmCodes == null)
            {
                throw new ArgumentNullException(nameof(alarmCodes));
            }

            var definitions = new List<MachineAlarmDefinition>();
            foreach (string code in alarmCodes)
            {
                definitions.Add(context.ResolveAlarm(code));
            }

            return definitions.AsReadOnly();
        }
    }
}
