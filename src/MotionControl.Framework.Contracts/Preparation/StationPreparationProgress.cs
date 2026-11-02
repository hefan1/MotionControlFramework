namespace MotionControl.Framework.Contracts.Preparation
{
    /// <summary>一次准备步骤的操作员说明；不包含框架假定的设备动作。</summary>
    public sealed class StationPreparationProgress
    {
        /// <summary>创建当前动作与等待原因，名称应直接表达现场动作。</summary>
        public StationPreparationProgress(string action, string detail = null)
        { Action = action ?? string.Empty; Detail = detail ?? string.Empty; }
        /// <summary>当前正在执行的动作。</summary>
        public string Action { get; }
        /// <summary>当前等待条件或补充处理说明。</summary>
        public string Detail { get; }
    }
}
