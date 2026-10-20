using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>
    /// 为多个并行工位提供按逻辑轴名称申请和释放轴资源的能力。
    /// 需要多轴时应一次申请完整轴组；持有占用凭证期间不要再嵌套申请其他轴资源，避免流程互相等待。
    /// </summary>
    public interface IAxisResourceCoordinator
    {
        /// <summary>等待并占用一根逻辑轴；返回的占用凭证释放后，其他流程才能申请该轴。</summary>
        Task<IDisposable> AcquireAxisAsync(string logicalAxisName, CancellationToken cancellationToken);

        /// <summary>
        /// 等待并同时占用一组逻辑轴；方法按固定名称顺序申请，并在等待取消时释放已申请的轴。
        /// 返回的占用凭证必须在相关动作停稳后释放。
        /// </summary>
        Task<IDisposable> AcquireAxisGroupAsync(
            IEnumerable<string> logicalAxisNames,
            CancellationToken cancellationToken);
    }
}
