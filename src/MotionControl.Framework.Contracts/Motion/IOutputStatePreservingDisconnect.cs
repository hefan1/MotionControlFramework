namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>控制器适配器可选声明：断开本软件会话后，控制器维持当前数字输出电平。</summary>
    /// <remarks>此能力只说明通信断开行为，不证明机械、气路或电源失效时安全。</remarks>
    public interface IOutputStatePreservingDisconnect
    {
        /// <summary>已验证断开会话不会主动改变控制器数字输出；未知或未验证应返回 false。</summary>
        bool PreservesDigitalOutputsOnDisconnect { get; }
    }
}
