using System;
using System.Collections.Generic;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>轴卡片中关联 IO 点的方向类型。</summary>
    public enum ManualAxisIoPointType
    {
        /// <summary>只读的逻辑数字输入。</summary>
        DigitalInput,

        /// <summary>可手动写入的逻辑数字输出。</summary>
        DigitalOutput
    }

    /// <summary>保存单台设备全部轴卡片的关联 IO 配置。</summary>
    public sealed class ManualAxisIoAssociationConfigurationDocument
    {
        /// <summary>当前关联 IO 配置结构版本。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>按控制卡和物理轴号分组的关联 IO 配置。</summary>
        public List<ManualAxisIoAssociationGroup> Axes { get; set; }

        /// <summary>创建初始为空的关联 IO 配置。</summary>
        public ManualAxisIoAssociationConfigurationDocument()
        {
            SchemaVersion = 1;
            Axes = new List<ManualAxisIoAssociationGroup>();
        }
    }

    /// <summary>保存一个物理轴所关联的逻辑 IO 点。</summary>
    public sealed class ManualAxisIoAssociationGroup
    {
        /// <summary>控制卡编号和物理轴号组成的稳定轴标识。</summary>
        public string AxisKey { get; set; }

        /// <summary>当前轴卡片配置的逻辑 IO 点。</summary>
        public List<ManualAxisIoAssociation> Points { get; set; }

        /// <summary>创建一个没有 IO 关联的轴分组。</summary>
        public ManualAxisIoAssociationGroup()
        {
            Points = new List<ManualAxisIoAssociation>();
        }
    }

    /// <summary>保存单个轴卡片的逻辑 IO 名称和现场显示文字。</summary>
    public sealed class ManualAxisIoAssociation
    {
        /// <summary>当前关联项的稳定标识。</summary>
        public string Id { get; set; }

        /// <summary>关联的逻辑点方向。</summary>
        public ManualAxisIoPointType PointType { get; set; }

        /// <summary>IO 映射页中的逻辑名称。</summary>
        public string PointName { get; set; }

        /// <summary>轴卡片上显示的现场名称。</summary>
        public string DisplayName { get; set; }

        /// <summary>逻辑 DO 置为 true 时显示的按钮名称。</summary>
        public string OnActionName { get; set; }

        /// <summary>逻辑 DO 置为 false 时显示的按钮名称。</summary>
        public string OffActionName { get; set; }
    }
}
