namespace MotionControl.TaskEngine.Catalog
{
    /// <summary>从轴手动页原有 JSON 读取的示教点只读记录。</summary>
    public sealed class TaskTeachPoint
    {
        /// <summary>该点所属逻辑轴。</summary>
        public string AxisName { get; private set; }
        /// <summary>原文件中的稳定位置编号。</summary>
        public string PointId { get; private set; }
        /// <summary>现场显示名称。</summary>
        public string Name { get; private set; }
        /// <summary>现有示教值；为空表示尚未示教，不能用于定位。</summary>
        public double? Position { get; private set; }

        /// <summary>配置是否显式声明逻辑轴归属；旧物理组只能按现有映射兼容，不能据此证明迁移正确。</summary>
        public bool OwnershipConfirmed { get; private set; }

        /// <summary>读取时的物理组键，只用于归属诊断，不取代逻辑轴与点位的复合引用。</summary>
        public string PhysicalAxisKey { get; private set; }

        /// <summary>保存原示教点信息，不修改文件或设备位置。</summary>
        public TaskTeachPoint(string axisName, string pointId, string name, double? position,
            bool ownershipConfirmed = true, string physicalAxisKey = null)
        {
            AxisName = axisName;
            PointId = pointId;
            Name = name;
            Position = position;
            OwnershipConfirmed = ownershipConfirmed;
            PhysicalAxisKey = physicalAxisKey;
        }
    }
}
