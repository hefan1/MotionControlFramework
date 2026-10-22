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

        /// <summary>保存原示教点信息，不修改文件或设备位置。</summary>
        public TaskTeachPoint(string axisName, string pointId, string name, double? position)
        {
            AxisName = axisName;
            PointId = pointId;
            Name = name;
            Position = position;
        }
    }
}
