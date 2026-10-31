using System.Collections.Generic;
using MotionControl.Framework.Contracts.Flow;

namespace MotionControl.Framework.Contracts.Equipment
{
    /// <summary>
    /// 一台设备的定义。设备工程实现本接口，提供本设备的工位流程；Host 按配置显式选择设备定义，
    /// 换设备时新增一个设备工程并登记，不修改 Host 的工位创建代码。
    /// </summary>
    public interface IEquipmentDefinition
    {
        /// <summary>设备定义名称，用于设备配置中显式选择，例如 "Demo"。</summary>
        string EquipmentName { get; }

        /// <summary>
        /// 按当前设备配置和已连接的控制卡创建工位流程。没有启用任何工位时返回空列表，
        /// 表示该设备只能手动调试、不能启动自动运行。
        /// </summary>
        IReadOnlyList<IStationFlow> CreateStationFlows(IEquipmentContext context);
    }
}
