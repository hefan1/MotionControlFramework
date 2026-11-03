namespace MotionControl.VirtualController
{
    /// <summary>仅影响下一次指定控制命令的一条测试故障。</summary>
    internal sealed class CommandFault
    {
        /// <summary>执行前拒绝、执行后返回失败、执行前延迟或应答延迟的模式。</summary>
        public string Mode { get; set; }

        /// <summary>延迟应答的时间，单位毫秒；不会阻塞其他控制器请求。</summary>
        public int DelayMilliseconds { get; set; }
    }
}
