using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.IO
{
    /// <summary>
    /// 按设备级逻辑名称读取和写入 DI/DO。设备硬件配置负责将逻辑名称映射到运动卡
    /// 或远程 IO 点。名称不存在或映射无效时必须报告错误，不能当作 false 返回。
    /// </summary>
    public interface ILogicalIo
    {
        /// <summary>按逻辑名称读取 DI；true 表示逻辑输入有效，未配置的名称必须报告错误。</summary>
        Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken);

        /// <summary>按逻辑名称读取控制卡当前报告的 DO 状态；未配置的名称必须报告错误。</summary>
        Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken);

        /// <summary>按逻辑名称设置 DO；true 表示逻辑输出开启，具体极性由硬件映射和适配器处理。</summary>
        Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken);
    }
}
