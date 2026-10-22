using System.Collections.Generic;
namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>明确的比较或组合条件；不允许嵌入任意 C# 脚本。</summary>
    public sealed class TaskCondition
    {
        /// <summary>比较、全部成立、任一成立或取反。</summary>
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public TaskConditionKind Kind { get; set; }
        /// <summary>比较条件使用的左值。</summary>
        public TaskValueBinding Left { get; set; }
        /// <summary>比较条件使用的操作符；组合条件必须留空。</summary>
        public TaskComparison? Comparison { get; set; }
        /// <summary>比较条件使用的右值。</summary>
        public TaskValueBinding Right { get; set; }
        /// <summary>组合条件的子项；Not 只能有一个子项，Compare 必须为空列表。</summary>
        public List<TaskCondition> Conditions { get; set; } = new List<TaskCondition>();
    }

    /// <summary>有限且容易定位的条件组合方式。</summary>
    public enum TaskConditionKind
    {
        /// <summary>比较两个值。</summary>
        Compare,
        /// <summary>所有子条件都成立。</summary>
        All,
        /// <summary>至少一个子条件成立。</summary>
        Any,
        /// <summary>对唯一子条件取反。</summary>
        Not
    }

    /// <summary>条件支持的比较操作。</summary>
    public enum TaskComparison
    {
        /// <summary>两个值相等。</summary>
        Equal,
        /// <summary>两个值不相等。</summary>
        NotEqual,
        /// <summary>左值小于右值。</summary>
        Less,
        /// <summary>左值小于或等于右值。</summary>
        LessOrEqual,
        /// <summary>左值大于右值。</summary>
        Greater,
        /// <summary>左值大于或等于右值。</summary>
        GreaterOrEqual
    }
}
