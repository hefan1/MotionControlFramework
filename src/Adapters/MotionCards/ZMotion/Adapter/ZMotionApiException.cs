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

        /// <summary>发生调用错误的控制卡标识，便于区分多卡设备中的故障来源。</summary>
        public string ControllerId { get; private set; }

        /// <summary>失败的控制器操作名称。</summary>
        public string Operation { get; private set; }

        /// <summary>错误上下文的类别，例如逻辑轴或数字输入。</summary>
        public string ContextType { get; private set; }

        /// <summary>发生错误的逻辑对象名称；没有具体对象时为空。</summary>
        public string ContextName { get; private set; }

        /// <summary>正运动 SDK 返回的原始错误码。</summary>
        public int ErrorCode { get; private set; }

        /// <summary>保留原始错误码；厂商明确说明句柄错误时补充面向维护人员的诊断。</summary>
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
            string message = "正运动控制卡 '" + controllerId + "'" + contextText
                + "执行 '" + operation + "' 失败，SDK 错误码：" + errorCode + "。";
            if (errorCode == 20009)
                message += "厂商定义为控制器句柄错误；请检查控制器会话及设备实际状态。";
            return message;
        }
    }
}
