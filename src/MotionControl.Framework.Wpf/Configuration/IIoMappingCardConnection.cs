using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Configuration;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>供支持在线更新映射的控制卡连接替换当前逻辑 DI/DO 名称映射。</summary>
    public interface IIoMappingCardConnection
    {
        /// <summary>确认已映射轴全部停止后，在当前连接内替换 DI/DO 映射，不重建控制卡对象；失败时保留原映射。</summary>
        Task ApplyIoMappingsAsync(
            IReadOnlyList<DigitalIoHardwareDefinition> digitalInputs,
            IReadOnlyList<DigitalIoHardwareDefinition> digitalOutputs,
            CancellationToken cancellationToken);
    }
}
