namespace MotionControl.Framework.Contracts.Safety
{
    /// <summary>为可选配置安全输入的状态来源提供配置状态；未实现此接口的状态来源视为始终可用。</summary>
    public interface IConfigurableMachineSafetyStatus : IMachineSafetyStatus
    {
        /// <summary>当前是否已配置可读取的安全输入名称。</summary>
        bool IsConfigured { get; }
    }
}
