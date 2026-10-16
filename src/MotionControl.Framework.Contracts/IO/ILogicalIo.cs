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
        Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken);

        Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken);

        Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken);
    }
}
