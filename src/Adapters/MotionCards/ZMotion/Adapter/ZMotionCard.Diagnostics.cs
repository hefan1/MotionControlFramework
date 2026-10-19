namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>正运动适配器中负责检查厂商错误码并保留操作上下文的实现部分。</summary>
    public sealed partial class ZMotionCard
    {
        /// <summary>检查轴操作的厂商错误码，并保留控制卡和轴名称。</summary>
        private void CheckResult(int result, string operation, string axisName)
        {
            if (result != 0)
            {
                throw new ZMotionApiException(ControllerId, operation, axisName, result);
            }
        }

        /// <summary>检查通用操作的厂商错误码，并保留操作上下文。</summary>
        private void CheckResult(int result, string operation, string contextType, string contextName)
        {
            if (result != 0)
            {
                throw new ZMotionApiException(ControllerId, operation, contextType, contextName, result);
            }
        }

        /// <summary>检查 IO 操作的厂商错误码，并注明逻辑点位名称。</summary>
        private void CheckIoResult(int result, string operation, string pointName)
        {
            CheckResult(result, operation, "逻辑 IO", pointName);
        }
    }
}
