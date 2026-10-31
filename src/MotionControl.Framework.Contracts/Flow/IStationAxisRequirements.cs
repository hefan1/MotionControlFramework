using System.Collections.Generic;

namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>声明自动工位启动前必须完成回零的逻辑轴。</summary>
    public interface IStationAxisRequirements
    {
        /// <summary>此工位定位前必须已建立坐标原点的逻辑轴名称。</summary>
        IReadOnlyCollection<string> RequiredHomedAxes { get; }
    }
}
