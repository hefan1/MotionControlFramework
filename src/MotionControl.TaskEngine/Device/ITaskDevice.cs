using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Device
{
    /// <summary>
    /// 任务执行器驱动的设备端口。执行器只通过本接口读写轴、IO、变量和资源，
    /// 不关心背后是内存替身还是真实运动控制卡。实现方负责把框架语义翻译成具体设备动作，
    /// 并在不支持或失败时明确报错，不得返回假成功。
    /// </summary>
    public interface ITaskDevice
    {
        /// <summary>本设备的只读资源目录；启动时按它校验任务定义。</summary>
        TaskResourceCatalog Catalog { get; }

        /// <summary>本设备共用的轴与输出资源协调器。</summary>
        TaskResourceCoordinator Resources { get; }

        /// <summary>读取逻辑 DI；null 表示当前没有有效数据，名称未配置必须报错。</summary>
        bool? ReadInput(string name);

        /// <summary>读取逻辑 DO 当前状态；null 表示当前没有有效数据。</summary>
        bool? ReadOutput(string name);

        /// <summary>写入逻辑 DO；写入失败必须报错，不能静默忽略。</summary>
        void WriteOutput(string name, bool value);

        /// <summary>写入一个设备变量；类型不匹配或变量未配置必须报错。</summary>
        void WriteVariable(string name, object value);

        /// <summary>复制全部设备变量当前值，供一次条件判断使用一致快照。</summary>
        IReadOnlyList<DeviceVariableSnapshot> ReadVariables();

        /// <summary>读取一根逻辑轴的状态快照；轴未配置必须报错。</summary>
        TaskAxisState ReadAxis(string name);

        /// <summary>检查一个互锁条件是否满足；不满足时通过 reason 给出可定位原因。</summary>
        bool CheckInterlock(TaskInterlock interlock, TaskCondition legacyCondition, out string reason);

        /// <summary>读取当前资源占用与待确认区域。</summary>
        IReadOnlyList<TaskResourceSnapshot> ReadResources();

        /// <summary>人工核实后恢复一个待确认资源区域。</summary>
        bool TryRecoverResources(string requestId, string confirmation, out string reason);

        /// <summary>
        /// 接受一次单轴动作并锁定绝对终点。profile 给出速度与加减速度，回零时可为空。设备必须在此完成前置条件检查；
        /// 检查不通过时抛出异常，不产生任何动作。
        /// </summary>
        ITaskAxisMove BeginMove(string name, double target, bool relative, bool home, AxisMotionProfile profile);

        /// <summary>按进度推进本次动作；真实设备可以忽略进度，以反馈位置为准。</summary>
        void AdvanceMove(ITaskAxisMove move, double progress);

        /// <summary>本次动作是否已完成到位且停稳；true 必须依据设备反馈确认停止，不能确认时返回 false 或报错。</summary>
        bool IsMoveComplete(ITaskAxisMove move, double progress);

        /// <summary>
        /// 结束一次单轴动作并释放占用。completed 为 false 时表示取消或失败，
        /// 实现方必须让轴受控停止并等待停稳，不得把中途位置当作到位。若停止失败但旧动作仍在异步收尾，
        /// 必须抛出包含待收尾任务的 TaskMoveCleanupPendingException；其他异常只能在没有未报告后台收尾时返回。
        /// </summary>
        Task EndMoveAsync(ITaskAxisMove move, bool completed, CancellationToken cancellationToken);

        /// <summary>接受一组轴动作；任一目标不合法时整组都不接受。</summary>
        IReadOnlyList<ITaskAxisGroupMove> BeginGroupMove(IReadOnlyList<TaskAxisGroupTarget> targets);

        /// <summary>推进整组动作；全部轴到位后返回 true，detail 给出各轴当前位置。</summary>
        bool AdvanceGroupMove(IReadOnlyList<ITaskAxisGroupMove> moves, double elapsedMilliseconds, out string detail);

        /// <summary>结束整组动作；未完成的轴必须受控停止并等待停稳。</summary>
        Task EndGroupMoveAsync(IReadOnlyList<ITaskAxisGroupMove> moves, CancellationToken cancellationToken);

        /// <summary>确认指定轴当前没有动作；轴仍被其他动作占用时必须报错。</summary>
        Task ConfirmStoppedAsync(string name, CancellationToken cancellationToken);

        /// <summary>读取请求/完成握手的两个变量当前值；null 表示当前没有有效数据。</summary>
        void ReadHandshake(string requestName, string completedName, out bool? request, out bool? completed);

        /// <summary>仅在请求有效且完成信号未置位时发布；未知值或条件不满足返回 false，不伪造握手成功。</summary>
        bool TryPublishCompletion(string requestName, string completedName);

        /// <summary>仅在请求已清且完成信号已置位时复归；未知值或条件不满足返回 false。</summary>
        bool TryClearCompletion(string requestName, string completedName);

        /// <summary>取得本设备控制权；已被其他控制器或独立运行占用时必须报错。</summary>
        void ClaimMachine(object owner);

        /// <summary>归还本设备控制权；仍有未收尾运行或不匹配时必须报错。</summary>
        void ReleaseMachine(object owner);

        /// <summary>登记即将开始的运行，用于阻止独立入口绕过整机控制。</summary>
        void RegisterRun(string runId, object owner);

        /// <summary>动作全部退出后移除运行登记。</summary>
        void UnregisterRun(string runId);
    }
}
