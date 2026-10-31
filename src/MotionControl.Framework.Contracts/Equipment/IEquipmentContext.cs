using System.Collections.Generic;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Contracts.Equipment
{
    /// <summary>
    /// 设备定义创建工位流程时可读取的设备环境。提供设备配置、硬件映射、逻辑轴、逻辑 IO 和报警服务，
    /// 设备定义本身不直接访问配置文件或控制卡对象。
    /// </summary>
    public interface IEquipmentContext
    {
        /// <summary>当前设备标识，用于区分同一台电脑上的设备配置目录。</summary>
        string EquipmentId { get; }

        /// <summary>判断设备配置目录下是否存在指定相对路径的文件；路径使用 / 分隔。</summary>
        bool FileExists(string relativePath);

        /// <summary>从设备配置目录读取指定相对路径的 JSON 配置；文件缺失或格式错误时报告错误。</summary>
        T LoadJson<T>(string relativePath) where T : class;

        /// <summary>本设备已映射的逻辑轴硬件定义，包含名称和软限位范围。</summary>
        IReadOnlyList<AxisHardwareDefinition> AxisDefinitions { get; }

        /// <summary>本设备已映射的逻辑 DI 定义，用于核对工位引用的输入名称。</summary>
        IReadOnlyList<DigitalIoHardwareDefinition> InputDefinitions { get; }

        /// <summary>本设备已映射的逻辑 DO 定义，用于核对工位引用的输出名称。</summary>
        IReadOnlyList<DigitalIoHardwareDefinition> OutputDefinitions { get; }

        /// <summary>按报警代码从设备报警库取出一条定义；代码不存在时必须报告错误。</summary>
        MachineAlarmDefinition ResolveAlarm(string alarmCode);

        /// <summary>按逻辑轴名称取得该轴的运行入口；名称未映射时必须报告错误。</summary>
        IAxis GetAxis(string axisName);

        /// <summary>按逻辑名称读写 DI/DO 的设备级入口。</summary>
        ILogicalIo LogicalIo { get; }

        /// <summary>本设备用于上报和复位报警的服务。</summary>
        IMachineAlarmService AlarmService { get; }
    }
}
