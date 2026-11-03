using System.Collections.Generic;

namespace MotionControl.Framework.Contracts.Clearance
{
    /// <summary>设备项目可选提供的固定清料配置；Host 连接前装配，不由操作员编辑。</summary>
    public interface IEquipmentClearanceProvider
    {
        /// <summary>返回当前设备明确声明的清料配置；设备未配置时应返回空集合。</summary>
        IEnumerable<ControlledClearanceProfile> CreateClearanceProfiles();
    }
}
