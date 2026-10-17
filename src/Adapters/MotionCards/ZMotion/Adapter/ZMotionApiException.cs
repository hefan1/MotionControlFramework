using System;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>包含控制卡、逻辑轴、操作名称和正运动 SDK 错误码的调用异常。</summary>
    public sealed class ZMotionApiException : InvalidOperationException
    {
        /// <summary>创建包含逻辑轴上下文的正运动 SDK 异常。</summary>
        internal ZMotionApiException(
            string controllerId,
            string operation,
            string axisName,
            int errorCode)
            : this(controllerId, operation, string.IsNullOrWhiteSpace(axisName) ? null : "逻辑轴", axisName, errorCode)
        {
        }

        /// <summary>创建包含自定义上下文的正运动 SDK 异常。</summary>
        internal ZMotionApiException(
            string controllerId,
            string operation,
            string contextType,
            string contextName,
            int errorCode)
            : base(CreateMessage(controllerId, operation, contextType, contextName, errorCode))
        {
            ControllerId = controllerId;
            Operation = operation;
            ContextType = contextType;
            ContextName = contextName;
            ErrorCode = errorCode;
        }

        public string ControllerId { get; private set; }

        public string Operation { get; private set; }

        public string ContextType { get; private set; }

        public string ContextName { get; private set; }

        public int ErrorCode { get; private set; }

        /// <summary>生成包含控制卡、操作、上下文和厂商错误码的中文说明。</summary>
        private static string CreateMessage(
            string controllerId,
            string operation,
            string contextType,
            string contextName,
            int errorCode)
        {
            var contextText = string.IsNullOrWhiteSpace(contextName)
                ? string.Empty
                : "，" + contextType + " '" + contextName + "'";
            return "正运动控制卡 '" + controllerId + "'" + contextText
                + "执行 '" + operation + "' 失败，SDK 错误码：" + errorCode + "。";
        }
    }
}
