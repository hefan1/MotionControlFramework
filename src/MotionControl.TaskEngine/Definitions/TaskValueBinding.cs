using MotionControl.Framework.Contracts.Variables;
namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>动作参数的数据来源；本阶段只校验引用，不读取当前运行值。</summary>
    public sealed class TaskValueBinding
    {
        /// <summary>常量、设备变量或某根逻辑轴上的示教点。</summary>
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public TaskValueSource Source { get; set; }
        /// <summary>常量的数据类型；变量和示教点的类型由共享目录确定，禁止重复指定。</summary>
        public DeviceVariableType? DataType { get; set; }
        /// <summary>常量文本，数字使用小数点；不随界面语言改变。</summary>
        public string Constant { get; set; }
        /// <summary>引用的现有设备变量名称。</summary>
        public string VariableName { get; set; }
        /// <summary>示教点所属逻辑轴名称。</summary>
        public string AxisName { get; set; }
        /// <summary>示教点的稳定 ID；使用现有文件中的编号，不按显示名称匹配。</summary>
        public string PointId { get; set; }
    }

    /// <summary>本阶段支持的数据来源。</summary>
    public enum TaskValueSource
    {
        /// <summary>直接保存一个带类型的常量。</summary>
        Constant,
        /// <summary>引用现有设备变量。</summary>
        Variable,
        /// <summary>引用现有示教位置。</summary>
        TeachPoint
    }
}
