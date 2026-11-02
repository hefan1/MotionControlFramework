using System.Collections.Generic;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;

namespace MotionControl.Framework.Contracts.Equipment
{
    /// <summary>设备定义可选提供的工位准备入口；通用 Host 只装配，不了解设备工艺类型。</summary>
    public interface IEquipmentPreparationProvider
    {
        /// <summary>使用已创建的生产工位及其共享现场状态建立准备流程，不在创建时发送动作。</summary>
        IReadOnlyList<IStationPreparation> CreatePreparations(IEquipmentContext context,
            IReadOnlyList<IStationFlow> stationFlows);
    }
}
