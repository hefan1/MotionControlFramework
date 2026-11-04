using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Preparation
{
    /// <summary>设备项目或任务列表提供的单工位准备动作；框架不解释具体轴、气缸或工艺。</summary>
    public interface IStationPreparation
    {
        /// <summary>稳定工位标识，用于依赖关系和选择单工位准备。</summary>
        string StationId { get; }
        /// <summary>面向操作员显示的工位名称。</summary>
        string StationName { get; }
        /// <summary>本工位准备前必须准备完成并保持规定姿态的工位标识。</summary>
        IReadOnlyCollection<string> Dependencies { get; }
        /// <summary>只读检查本工位动作影响范围内的占料、保留工步、输出及互锁等准备前提；不满足时抛出具体原因，禁止清空生产数据。依赖工位的前提在其前序准备完成后检查，不得依赖它们提前检查来保护本工位动作。</summary>
        Task ValidateStartAsync(CancellationToken cancellationToken);
        /// <summary>从设备定义的起始检查执行准备；完成每步后报告进度，取消时受控停止，不自动启动生产。</summary>
        Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken cancellationToken);
        /// <summary>只读核对当前轴、IO和机构姿态确实满足初始位置；未知或不满足时必须报错。</summary>
        Task ValidateReadyAsync(CancellationToken cancellationToken);
    }
}
