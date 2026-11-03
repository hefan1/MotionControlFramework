using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Equipment.Demo;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Wpf.ManualDebug;
using MotionControl.Host.Wpf;
using MotionControl.Host.Wpf.Recovery;
using MotionControl.Framework.Runtime.Variables;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>用不连接硬件的替身检查 Runtime 状态、报警和工位步骤行为。</summary>
    internal static class RuntimeRegressionChecks
    {
        /// <summary>依次运行核心 Runtime 回归检查；任一失败时返回错误信息。</summary>
        public static async Task<IReadOnlyList<string>> RunAsync()
        {
            var failures = new List<string>();
            await RunCheckAsync("连接中途失败后回收已连接卡", ConnectionFailureRollsBackEarlierCardsAsync, failures);
            await RunCheckAsync("重复逻辑轴名称被拒绝", DuplicateAxisNameIsRejectedAsync, failures);
            await RunCheckAsync("当前正运动 Host 拒绝多卡配置", ZMotionHostRejectsMultipleCardsAsync, failures);
            await RunCheckAsync("未配置自动流程时允许连接手动调试但禁止启动", ManualOnlyRuntimeConnectsButCannotStartAsync, failures);
            await RunCheckAsync("工位流程与任务执行入口互斥，同一时刻只有一个能启动", ControlOwnerExcludesTaskExecutionAsync, failures);
            await RunCheckAsync("设备定义按设备环境创建工位并拒绝越行程配置", EquipmentDefinitionCreatesStationsFromContextAsync, failures);
            await RunCheckAsync("执行入口可由设备配置指定", ConfiguredControlOwnerIsHonoredAsync, failures);
            await RunCheckAsync("任一轴仍在运动时禁止启动自动流程", MovingAxisBlocksAutomaticStartAsync, failures);
            await RunCheckAsync("轴速度异常或反馈未知时禁止复位、准备、启动和断卡", UnknownAxisStopBlocksCommandsAsync, failures);
            await RunCheckAsync("硬限位后未配置初始化流程时必须先回零", HomingRequiredAxisBlocksStartWithoutInitializationAsync, failures);
            await RunCheckAsync("可选初始化先于工位、暂停续跑且每个连接周期只执行一次", EquipmentInitializationRunsBeforeStationsAndResumesAsync, failures);
            await RunCheckAsync("启动后立即暂停也能等待后台初始化调度收尾", PauseImmediatelyAfterStartWaitsForInitializationAsync, failures);
            await RunCheckAsync("初始化结束后仍检查轴是否真正完成回零", EquipmentInitializationMustClearHomingRequirementAsync, failures);
            await RunCheckAsync("共享轴等待释放且不同轴可以并行申请", AxisResourcesSerializeSharedAxesAsync, failures);
            await RunCheckAsync("轴组整体占用并在等待取消时归还已申请轴", AxisGroupResourcesReleaseAfterCancellationAsync, failures);
            await RunCheckAsync("多卡断开先检查所有轴，不留下半连接状态", MovingAxisPreventsPartialCardDisconnectAsync, failures);
            await RunCheckAsync("必须复位后启动，暂停后保留当前步骤", ResetAndPauseKeepCurrentStepAsync, failures);
            await RunCheckAsync("在线映射与整机命令互斥", OnlineMappingRespectsMachineCommandsAsync, failures);
            await RunCheckAsync("有效或不可读的输出不能被更换映射", OutputMappingSafetyChecks.RunAsync, failures);
            await RunCheckAsync("停机报警暂停流程并要求复位", StopAlarmPausesMachineUntilResetAsync, failures);
            await RunCheckAsync("多报警原子发布且订阅者读取完整快照", BatchedAlarmUpdatesPublishCompleteSnapshotAsync, failures);
            await RunCheckAsync("非停机报警在条件恢复后自动清除", NonStopAlarmClearsWhenRecoveredAsync, failures);
            await RunCheckAsync("工位流程异常使整机进入报警", StationFailureAlarmsMachineAsync, failures);
            await RunCheckAsync("未配置软件急停 DI 不阻止手动调试但禁止自动启动", UnconfiguredEmergencyStopAllowsManualDebugOnlyAsync, failures);
            await RunCheckAsync("手动轴和 DO 操作实时检查急停并等待复位", EmergencyStopBlocksManualOperationsAsync, failures);
            await RunCheckAsync("旧工位收尾未确认时禁止手动动作但允许受控停止", UnsettledStationBlocksManualMotionButAllowsStopAsync, failures);
            await RunCheckAsync("急停有效时禁止复位和启动，运行期间触发会暂停流程", EmergencyStopBlocksAndStopsMachineAsync, failures);
            await RunCheckAsync("轴仍在运动时拒绝整机复位", ResetIsRejectedWhileAxisIsMovingAsync, failures);
            await RunCheckAsync("急停状态通过配置的逻辑 DI 读取", EmergencyStopStatusUsesConfiguredInputAsync, failures);
            await RunCheckAsync("现场按钮按上升沿执行复位、启动、暂停并在 DI 故障恢复后要求复位", PhysicalButtonInputsFollowSafeSequenceAsync, failures);
            await RunCheckAsync("断卡失败后现场按钮监控恢复且不误触发", ControlInputsResumeAfterFailedDisconnectAsync, failures);
            await RunCheckAsync("设备硬件配置关联和急停输入校验", HardwareConfigurationValidationAsync, failures);
            await RunCheckAsync("通用框架程序集不引用厂商适配器或原生 API", SharedFrameworkAssembliesDoNotReferenceVendorAdaptersAsync, failures);
            await RunCheckAsync("正运动轴号超出控制器范围时在连接前被拒绝", ZMotionAxisNumberOutsideControllerRangeIsRejectedAsync, failures);
            await RunCheckAsync("示例工位轴动作、传感器、输出、超时报警和复位闭环", DemoStationFlowChecks.RunAsync, failures);
            await RunCheckAsync("设备变量跨进程恢复及配置变化保护", MachineRecoveryChecks.RunAsync, failures);
            await RunCheckAsync("自动运行异常退出标记及受控暂停保存", RunMarkerSurvivesUntilSafePauseAsync, failures);
            await RunCheckAsync("设备准备异常退出标记及安全停止", PreparationMarkerSurvivesUntilSafeStopAsync, failures);
            await RunCheckAsync("任务暂停保留证据并禁止未确认的续跑", TaskResourceMarkerSurvivesPauseAsync, failures);
            await RunCheckAsync("任务故障复位及会话关闭后保留未确认运行标记", FaultMarkerBlocksUnsafeRestartAsync, failures);
            await RunCheckAsync("已结算任务仅同一会话允许续跑且保留运行证据", SettledTaskSessionCanResumeAsync, failures);
            await RunCheckAsync("准备标记转为生产标记后不得借准备清除", PreparationMarkerBecomesTaskMarkerAsync, failures);
            await RunCheckAsync("C# 工位报警复位仅同连接允许续跑并保留记录", StationFlowMarkerResumesSameConnectionAsync, failures);
            return failures.AsReadOnly();
        }

        /// <summary>验证 C# 工位报警复位续跑、运行记录持久保留和重连隔离。</summary>
        private static async Task StationFlowMarkerResumesSameConnectionAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "StationFlowResume_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                var store = new MachineRecoveryStore(configuration, "Machine01");
                var coordinator = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                coordinator.Load(out string warning);
                var alarms = new MachineAlarmService();
                var flow = new WaitingStationFlow("工位", "等待工件");
                var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", new FakeAxis("X")) }),
                    new IStationFlow[] { flow }, alarms, new FakeSafetyStatus());
                runtime.SetBeforeAutomaticRun(() => coordinator.InvalidateBeforeAutomaticRun(runtime));
                runtime.SetPreparationRecordCallbacks(() => coordinator.MarkBeforePreparation(runtime), coordinator.SaveAfterSafePreparation);
                runtime.ConfigurePreparations(new[] { new RecoveryPreparationForCheck() });
                runtime.StatusChanged += (sender, args) => { if (runtime.State == MachineRunState.Paused) coordinator.SavePausedCheckpoint(runtime); };
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.PrepareAsync(null, CancellationToken.None).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun() && runtime.IsPrepared, "C# 设备准备没有保留标记或确认准备完成。");
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await flow.WaitUntilStartedAsync().ConfigureAwait(false);
                var alarm = new MachineAlarmDefinition("STATION_FEED_TIMEOUT", "等待供料超时", MachineAlarmKind.StopMachine);
                alarms.ReportCondition(alarm, "工位", true, "上料未到位");
                await WaitForStateAsync(runtime, MachineRunState.Alarmed).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "工位报警后运行标记丢失。");
                alarms.ReportCondition(alarm, "工位", false, "供料恢复");
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(coordinator.GetAutomaticStartBlockReason(runtime) == null, "同一连接确认收尾后仍被错误拦截。");
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "报警后续跑不应删除标记。");
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "C# 正常暂停不应删除标记。");
                await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(!string.IsNullOrWhiteSpace(coordinator.GetAutomaticStartBlockReason(runtime)), "断卡重连错误复用了旧连接的生产标记。");
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "断卡重连不得删除旧运行标记。");
                var restarted = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                restarted.Load(out warning);
                Ensure(!string.IsNullOrWhiteSpace(restarted.RecoveryBlockReason), "进程重启不得复用旧连接的收尾确认。");
                await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        /// <summary>准备记录仅可在同一 Runtime 内作为生产起点，生产后不能借准备清除证据。</summary>
        private static async Task PreparationMarkerBecomesTaskMarkerAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "PreparationToTask_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                var store = new MachineRecoveryStore(configuration, "Machine01");
                var coordinator = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                coordinator.Load(out string warning);
                var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", new FakeAxis("X")) }),
                    new IStationFlow[0], new MachineAlarmService(), new FakeSafetyStatus(), controlOwner: MachineControlOwner.TaskEngine);
                var owner = new RecoveryResourceOwner();
                object session = new object();
                coordinator.SetTaskSessionSettlementQueries(() => session, current => true);
                runtime.SetBeforeAutomaticRun(() => coordinator.InvalidateBeforeAutomaticRun(runtime));
                runtime.SetPreparationRecordCallbacks(() => coordinator.MarkBeforePreparation(runtime), coordinator.SaveAfterSafePreparation);
                runtime.ConfigurePreparations(new[] { new RecoveryPreparationForCheck() });
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.PrepareAsync(null, CancellationToken.None).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun() && runtime.IsPreparationRecordReusable, "准备结束未保留可确认的运行记录。");
                await runtime.AcquireTaskEngineControlAsync(owner, CancellationToken.None).ConfigureAwait(false);
                await runtime.BeginTaskEngineRunAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "准备转生产时错误清除了运行标记。");
                await runtime.FinishTaskEngineRunAsync(false, null).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    coordinator.MarkBeforePreparation(runtime);
                    throw new InvalidOperationException("生产标记被错误复用为准备标记。");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("已有未确认")) { }
                Ensure(store.HasUnfinishedAutomaticRun(), "准备操作不得清除生产运行标记。");
                runtime.ReleaseTaskEngineControl(owner);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        /// <summary>模拟同一任务会话完成收尾，以及结算失败、会话变更和进程重启。</summary>
        private static async Task SettledTaskSessionCanResumeAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "TaskSessionResume_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                var store = new MachineRecoveryStore(configuration, "Machine01");
                var coordinator = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                coordinator.Load(out string warning);
                var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", new FakeAxis("X")) }),
                    new IStationFlow[0], new MachineAlarmService(), new FakeSafetyStatus(), controlOwner: MachineControlOwner.TaskEngine);
                var owner = new RecoveryResourceOwner();
                object session = new object();
                bool settled = false;
                coordinator.SetTaskSessionSettlementQueries(() => session, current => settled);
                runtime.SetBeforeAutomaticRun(() => coordinator.InvalidateBeforeAutomaticRun(runtime));
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.AcquireTaskEngineControlAsync(owner, CancellationToken.None).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.BeginTaskEngineRunAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.FinishTaskEngineRunAsync(false, null).ConfigureAwait(false);
                coordinator.SavePausedCheckpoint(runtime);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.BeginTaskEngineRunAsync(CancellationToken.None)).ConfigureAwait(false);
                settled = true;
                owner.State = TaskResourceRecoveryState.Blocked;
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.BeginTaskEngineRunAsync(CancellationToken.None)).ConfigureAwait(false);
                owner.State = TaskResourceRecoveryState.Clear;
                Ensure(coordinator.GetAutomaticStartBlockReason(runtime) == null, "已结算同一会话仍被错误阻止。");
                try
                {
                    coordinator.MarkBeforePreparation(runtime);
                    throw new InvalidOperationException("生产运行标记被错误地作为准备标记复用。");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("已有未确认")) { }
                Ensure(store.HasUnfinishedAutomaticRun(), "准备请求不得替代生产运行记录。");
                await runtime.BeginTaskEngineRunAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "同会话续跑不应移除运行标记。");
                await runtime.FinishTaskEngineRunAsync(false, null).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                session = new object();
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.BeginTaskEngineRunAsync(CancellationToken.None)).ConfigureAwait(false);
                var restarted = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                restarted.Load(out warning);
                Ensure(!string.IsNullOrWhiteSpace(restarted.RecoveryBlockReason), "重启后不得借用旧进程的收尾确认。");
                Ensure(store.HasUnfinishedAutomaticRun(), "跨会话检查不得清除运行标记。");
                runtime.ReleaseTaskEngineControl(owner);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        /// <summary>尚无全路径收尾证据时，故障复位或另开会话不能绕过原运行标记。</summary>
        private static async Task FaultMarkerBlocksUnsafeRestartAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "TaskFaultMarker_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                var store = new MachineRecoveryStore(configuration, "Machine01");
                var coordinator = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                coordinator.Load(out string warning);
                var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", new FakeAxis("X")) }),
                    new IStationFlow[0], new MachineAlarmService(), new FakeSafetyStatus(), controlOwner: MachineControlOwner.TaskEngine);
                var owner = new RecoveryResourceOwner();
                runtime.SetBeforeAutomaticRun(() => coordinator.InvalidateBeforeAutomaticRun(runtime));
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.AcquireTaskEngineControlAsync(owner, CancellationToken.None).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(coordinator.GetAutomaticStartBlockReason(runtime) == null, "首次启动前错误显示恢复保护。");
                await runtime.BeginTaskEngineRunAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.FinishTaskEngineRunAsync(true, new InvalidOperationException("输入等待超时")).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await runtime.BeginTaskEngineRunAsync(CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException("故障收尾未确认却允许续跑。");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("联系维护人员")) { }
                Ensure(store.HasUnfinishedAutomaticRun(), "故障复位后错误清除了运行标记。");
                runtime.ReleaseTaskEngineControl(owner);
                var nextOwner = new RecoveryResourceOwner();
                await runtime.AcquireTaskEngineControlAsync(nextOwner, CancellationToken.None).ConfigureAwait(false);
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.BeginTaskEngineRunAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "新任务会话错误清除了旧标记。");
                var afterRestart = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                afterRestart.Load(out warning);
                Ensure(!string.IsNullOrWhiteSpace(afterRestart.RecoveryBlockReason), "进程重启未阻止未经确认的任务运行。");
                runtime.ReleaseTaskEngineControl(nextOwner);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        /// <summary>尚无完整收尾屏障时，即使资源为空，同会话和跨进程也不得把暂停视为安全。</summary>
        private static async Task TaskResourceMarkerSurvivesPauseAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "TaskResourceMarker_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                var store = new MachineRecoveryStore(configuration, "Machine01");
                var coordinator = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                coordinator.Load(out string warning);
                coordinator.SaveForShutdown(null, false);
                string path = Path.Combine(configuration.GetMachineDirectory("Machine01"), "Runtime", "RecoverySnapshot.json");
                string original = File.ReadAllText(path);
                var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", new FakeAxis("X")) }),
                    new IStationFlow[0], new MachineAlarmService(), new FakeSafetyStatus(), controlOwner: MachineControlOwner.TaskEngine);
                var owner = new RecoveryResourceOwner();
                runtime.SetBeforeAutomaticRun(() => coordinator.InvalidateBeforeAutomaticRun(runtime));
                runtime.StatusChanged += (sender, args) => { if (runtime.State == MachineRunState.Paused) coordinator.SavePausedCheckpoint(runtime); };
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.AcquireTaskEngineControlAsync(owner, CancellationToken.None).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.BeginTaskEngineRunAsync(CancellationToken.None).ConfigureAwait(false);
                owner.State = TaskResourceRecoveryState.Clear;
                await runtime.FinishTaskEngineRunAsync(false, null).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun() && File.ReadAllText(path) == original, "暂停时资源为空却丢失了标记或覆盖旧记录。");
                string readinessReason = coordinator.GetAutomaticStartBlockReason(runtime);
                Ensure(!string.IsNullOrWhiteSpace(readinessReason) && readinessReason.Contains("维护人员"), "暂停后启动按钮缺少保护原因和处理方向。");
                Ensure(store.HasUnfinishedAutomaticRun() && File.ReadAllText(path) == original, "查询启动原因不应更改恢复记录。");
                var restarted = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                restarted.Load(out warning);
                Ensure(!string.IsNullOrWhiteSpace(restarted.RecoveryBlockReason), "占用资源时重启没有拦截。");
                coordinator.SaveForShutdown(runtime, true);
                Ensure(store.HasUnfinishedAutomaticRun() && File.ReadAllText(path) == original, "关闭时覆盖占用资源的保护记录。");
                runtime.ReleaseTaskEngineControl(owner);
                coordinator.SaveForShutdown(runtime, true);
                Ensure(store.HasUnfinishedAutomaticRun() && File.ReadAllText(path) == original, "会话释放后错误清除了尚未结算的任务标记。");
                await runtime.AcquireTaskEngineControlAsync(owner, CancellationToken.None).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.BeginTaskEngineRunAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "资源为空时错误允许续跑或丢失运行标记。");
                owner.State = TaskResourceRecoveryState.Held;
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.BeginTaskEngineRunAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "占用资源时错误允许续跑或丢失运行标记。");
                owner.State = TaskResourceRecoveryState.Blocked;
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.BeginTaskEngineRunAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "隔离资源尝试启动后丢失了标记。");
                owner.State = TaskResourceRecoveryState.Unknown;
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.BeginTaskEngineRunAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "未知资源状态错误清除标记。");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        /// <summary>可控资源状态替身，只用于检查运行标记，不访问控制卡。</summary>
        private sealed class RecoveryResourceOwner : ITaskResourceRecoveryState
        {
            /// <summary>当前测试的资源状态。</summary>
            public TaskResourceRecoveryState State { get; set; }
            /// <summary>返回测试设置的资源状态。</summary>
            public TaskResourceRecoveryState GetResourceRecoveryState() { return State; }
        }

        /// <summary>自动运行与暂停后均保留标记；重启和新的连接周期不能自动复用现场状态。</summary>
        private static async Task RunMarkerSurvivesUntilSafePauseAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "MachineRunMarker_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                var store = new MachineRecoveryStore(configuration, "Machine01");
                var variables = new DeviceVariableStore(new DeviceVariableDefinition[0]);
                var coordinator = new MachineRecoveryCoordinator(store, variables, "Machine01");
                string warning;
                coordinator.Load(out warning);
                coordinator.SaveForShutdown(null, false);
                string snapshotPath = Path.Combine(configuration.GetMachineDirectory("Machine01"), "Runtime", "RecoverySnapshot.json");
                string previous = File.ReadAllText(snapshotPath);
                var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", new FakeAxis("X")) }),
                    new IStationFlow[] { new WaitingStationFlow("工位", "等待") }, new MachineAlarmService(), new FakeSafetyStatus());
                runtime.SetBeforeAutomaticRun(() => coordinator.InvalidateBeforeAutomaticRun(runtime));
                runtime.StatusChanged += (sender, args) => { if (runtime.State == MachineRunState.Paused) coordinator.SavePausedCheckpoint(runtime); };
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun() && File.ReadAllText(snapshotPath) == previous, "运行未建立独立标记或覆盖了原记录。");
                var afterCrash = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                afterCrash.Load(out warning);
                Ensure(!string.IsNullOrWhiteSpace(afterCrash.RecoveryBlockReason), "运行中重启未被拦截。");
                try { coordinator.SavePausedCheckpoint(runtime); throw new InvalidOperationException("运行中允许保存暂停记录。"); }
                catch (InvalidOperationException error) when (error.Message.Contains("只有设备已受控暂停")) { }
                Ensure(store.HasUnfinishedAutomaticRun(), "运行中错误移除了标记。");
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun(), "暂停后不得删除运行标记。");
                var afterPause = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                afterPause.Load(out warning);
                Ensure(!string.IsNullOrWhiteSpace(afterPause.RecoveryBlockReason), "进程重启不得自动使用旧运行标记。");
                await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        /// <summary>真实持久标记覆盖准备中重启、受控取消、正常完成和准备失败。</summary>
        private static async Task PreparationMarkerSurvivesUntilSafeStopAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "PreparationMarker_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                var store = new MachineRecoveryStore(configuration, "Machine01");
                var coordinator = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                string warning;
                coordinator.Load(out warning);
                coordinator.SaveForShutdown(null, false);
                string path = Path.Combine(configuration.GetMachineDirectory("Machine01"), "Runtime", "RecoverySnapshot.json");
                string oldSnapshot = File.ReadAllText(path);
                var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", new FakeAxis("X")) }),
                    new IStationFlow[] { new WaitingStationFlow("工位", "等待") }, new MachineAlarmService(), new FakeSafetyStatus());
                var preparation = new RecoveryPreparationForCheck { WaitForCancel = true };
                runtime.ConfigurePreparations(new[] { preparation });
                runtime.SetPreparationRecordCallbacks(() => coordinator.MarkBeforePreparation(runtime), coordinator.SaveAfterSafePreparation);
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                Task preparing = runtime.PrepareAsync(null, CancellationToken.None);
                for (int index = 0; index < 200 && !preparation.Executing; index++) await Task.Delay(10).ConfigureAwait(false);
                Ensure(preparation.Executing && store.HasUnfinishedAutomaticRun(), "准备执行前未持久化运行标记。");
                Ensure(File.ReadAllText(path) == oldSnapshot, "准备期间覆盖了旧恢复记录。");
                var afterCrash = new MachineRecoveryCoordinator(store, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                afterCrash.Load(out warning);
                Ensure(!string.IsNullOrWhiteSpace(afterCrash.RecoveryBlockReason), "准备中意外退出未被拦截。");
                await runtime.StopPreparationAsync(CancellationToken.None).ConfigureAwait(false);
                try { await preparing.ConfigureAwait(false); } catch (OperationCanceledException) { }
                Ensure(store.HasUnfinishedAutomaticRun(), "受控停止准备后必须保留运行标记。");
                var saved = store.Load(out warning);
                Ensure(!saved.CanResume && saved.ResumeBlockedReason.Contains("设备准备"), "安全停止准备后旧工步仍允许续跑。");
                preparation.WaitForCancel = false;
                await runtime.PrepareAsync(null, CancellationToken.None).ConfigureAwait(false);
                Ensure(store.HasUnfinishedAutomaticRun() && runtime.IsPrepared, "准备成功后应保留标记并通过验收。");
                Ensure(coordinator.GetAutomaticStartBlockReason(runtime) == null, "已确认完成的准备应允许同 Runtime 启动生产。");
                await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

                var failedStore = new MachineRecoveryStore(new MachineConfigurationStore(Path.Combine(root, "failed")), "Machine01");
                var failedCoordinator = new MachineRecoveryCoordinator(failedStore, new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                failedCoordinator.Load(out warning);
                var failed = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardB", new FakeAxis("Y")) }),
                    new IStationFlow[] { new WaitingStationFlow("工位", "等待") }, new MachineAlarmService(), new FakeSafetyStatus());
                try
                {
                    coordinator.MarkBeforePreparation(failed);
                    throw new InvalidOperationException("其他 Runtime 错误复用了已有准备标记。");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("已有未确认")) { }
                Ensure(store.HasUnfinishedAutomaticRun(), "其他 Runtime 不得清除已有准备记录。");
                failed.ConfigurePreparations(new[] { new RecoveryPreparationForCheck { ThrowDuringExecute = true } });
                failed.SetPreparationRecordCallbacks(() => failedCoordinator.MarkBeforePreparation(failed), failedCoordinator.SaveAfterSafePreparation);
                await failed.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await ExpectExceptionAsync<InvalidOperationException>(() => failed.PrepareAsync(null, CancellationToken.None)).ConfigureAwait(false);
                Ensure(failedStore.HasUnfinishedAutomaticRun(), "准备失败错误清除了标记。");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        /// <summary>用于异常退出回归的设备准备替身；不访问硬件。</summary>
        private sealed class RecoveryPreparationForCheck : IStationPreparation
        {
            /// <summary>测试工位标识。</summary>
            public string StationId => "P";
            /// <summary>测试工位名称。</summary>
            public string StationName => "准备工位";
            /// <summary>测试没有前置工位。</summary>
            public IReadOnlyCollection<string> Dependencies => new string[0];
            /// <summary>开始后等待取消。</summary>
            internal bool WaitForCancel;
            /// <summary>开始后触发异常。</summary>
            internal bool ThrowDuringExecute;
            /// <summary>标记动作已经开始。</summary>
            internal volatile bool Executing;
            /// <summary>检查开始状态，不进行动作。</summary>
            public Task ValidateStartAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            /// <summary>根据测试选择等待、正常完成或报错。</summary>
            public async Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken cancellationToken)
            {
                Executing = true;
                if (ThrowDuringExecute) throw new InvalidOperationException("测试准备失败。");
                if (WaitForCancel) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            /// <summary>最终状态由测试替身直接确认。</summary>
            public Task ValidateReadyAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        }

        /// <summary>检查 Contracts、Runtime 和通用 WPF 页面没有引用具体厂商适配器或原生 API 项目。</summary>
        private static Task SharedFrameworkAssembliesDoNotReferenceVendorAdaptersAsync()
        {
            var frameworkAssemblies = new[]
            {
                typeof(IMotionCard).Assembly,
                typeof(MachineRuntime).Assembly,
                typeof(ManualMotionDebugView).Assembly
            };

            foreach (var frameworkAssembly in frameworkAssemblies)
            {
                foreach (var reference in frameworkAssembly.GetReferencedAssemblies())
                {
                    bool isVendorAdapter = reference.Name.StartsWith(
                        "MotionControl.Adapter.MotionCard.",
                        StringComparison.Ordinal);
                    bool isVendorApi = reference.Name.StartsWith(
                        "MotionControl.VendorApi.",
                        StringComparison.Ordinal);

                    if (isVendorAdapter || isVendorApi)
                    {
                        throw new InvalidOperationException(
                            "通用程序集 '" + frameworkAssembly.GetName().Name
                            + "' 引用了厂商程序集 '" + reference.Name
                            + "'；厂商 SDK 应只由对应适配器引用。");
                    }
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>确认正运动物理轴号 64 在创建适配器时被拒绝，不会等到连接后才调用 SDK。</summary>
        private static Task ZMotionAxisNumberOutsideControllerRangeIsRejectedAsync()
        {
            var invalidAxis = new ZMotionAxisConfiguration
            {
                ControllerId = "CardA",
                Name = "OutOfRangeAxis",
                AxisNumber = 64,
                UnitsPerEngineeringUnit = 1,
                MinimumPosition = -10,
                MaximumPosition = 10,
                ManualSpeed = 1
            };

            try
            {
                new ZMotionCard(
                    "CardA",
                    new ZMotionCardConfiguration(),
                    new[] { invalidAxis },
                    new ZMotionIoPointConfiguration[0],
                    new ZMotionIoPointConfiguration[0]);
            }
            catch (InvalidOperationException error)
            {
                Ensure(
                    error.Message.Contains("0 到 63") && error.Message.Contains("64"),
                    "越界轴号的错误信息没有同时说明有效范围和实际值。 " + error.Message);
                return Task.CompletedTask;
            }

            throw new InvalidOperationException("正运动物理轴号 64 在适配器创建时没有被拒绝。");
        }

        /// <summary>执行一个回归用例并保存带有用例名称的异常信息。</summary>
        private static async Task RunCheckAsync(string name, Func<Task> check, ICollection<string> failures)
        {
            try
            {
                await check().ConfigureAwait(false);
                Console.WriteLine("[Runtime通过] " + name);
            }
            catch (Exception error)
            {
                failures.Add(name + "：" + error.Message);
                Console.WriteLine("[Runtime失败] " + name + "：" + error.Message);
            }
        }

        /// <summary>确认工位流程入口与任务执行入口互斥，同一时刻只有一个能启动设备。</summary>
        private static async Task ControlOwnerExcludesTaskExecutionAsync()
        {
            var stationCard = new FakeMotionCard("OwnerStationCard", new FakeAxis("X"));
            var stationRuntime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { stationCard }),
                new IStationFlow[] { new WaitingStationFlow("上料工位", "等待上料到位") },
                new MachineAlarmService(),
                new FakeSafetyStatus());

            Ensure(
                stationRuntime.ControlOwner == MachineControlOwner.StationFlows,
                "配置工位流程后执行入口不是工位流程。");
            await stationRuntime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

            // 工位流程定义了但没在运行：允许切到任务入口；切换后工位流程不能再启动设备。
            await stationRuntime.AcquireTaskEngineControlAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(
                stationRuntime.ControlOwner == MachineControlOwner.TaskEngine,
                "空闲设备没有切换成任务执行入口。");
            await stationRuntime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => stationRuntime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(
                stationRuntime.ControlOwner == MachineControlOwner.TaskEngine,
                "工位流程启动被拒后执行入口被改掉了。");
            stationRuntime.ReleaseTaskEngineControl();
            Ensure(
                stationRuntime.ControlOwner == MachineControlOwner.StationFlows,
                "归还任务入口后没有回到工位流程入口。");
            await stationRuntime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

            var taskCard = new FakeMotionCard("OwnerTaskCard", new FakeAxis("X"));
            var taskRuntime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { taskCard }),
                new IStationFlow[0],
                new MachineAlarmService(),
                new FakeSafetyStatus());

            Ensure(taskRuntime.ControlOwner == MachineControlOwner.None, "未配置工位流程时执行入口不是空。");
            await taskRuntime.AcquireTaskEngineControlAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(
                taskRuntime.ControlOwner == MachineControlOwner.TaskEngine,
                "任务执行入口接管后执行入口未更新。");
            await taskRuntime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await taskRuntime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => taskRuntime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(taskRuntime.State == MachineRunState.Ready, "任务入口接管时误启动改变了整机状态。");
            await ExpectExceptionAsync<InvalidOperationException>(
                () => taskRuntime.DisconnectAsync(CancellationToken.None)).ConfigureAwait(false);
            taskRuntime.ReleaseTaskEngineControl();
            await taskRuntime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认执行入口可以由配置直接指定，不必依赖"是否配置了工位流程"来推断。</summary>
        private static async Task ConfiguredControlOwnerIsHonoredAsync()
        {
            var card = new FakeMotionCard("ConfiguredOwnerCard", new FakeAxis("X"));
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { new WaitingStationFlow("配置工位", "等待输入") },
                new MachineAlarmService(),
                new FakeSafetyStatus(),
                controlOwner: MachineControlOwner.TaskEngine);

            Ensure(
                runtime.ControlOwner == MachineControlOwner.TaskEngine,
                "配置指定的执行入口没有被采用，仍按工位流程推断。");
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Ready, "入口被拒绝时误启动改变了整机状态。");
            object session = new object();
            await runtime.AcquireTaskEngineControlAsync(session, CancellationToken.None).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.AcquireTaskEngineControlAsync(new object(), CancellationToken.None)).ConfigureAwait(false);
            runtime.ReleaseTaskEngineControl(session);
            Ensure(runtime.ControlOwner == MachineControlOwner.TaskEngine, "归还后未保留配置指定的任务入口。");
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认设备定义只通过设备环境创建工位，并按软限位拒绝越行程的工位配置。</summary>
        private static Task EquipmentDefinitionCreatesStationsFromContextAsync()
        {
            var stationSet = new DemoStationSetSettings { SchemaVersion = 1, Enabled = true };
            stationSet.Stations.Add(new DemoStationSettings
            {
                Enabled = true,
                StationName = "上料工位",
                AxisName = "Load_X",
                SensorInputName = "上料到位",
                OutputName = "夹紧",
                TargetPosition = 10
            });
            stationSet.Stations.Add(new DemoStationSettings
            {
                Enabled = true,
                StationName = "下料工位",
                AxisName = "Unload_X",
                SensorInputName = "下料到位",
                OutputName = "松开",
                TargetPosition = 20
            });

            FakeEquipmentContext context = CreateEquipmentContext(stationSet);
            IReadOnlyList<IStationFlow> flows = new DemoEquipmentDefinition().CreateStationFlows(context);
            Ensure(flows.Count == 2, "设备定义没有按启用的工位创建两条流程。");
            Ensure(
                flows.Any(flow => flow.StationName == "上料工位") && flows.Any(flow => flow.StationName == "下料工位"),
                "设备定义创建的工位名称与配置不一致。");

            stationSet.Stations[0].TargetPosition = 500;
            try
            {
                new DemoEquipmentDefinition().CreateStationFlows(CreateEquipmentContext(stationSet));
            }
            catch (InvalidOperationException error)
            {
                Ensure(
                    error.Message.Contains("超出逻辑轴"),
                    "越行程工位失败信息没有指出超出逻辑轴行程。 " + error.Message);
                return Task.CompletedTask;
            }

            throw new InvalidOperationException("工位目标位置超出软限位时设备定义没有拒绝创建。");
        }

        /// <summary>按固定硬件映射创建一套设备环境，供设备定义检查复用。</summary>
        private static FakeEquipmentContext CreateEquipmentContext(DemoStationSetSettings stationSet)
        {
            return new FakeEquipmentContext(
                stationSet,
                new[]
                {
                    new AxisHardwareDefinition { Name = "Load_X", MinimumPosition = -100, MaximumPosition = 100 },
                    new AxisHardwareDefinition { Name = "Unload_X", MinimumPosition = -100, MaximumPosition = 100 }
                },
                new[]
                {
                    new DigitalIoHardwareDefinition { Name = "上料到位" },
                    new DigitalIoHardwareDefinition { Name = "下料到位" }
                },
                new[]
                {
                    new DigitalIoHardwareDefinition { Name = "夹紧" },
                    new DigitalIoHardwareDefinition { Name = "松开" }
                },
                new[]
                {
                    new MachineAlarmDefinition("DEMO_SENSOR_TIMEOUT", "等待传感器超时", MachineAlarmKind.StopMachine)
                });
        }

        /// <summary>确认第二张卡连接失败时，第一张卡会被连接组回收。</summary>
        private static async Task ConnectionFailureRollsBackEarlierCardsAsync()
        {
            var firstCard = new FakeMotionCard("CardA", new FakeAxis("X"));
            var failedCard = new FakeMotionCard("CardB", new FakeAxis("Y"))
            {
                ConnectError = new InvalidOperationException("模拟连接失败")
            };
            var group = new MotionCardGroup(new IMotionCard[] { firstCard, failedCard });

            await ExpectExceptionAsync<InvalidOperationException>(
                () => group.ConnectAsync(CancellationToken.None)).ConfigureAwait(false);

            Ensure(!firstCard.IsConnected, "连接回滚后第一张卡仍处于连接状态。");
            Ensure(!failedCard.IsConnected, "连接失败的卡不应处于连接状态。");
        }

        /// <summary>确认当前单卡正运动 Host 遇到多卡配置时会在调用厂商 API 前明确拒绝。</summary>
        private static async Task ZMotionHostRejectsMultipleCardsAsync()
        {
            var connection = new ZMotionAxisMappingCardConnection(
                new MachineConfigurationStore(System.IO.Path.GetTempPath()),
                "MultiCardConfigurationCheck");
            IReadOnlyList<MotionCardSelectionOption> supportedCards = connection.SupportedMotionCards;
            Ensure(supportedCards.Count > 0, "正运动 Host 没有提供可用于配置检查的卡型。");

            MotionCardSelectionOption selectedCard = supportedCards[0];
            var settings = new MotionCardSettingsDocument
            {
                MotionCardVendor = selectedCard.VendorCode,
                MotionCardType = selectedCard.CardTypeCode,
                MotionCards = new List<MotionCardConnectionDefinition>
                {
                    new MotionCardConnectionDefinition { ControllerId = "CardA" },
                    new MotionCardConnectionDefinition { ControllerId = "CardB" }
                }
            };

            try
            {
                await connection.ConnectAsync(settings, CancellationToken.None).ConfigureAwait(false);
            }
            catch (NotSupportedException error)
            {
                Ensure(
                    error.Message.Contains("多张运动控制卡")
                        && error.Message.Contains("已拒绝连接"),
                    "多卡配置错误没有说明 Host 暂不支持并已拒绝连接。 " + error.Message);
                Ensure(!connection.IsConnected, "拒绝多卡配置后 Host 不应保持连接状态。");
                return;
            }

            throw new InvalidOperationException("当前正运动 Host 没有拒绝两张卡的配置。");
        }

        /// <summary>确认不同控制卡上的逻辑轴名称不能重复。</summary>
        private static Task DuplicateAxisNameIsRejectedAsync()
        {
            var firstCard = new FakeMotionCard("CardA", new FakeAxis("X"));
            var secondCard = new FakeMotionCard("CardB", new FakeAxis("x"));
            ExpectException<ArgumentException>(() => new MotionCardGroup(new IMotionCard[] { firstCard, secondCard }));
            return Task.CompletedTask;
        }

        /// <summary>确认只做轴手动调试的设备可以连接和复位，但不会空跑自动流程。</summary>
        private static async Task ManualOnlyRuntimeConnectsButCannotStartAsync()
        {
            var card = new FakeMotionCard("ManualCard", new FakeAxis("X"));
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[0],
                new MachineAlarmService(),
                new FakeSafetyStatus());

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.ResetRequired, "手动调试设备连接后没有进入待复位状态。");
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Ready, "手动调试设备复位后没有进入就绪状态。");
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Ready, "无工位流程时误启动改变了整机状态。");
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(!card.IsConnected, "手动调试设备断开后控制卡仍处于连接状态。");
        }

        /// <summary>确认手动定位或点动尚未停止时，Runtime 不会启动并行工位流程。</summary>
        private static async Task MovingAxisBlocksAutomaticStartAsync()
        {
            var movingAxis = new FakeAxis("MovingAxis");
            var card = new FakeMotionCard("MovingCard", movingAxis);
            var flow = new WaitingStationFlow("送料工位", "等待取料到位");
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { flow },
                new MachineAlarmService(),
                new FakeSafetyStatus());

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            movingAxis.IsMoving = true;
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);

            Ensure(runtime.State == MachineRunState.Alarmed, "轴仍在运动时，Runtime 没有拒绝启动并保留报警状态。");
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.DisconnectAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(card.IsConnected, "轴仍在运动时，Runtime 断开请求没有保留控制卡连接。");

            movingAxis.IsMoving = false;
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>运动标志为停止但速度异常或状态缺失时，不得让复位、准备、自动启动或断卡越过停稳检查。</summary>
        private static async Task UnknownAxisStopBlocksCommandsAsync()
        {
            foreach (double? velocity in new double?[] { 0.1, double.NaN, double.PositiveInfinity, double.NegativeInfinity, null })
            {
                var axis = new FakeAxis("反馈轴") { ReportedVelocity = velocity ?? 0, ReturnNullStatus = !velocity.HasValue };
                var card = new FakeMotionCard("反馈卡", axis);
                var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                    new IStationFlow[] { new WaitingStationFlow("工位", "等待") }, new MachineAlarmService(), new FakeSafetyStatus());
                var preparation = new RecoveryPreparationForCheck();
                runtime.ConfigurePreparations(new[] { preparation });
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.ResetAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(axis.AlarmResetCount == 0, "轴停稳未知时不得下发报警复位。");
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.PrepareAsync(null, CancellationToken.None)).ConfigureAwait(false);
                Ensure(!preparation.Executing, "轴停稳未知时不得开始设备准备。");
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.DisconnectAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(card.IsConnected, "轴停稳未知时不得断开控制卡。");
                axis.ReturnNullStatus = false; axis.ReportedVelocity = 0;
                await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

                var taskAxis = new FakeAxis("任务轴");
                var taskCard = new FakeMotionCard("任务卡", taskAxis);
                var taskRuntime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { taskCard }),
                    new IStationFlow[] { new WaitingStationFlow("工位", "等待") }, new MachineAlarmService(), new FakeSafetyStatus(),
                    controlOwner: MachineControlOwner.TaskEngine);
                await taskRuntime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await taskRuntime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                object owner = new object();
                await taskRuntime.AcquireTaskEngineControlAsync(owner, CancellationToken.None).ConfigureAwait(false);
                taskAxis.ReportedVelocity = velocity ?? 0; taskAxis.ReturnNullStatus = !velocity.HasValue;
                await ExpectExceptionAsync<InvalidOperationException>(() => taskRuntime.BeginTaskEngineRunAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(taskRuntime.State != MachineRunState.Running, "轴停稳未知时任务入口错误进入运行。 ");
                taskAxis.ReturnNullStatus = false; taskAxis.ReportedVelocity = 0;
                taskRuntime.ReleaseTaskEngineControl(owner);
                await taskRuntime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

                var flowAxis = new FakeAxis("流程轴");
                var flow = new WaitingStationFlow("工位", "等待");
                var flowRuntime = CreateRuntime(flow, new MachineAlarmService(), axis: flowAxis);
                await flowRuntime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await flowRuntime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                flowAxis.ReportedVelocity = velocity ?? 0; flowAxis.ReturnNullStatus = !velocity.HasValue;
                await ExpectExceptionAsync<InvalidOperationException>(() => flowRuntime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(flow.StartCount == 0, "轴停稳未知时错误启动了 C# 工位。 ");
                flowAxis.ReturnNullStatus = false; flowAxis.ReportedVelocity = 0;
                await flowRuntime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>确认没有设备初始化流程时，硬限位恢复留下的回零要求仍会阻止自动启动。</summary>
        private static async Task HomingRequiredAxisBlocksStartWithoutInitializationAsync()
        {
            var axis = new FakeAxis("LimitAxis")
            {
                RequiresHomingAfterLimitAlarm = true
            };
            var card = new FakeMotionCard("LimitCard", axis);
            var flow = new WaitingStationFlow("送料工位", "等待取料到位");
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { flow },
                new MachineAlarmService(),
                new FakeSafetyStatus());

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);

            Ensure(runtime.State == MachineRunState.Alarmed, "硬限位恢复后没有回零时，整机未进入报警状态。");
            Ensure(flow.StartCount == 0, "硬限位回零要求未满足时启动了工位流程。");
            Ensure(
                runtime.LastError.Message.Contains("必须先完成回零"),
                "硬限位回零拦截没有说明需要先完成回零。");

            axis.RequiresHomingAfterLimitAlarm = false;
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认初始化在后台先于工位执行，暂停保留步骤，重连后重新执行。</summary>
        private static async Task EquipmentInitializationRunsBeforeStationsAndResumesAsync()
        {
            var axis = new FakeAxis("HomeAxis")
            {
                RequiresHomingAfterLimitAlarm = true
            };
            var card = new FakeMotionCard("HomeCard", axis);
            var stationFlow = new WaitingStationFlow("上料工位", "等待产品到位");
            var initializationFlow = new FakeEquipmentInitializationFlow
            {
                OnComplete = () => axis.RequiresHomingAfterLimitAlarm = false
            };
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { stationFlow },
                new MachineAlarmService(),
                new FakeSafetyStatus(),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                initializationFlow);

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            SynchronizationContext previousContext = SynchronizationContext.Current;
            var startCallingContext = new SynchronizationContext();
            Task startTask;
            try
            {
                SynchronizationContext.SetSynchronizationContext(startCallingContext);
                startTask = runtime.StartAsync(CancellationToken.None);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }

            await startTask.ConfigureAwait(false);
            await initializationFlow.WaitUntilStartedAsync().ConfigureAwait(false);

            Ensure(
                !initializationFlow.SawSynchronizationContext,
                "设备初始化在启动调用线程内执行，可能阻塞 WPF 界面。");
            Ensure(stationFlow.StartCount == 0, "设备初始化完成前启动了并行工位流程。");
            Ensure(
                initializationFlow.CurrentStepName == "检查轴回零条件",
                "设备初始化开始时没有显示当前准备步骤。");

            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(!initializationFlow.IsCompleted, "暂停初始化时错误地标记为已完成。");
            Ensure(
                initializationFlow.CurrentStepName == "检查轴回零条件",
                "暂停初始化时没有保留当前步骤。");
            Ensure(stationFlow.StartCount == 0, "初始化暂停时提前启动了工位流程。");

            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            initializationFlow.AllowCompletion = true;
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForStartCountAsync(stationFlow, 1).ConfigureAwait(false);

            Ensure(initializationFlow.IsCompleted, "初始化步骤成功后没有标记完成。");
            Ensure(initializationFlow.ExecutionCount == 2, "暂停恢复后没有从保留步骤继续执行初始化。");
            Ensure(!axis.RequiresHomingAfterLimitAlarm, "初始化没有清除测试轴的硬限位后回零要求。");

            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForStartCountAsync(stationFlow, 2).ConfigureAwait(false);
            Ensure(initializationFlow.ExecutionCount == 2, "同一连接周期内复位重启重复执行了已完成初始化。");

            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForStartCountAsync(stationFlow, 3).ConfigureAwait(false);
            Ensure(initializationFlow.ExecutionCount == 3, "新的控制卡连接周期没有重新执行设备初始化。");

            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

            axis.RequiresHomingAfterLimitAlarm = true;
            initializationFlow.CanSkipAfterRestart = true;
            initializationFlow.OnRecoveryCheck = () => axis.RequiresHomingAfterLimitAlarm = false;
            runtime.RequestInitializationRecovery(true);
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForStartCountAsync(stationFlow, 4).ConfigureAwait(false);
            Ensure(initializationFlow.RecoveryCheckCount == 1, "重启后没有请求设备初始化流程核实现场状态。");
            Ensure(initializationFlow.ExecutionCount == 3, "现场状态确认有效后仍重复执行了回零初始化步骤。");
            Ensure(!axis.RequiresHomingAfterLimitAlarm, "跳过初始化后未重新确认轴已满足回零要求。");

            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

            initializationFlow.CanSkipAfterRestart = false;
            runtime.RequestInitializationRecovery(true);
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForStartCountAsync(stationFlow, 5).ConfigureAwait(false);
            Ensure(initializationFlow.RecoveryCheckCount == 2, "恢复候选未重新检查现场状态。");
            Ensure(initializationFlow.ExecutionCount == 4, "现场状态不满足时没有重新执行初始化流程。");

            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认启动后立即暂停时，后台初始化调度仍能收尾并保留当前步骤。</summary>
        private static async Task PauseImmediatelyAfterStartWaitsForInitializationAsync()
        {
            var card = new FakeMotionCard("ImmediatePauseCard", new FakeAxis("X"));
            var stationFlow = new WaitingStationFlow("上料工位", "等待产品到位");
            var initializationFlow = new FakeEquipmentInitializationFlow();
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { stationFlow },
                new MachineAlarmService(),
                new FakeSafetyStatus(),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                initializationFlow);

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);

            Ensure(runtime.State == MachineRunState.Paused, "启动后立即暂停没有等待流程进入暂停状态。");
            Ensure(!initializationFlow.IsCompleted, "启动后立即暂停错误地将初始化标记为完成。");
            Ensure(
                initializationFlow.CurrentStepName == "检查轴回零条件",
                "启动后立即暂停没有保留初始化当前步骤。");
            Ensure(stationFlow.StartCount == 0, "初始化尚未完成时，立即暂停测试启动了工位流程。");

            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认初始化流程报告完成但轴仍需回零时，工位不会启动；人工回零后可继续。</summary>
        private static async Task EquipmentInitializationMustClearHomingRequirementAsync()
        {
            var axis = new FakeAxis("HomeAxis")
            {
                RequiresHomingAfterLimitAlarm = true
            };
            var card = new FakeMotionCard("HomeCard", axis);
            var stationFlow = new WaitingStationFlow("上料工位", "等待产品到位");
            var initializationFlow = new FakeEquipmentInitializationFlow
            {
                AllowCompletion = true
            };
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { stationFlow },
                new MachineAlarmService(),
                new FakeSafetyStatus(),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                initializationFlow);

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForStateAsync(runtime, MachineRunState.Alarmed).ConfigureAwait(false);

            Ensure(initializationFlow.IsCompleted, "测试初始化流程没有报告完成。");
            Ensure(stationFlow.StartCount == 0, "轴仍要求回零时，初始化流程后启动了工位。");
            Ensure(
                runtime.LastError.ToString().Contains("必须先完成回零"),
                "初始化后的轴状态检查没有说明仍需回零。");

            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            axis.RequiresHomingAfterLimitAlarm = false;
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForStartCountAsync(stationFlow, 1).ConfigureAwait(false);
            Ensure(initializationFlow.ExecutionCount == 1, "初始化状态检查失败后不必要地重跑了已完成初始化步骤。");

            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认同一逻辑轴只能被一个流程占用，不同逻辑轴可以同时使用。</summary>
        private static async Task AxisResourcesSerializeSharedAxesAsync()
        {
            var motionCards = new MotionCardGroup(new IMotionCard[]
            {
                new FakeMotionCard(
                    "ResourceCard",
                    new FakeAxis("AxisX"),
                    new FakeAxis("AxisY"))
            });
            IAxisResourceCoordinator resources = motionCards.AxisResources;

            IDisposable firstAxisLease = await resources
                .AcquireAxisAsync("AxisX", CancellationToken.None)
                .ConfigureAwait(false);
            Task<IDisposable> secondAxisLeaseTask = resources
                .AcquireAxisAsync("axisx", CancellationToken.None);
            bool secondRequestWaited = !secondAxisLeaseTask.IsCompleted;

            firstAxisLease.Dispose();
            IDisposable secondAxisLease = await WaitForResourceLeaseAsync(
                secondAxisLeaseTask,
                "释放第一份轴占用后，第二个流程仍未获得轴资源。").ConfigureAwait(false);
            Ensure(secondRequestWaited, "同一逻辑轴被两个流程同时占用。");
            secondAxisLease.Dispose();
            secondAxisLease.Dispose();

            IDisposable xAxisLease = await resources
                .AcquireAxisAsync("AxisX", CancellationToken.None)
                .ConfigureAwait(false);
            IDisposable yAxisLease = null;
            try
            {
                yAxisLease = await resources
                    .AcquireAxisAsync("AxisY", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                xAxisLease.Dispose();
                if (yAxisLease != null)
                {
                    yAxisLease.Dispose();
                }
            }

            await ExpectExceptionAsync<KeyNotFoundException>(
                () => resources.AcquireAxisAsync("未配置轴", CancellationToken.None)).ConfigureAwait(false);
        }

        /// <summary>确认轴组按整体占用，并在等待取消后释放已经取得的轴。</summary>
        private static async Task AxisGroupResourcesReleaseAfterCancellationAsync()
        {
            var motionCards = new MotionCardGroup(new IMotionCard[]
            {
                new FakeMotionCard(
                    "ResourceCard",
                    new FakeAxis("AxisA"),
                    new FakeAxis("AxisB"))
            });
            IAxisResourceCoordinator resources = motionCards.AxisResources;

            IDisposable axisBLease = await resources
                .AcquireAxisAsync("AxisB", CancellationToken.None)
                .ConfigureAwait(false);
            Task<IDisposable> axisGroupLeaseTask = resources
                .AcquireAxisGroupAsync(new[] { "AxisB", "AxisA", "axisa" }, CancellationToken.None);
            Task<IDisposable> axisALeaseTask = resources
                .AcquireAxisAsync("AxisA", CancellationToken.None);

            bool axisGroupWaitedForAxisB = !axisGroupLeaseTask.IsCompleted;
            bool axisGroupReservedAxisA = !axisALeaseTask.IsCompleted;
            axisBLease.Dispose();

            IDisposable axisGroupLease = await WaitForResourceLeaseAsync(
                axisGroupLeaseTask,
                "轴组在两根轴都释放后仍未获得完整占用。").ConfigureAwait(false);
            bool otherFlowWaitedForWholeGroup = !axisALeaseTask.IsCompleted;
            axisGroupLease.Dispose();
            IDisposable axisALease = await WaitForResourceLeaseAsync(
                axisALeaseTask,
                "释放完整轴组后，等待中的单轴流程仍未获得资源。").ConfigureAwait(false);
            axisALease.Dispose();

            Ensure(axisGroupWaitedForAxisB, "轴组没有等待被占用的第二根轴。");
            Ensure(axisGroupReservedAxisA, "轴组等待期间没有保护已先申请的第一根轴。");
            Ensure(otherFlowWaitedForWholeGroup, "轴组占用期间另一工位提前使用了组内轴。");

            IDisposable heldAxisBLease = await resources
                .AcquireAxisAsync("AxisB", CancellationToken.None)
                .ConfigureAwait(false);
            var cancellation = new CancellationTokenSource();
            Task<IDisposable> cancelledGroupTask = resources
                .AcquireAxisGroupAsync(new[] { "AxisA", "AxisB" }, cancellation.Token);
            Task<IDisposable> axisAWaitingTask = resources
                .AcquireAxisAsync("AxisA", CancellationToken.None);

            bool cancelledGroupReservedAxisA = !axisAWaitingTask.IsCompleted;
            cancellation.Cancel();
            await ExpectExceptionAsync<OperationCanceledException>(
                () => cancelledGroupTask).ConfigureAwait(false);

            heldAxisBLease.Dispose();
            IDisposable recoveredAxisALease = await WaitForResourceLeaseAsync(
                axisAWaitingTask,
                "轴组等待取消后，已先占用的 AxisA 没有归还给其他流程。").ConfigureAwait(false);
            recoveredAxisALease.Dispose();
            cancellation.Dispose();

            Ensure(cancelledGroupReservedAxisA, "取消测试没有验证轴组等待过程中的部分占用释放。");
        }

        /// <summary>等待异步资源申请完成，超时后给出可定位的测试错误。</summary>
        private static async Task<IDisposable> WaitForResourceLeaseAsync(
            Task<IDisposable> leaseTask,
            string timeoutMessage)
        {
            Task completedTask = await Task.WhenAny(leaseTask, Task.Delay(TimeSpan.FromSeconds(3)))
                .ConfigureAwait(false);
            Ensure(ReferenceEquals(completedTask, leaseTask), timeoutMessage);
            return await leaseTask.ConfigureAwait(false);
        }

        /// <summary>确认第二张卡的轴仍在运动时，断开操作不会先断开第一张卡。</summary>
        private static async Task MovingAxisPreventsPartialCardDisconnectAsync()
        {
            var firstCard = new FakeMotionCard("CardA", new FakeAxis("AxisA"));
            var movingAxis = new FakeAxis("AxisB");
            var secondCard = new FakeMotionCard("CardB", movingAxis);
            var group = new MotionCardGroup(new IMotionCard[] { firstCard, secondCard });

            await group.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            movingAxis.IsMoving = true;
            await ExpectExceptionAsync<InvalidOperationException>(
                () => group.DisconnectAsync(CancellationToken.None)).ConfigureAwait(false);

            Ensure(firstCard.IsConnected, "发现第二张卡仍有轴运动后，第一张卡已经被断开。");
            Ensure(secondCard.IsConnected, "发现轴仍在运动后，第二张卡不应被断开。");

            movingAxis.IsMoving = false;
            await group.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(!firstCard.IsConnected && !secondCard.IsConnected, "确认所有轴停稳后，运动卡组没有全部断开。");
        }

        /// <summary>工位等待时拒绝修改映射，映射更新期间启动命令必须等待更新结束。</summary>
        private static async Task OnlineMappingRespectsMachineCommandsAsync()
        {
            var flow = new WaitingStationFlow("送料工位", "等待取料到位");
            var runtime = CreateRuntime(flow, new MachineAlarmService());
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            var manualEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var manualRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ManualOperationBlockReason> manual = runtime.RunManualRecoveryOperationAsync(async () =>
            {
                manualEntered.TrySetResult(true);
                await manualRelease.Task.ConfigureAwait(false);
            }, CancellationToken.None);
            try
            {
                Ensure(await Task.WhenAny(manualEntered.Task, Task.Delay(3000)).ConfigureAwait(false) == manualEntered.Task,
                    "手动操作未进入测试状态。");
                bool applied = false;
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.RunConnectedConfigurationChangeAsync(
                    () => { applied = true; return Task.CompletedTask; }, CancellationToken.None)).ConfigureAwait(false);
                Ensure(!applied, "手动操作尚未结束时仍应用了映射。");
            }
            finally { manualRelease.TrySetResult(true); }
            await manual.ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(() => runtime.RunConnectedConfigurationChangeAsync(
                () => Task.FromException(new InvalidOperationException("测试配置失败")), CancellationToken.None)).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Alarmed, "在线映射失败后没有禁止直接启动。");
            await ExpectExceptionAsync<InvalidOperationException>(() => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task changing = runtime.RunConnectedConfigurationChangeAsync(async () =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }, CancellationToken.None);
            try
            {
                Ensure(await Task.WhenAny(entered.Task, Task.Delay(3000)).ConfigureAwait(false) == entered.Task,
                    "在线映射没有进入保护区域。");
                Task start = runtime.StartAsync(CancellationToken.None);
                Ensure(!start.IsCompleted, "映射尚未完成时启动命令没有等待。");
                release.TrySetResult(true);
                await changing.ConfigureAwait(false);
                await start.ConfigureAwait(false);
                await flow.WaitUntilStartedAsync().ConfigureAwait(false);
                bool wrote = false;
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.RunConnectedConfigurationChangeAsync(
                    () => { wrote = true; return Task.CompletedTask; }, CancellationToken.None)).ConfigureAwait(false);
                Ensure(!wrote && runtime.State == MachineRunState.Running,
                    "工位等待输入期间仍执行了映射更新，或改变了设备状态。");
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { release.TrySetResult(true); }
        }

        /// <summary>确认启动前必须复位，暂停和复位后保留相同的工位步骤。</summary>
        private static async Task ResetAndPauseKeepCurrentStepAsync()
        {
            var flow = new WaitingStationFlow("送料工位", "等待取料到位");
            var runtime = CreateRuntime(flow, new MachineAlarmService());
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);

            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await flow.WaitUntilStartedAsync().ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.DisconnectAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Running && flow.CurrentStepName == "等待取料到位",
                "工位等待输入时轴虽已停稳，但 Runtime 不应允许断开卡或改变流程状态。");
            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);

            Ensure(runtime.State == MachineRunState.Paused, "暂停后整机没有进入 Paused 状态。");
            Ensure(flow.CurrentStepName == "等待取料到位", "暂停改变了工位当前步骤。");
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(flow.CurrentStepName == "等待取料到位", "复位改变了工位当前步骤。");
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认停机报警会暂停流程、锁存状态，并要求条件恢复后先复位。</summary>
        private static async Task StopAlarmPausesMachineUntilResetAsync()
        {
            var flow = new WaitingStationFlow("检测工位", "等待检测完成");
            var alarms = new MachineAlarmService();
            var runtime = CreateRuntime(flow, alarms);
            var alarm = new MachineAlarmDefinition("TEST_STOP", "回归检查停机报警", MachineAlarmKind.StopMachine);

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await flow.WaitUntilStartedAsync().ConfigureAwait(false);
            alarms.ReportCondition(alarm, "检测工位", true, "模拟检测失败");
            await WaitForStateAsync(runtime, MachineRunState.Alarmed).ConfigureAwait(false);

            Ensure(flow.CurrentStepName == "等待检测完成", "停机报警推进了工位当前步骤。");
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
            alarms.ReportCondition(alarm, "检测工位", false, "故障条件已恢复");
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Ready, "报警恢复并复位后没有回到 Ready 状态。");
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认整机复位会先读轴状态，发现轴仍运动时不下发轴报警复位。</summary>
        private static async Task ResetIsRejectedWhileAxisIsMovingAsync()
        {
            var movingAxis = new FakeAxis("MovingAxis") { IsMoving = true };
            var flow = new WaitingStationFlow("测试工位", "等待测试");
            var runtime = CreateRuntime(flow, new MachineAlarmService(), axis: movingAxis);
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.ResetAsync(CancellationToken.None)).ConfigureAwait(false);

            Ensure(runtime.State == MachineRunState.Alarmed, "轴仍在运动时复位失败，没有保留报警状态。");
            Ensure(movingAxis.AlarmResetCount == 0, "轴仍在运动时 Runtime 仍下发了轴报警复位。");
            movingAxis.IsMoving = false;
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认非停机报警会在条件恢复后自动从报警列表中清除。</summary>
        private static Task NonStopAlarmClearsWhenRecoveredAsync()
        {
            var alarms = new MachineAlarmService();
            var alarm = new MachineAlarmDefinition("TEST_WARNING", "回归检查提示", MachineAlarmKind.NonStop);
            alarms.ReportCondition(alarm, "视觉工位", true, "模拟提示");
            Ensure(alarms.Alarms.Count == 1, "非停机报警触发后没有显示。");
            alarms.ReportCondition(alarm, "视觉工位", false, "条件恢复");
            Ensure(alarms.Alarms.Count == 0, "非停机报警恢复后仍留在报警列表。");
            return Task.CompletedTask;
        }

        /// <summary>同一条件触发多条报警时只发布完整快照，并在条件恢复时保留停机锁存。</summary>
        private static Task BatchedAlarmUpdatesPublishCompleteSnapshotAsync()
        {
            var alarms = new MachineAlarmService();
            var stop = new MachineAlarmDefinition("BATCH_STOP", "批量更新停机报警", MachineAlarmKind.StopMachine);
            var notice = new MachineAlarmDefinition("BATCH_NOTICE", "批量更新非停机报警", MachineAlarmKind.NonStop);
            int changedNotifications = 0;
            int stopNotifications = 0;
            bool subscribersSawWholeBatch = true;
            alarms.StopAlarmRaised += (sender, args) =>
            {
                stopNotifications++;
                MachineAlarmRecord[] snapshot = alarms.Alarms.ToArray();
                subscribersSawWholeBatch &= snapshot.Length == 2
                    && snapshot.Any(item => item.Definition.Code == stop.Code)
                    && snapshot.Any(item => item.Definition.Code == notice.Code);
            };
            alarms.AlarmsChanged += (sender, args) =>
            {
                changedNotifications++;
                MachineAlarmRecord[] snapshot = alarms.Alarms.ToArray();
                subscribersSawWholeBatch &= snapshot.Length == 2
                    && snapshot.Any(item => item.Definition.Code == stop.Code)
                    && snapshot.Any(item => item.Definition.Code == notice.Code);
            };

            alarms.ReportConditions(new[] { stop, notice }, "上料工位", true, "等待 DI 超时");
            Ensure(alarms.Alarms.Count == 2, "批量触发后报警集合没有同时包含全部报警。");
            Ensure(changedNotifications == 1, "批量触发通知了多次，界面可能刷新出部分报警集合。");
            Ensure(stopNotifications == 1, "新的停机报警没有通知整机 Runtime。");
            Ensure(subscribersSawWholeBatch, "报警订阅者读取到的不是完整批量快照。");

            bool conflictRejected = false;
            try
            {
                alarms.ReportConditions(new[]
                {
                    new MachineAlarmDefinition("BATCH_NEW", "不应部分写入", MachineAlarmKind.NonStop),
                    new MachineAlarmDefinition("BATCH_STOP", "冲突定义", MachineAlarmKind.StopMachine)
                }, "上料工位", true, "定义冲突");
            }
            catch (InvalidOperationException)
            {
                conflictRejected = true;
            }
            Ensure(conflictRejected && alarms.Alarms.Count == 2 && changedNotifications == 1,
                "报警定义冲突时批量集合发生了部分写入。");

            alarms.ReportConditions(new[] { notice }, "上料工位", false, "DI 已恢复");
            Ensure(alarms.Alarms.Count == 1 && alarms.Alarms.Single().Definition.Code == stop.Code,
                "恢复非停机条件时错误清除了停机锁存。");
            Ensure(changedNotifications == 2, "非停机报警恢复没有仅发布一次完整列表变化。");
            return Task.CompletedTask;
        }

        /// <summary>确认未处理的工位异常会让整机报警并保留原始异常原因。</summary>
        private static async Task StationFailureAlarmsMachineAsync()
        {
            var flow = new FailingStationFlow("搬运工位");
            var runtime = CreateRuntime(flow, new MachineAlarmService());
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForStateAsync(runtime, MachineRunState.Alarmed).ConfigureAwait(false);

            Ensure(runtime.LastError != null, "工位异常没有保存到 Runtime 最近错误。");
            Ensure(runtime.LastError.ToString().Contains("测试流程异常"), "Runtime 最近错误丢失了工位异常。");
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认空白软件急停配置不会误报或锁住手动调试，但仍不能启动自动流程。</summary>
        private static async Task UnconfiguredEmergencyStopAllowsManualDebugOnlyAsync()
        {
            var flow = new WaitingStationFlow("上料工位", "等待上料完成");
            var alarms = new MachineAlarmService();
            var safetyStatus = new FakeSafetyStatus { IsConfigured = false };
            var runtime = CreateRuntime(flow, alarms, safetyStatus);

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            ManualOperationBlockReason blockReason = await runtime
                .GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(blockReason == ManualOperationBlockReason.None,
                "软件急停 DI 未配置时，Runtime 仍然阻止了手动操作。");
            Ensure(alarms.Alarms.Count == 0,
                "软件急停 DI 未配置时，Runtime 错误地产生了急停报警。");

            bool manualOperationCalled = false;
            ManualOperationBlockReason operationReason = await runtime.RunManualOperationAsync(
                () =>
                {
                    manualOperationCalled = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
            Ensure(operationReason == ManualOperationBlockReason.None && manualOperationCalled,
                "软件急停 DI 未配置时，普通手动操作没有执行。");

            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Ready,
                "软件急停 DI 未配置时，自动启动失败错误地将手动调试 Runtime 置为报警。");
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认手动操作会实时读取急停输入，且释放后仍须复位急停报警。</summary>
        private static async Task EmergencyStopBlocksManualOperationsAsync()
        {
            var flow = new WaitingStationFlow("上料工位", "等待上料完成");
            var safetyStatus = new FakeSafetyStatus { EmergencyStopActive = true };
            var runtime = CreateRuntime(flow, new MachineAlarmService(), safetyStatus);

            ManualOperationBlockReason disconnectedReason = await runtime
                .GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(disconnectedReason == ManualOperationBlockReason.MotionCardDisconnected,
                "运动卡未连接时，手动联锁没有返回未连接原因。");

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            ManualOperationBlockReason activeReason = await runtime
                .GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(activeReason == ManualOperationBlockReason.EmergencyStopInputActive,
                "手动联锁没有返回急停输入有效原因。");
            Ensure(runtime.State == MachineRunState.Alarmed, "手动联锁读取到急停有效后没有锁存整机报警。");
            bool stopOperationCalled = false;
            ManualOperationBlockReason stopReason = await runtime.RunManualStopAsync(
                () =>
                {
                    stopOperationCalled = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
            Ensure(stopReason == ManualOperationBlockReason.None && stopOperationCalled,
                "急停输入有效时，停止专用入口没有允许受控停止。");

            safetyStatus.EmergencyStopActive = false;
            ManualOperationBlockReason latchedReason = await runtime
                .GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(latchedReason == ManualOperationBlockReason.EmergencyStopAlarmNotReset,
                "急停释放后，手动联锁没有要求先复位报警。");

            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            ManualOperationBlockReason readyReason = await runtime
                .GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(readyReason == ManualOperationBlockReason.None, "复位后手动联锁仍阻止操作。");

            var manualOperationStarted = new TaskCompletionSource<bool>();
            var releaseManualOperation = new TaskCompletionSource<bool>();
            Task<ManualOperationBlockReason> manualOperationTask = runtime.RunManualOperationAsync(
                async () =>
                {
                    manualOperationStarted.TrySetResult(true);
                    await releaseManualOperation.Task.ConfigureAwait(false);
                },
                CancellationToken.None);
            await manualOperationStarted.Task.ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.ResetAsync(CancellationToken.None)).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.DisconnectAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Ready,
                "手动操作期间拒绝整机命令后，Runtime 错误地改变了设备状态。");
            releaseManualOperation.TrySetResult(true);
            ManualOperationBlockReason operationResult = await manualOperationTask.ConfigureAwait(false);
            Ensure(operationResult == ManualOperationBlockReason.None, "通过联锁的手动操作没有正常完成。");

            safetyStatus.ReadError = new InvalidOperationException("模拟急停状态读取失败");
            ManualOperationBlockReason readErrorReason = await runtime
                .GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(readErrorReason == ManualOperationBlockReason.EmergencyStopStatusUnavailable,
                "急停读取失败时，手动联锁没有返回读取失败原因。");
            Ensure(runtime.State == MachineRunState.Alarmed, "急停读取失败后没有锁存整机报警。");
            bool readErrorSavedInAlarm = false;
            foreach (var alarm in runtime.AlarmService.Alarms)
            {
                if (string.Equals(alarm.Definition.Code, "EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(alarm.Detail)
                    && alarm.Detail.Contains("模拟急停状态读取失败"))
                {
                    readErrorSavedInAlarm = true;
                    break;
                }
            }

            Ensure(readErrorSavedInAlarm, "急停状态读取异常没有保存在报警详情中。");

            safetyStatus.ReadError = null;
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForStateAsync(runtime, MachineRunState.Running).ConfigureAwait(false);
            ManualOperationBlockReason runningReason = await runtime
                .GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(runningReason == ManualOperationBlockReason.AutomaticFlowActive,
                "自动流程期间，手动联锁没有返回自动运行原因。");
            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>暂停超时的旧工位退出前拒绝手动动作；受控停止仍可用，收尾后恢复手动准入。</summary>
        private static async Task UnsettledStationBlocksManualMotionButAllowsStopAsync()
        {
            var flow = new DelayedStopStationFlow();
            var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", new FakeAxis("X")) }),
                new IStationFlow[] { flow }, new MachineAlarmService(), new FakeSafetyStatus(), pauseTimeout: TimeSpan.FromMilliseconds(100));
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await flow.WaitUntilStartedAsync().ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(() => runtime.PauseAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Alarmed, "暂停超时没有保留报警状态。");
            Ensure(await runtime.GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(false)
                == ManualOperationBlockReason.AutomaticFlowUnsettled, "旧工位未退出时没有报告收尾未确认。");
            bool manualCalled = false;
            ManualOperationBlockReason manual = await runtime.RunManualOperationAsync(() => { manualCalled = true; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
            Ensure(manual == ManualOperationBlockReason.AutomaticFlowUnsettled && !manualCalled, "旧工位未退出时错误允许手动运动或输出。");
            bool recoveryCalled = false;
            ManualOperationBlockReason recovery = await runtime.RunManualRecoveryOperationAsync(() => { recoveryCalled = true; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
            Ensure(recovery == ManualOperationBlockReason.AutomaticFlowUnsettled && !recoveryCalled, "旧工位未退出时错误允许手动恢复操作。");
            bool stopCalled = false;
            ManualOperationBlockReason stop = await runtime.RunManualStopAsync(() => { stopCalled = true; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
            Ensure(stop == ManualOperationBlockReason.None && stopCalled, "旧工位未退出时错误阻止受控停止。");
            flow.Release();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (await runtime.GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(false) != ManualOperationBlockReason.None)
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("旧工位退出后没有恢复手动准入。");
                await Task.Delay(10).ConfigureAwait(false);
            }
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认急停状态未恢复时不能复位或启动，运行期间触发会停机报警。</summary>
        private static async Task EmergencyStopBlocksAndStopsMachineAsync()
        {
            var flow = new WaitingStationFlow("上料工位", "等待上料完成");
            var safetyStatus = new FakeSafetyStatus { EmergencyStopActive = true };
            var runtime = CreateRuntime(flow, new MachineAlarmService(), safetyStatus);
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await ExpectExceptionAsync<InvalidOperationException>(
                () => runtime.ResetAsync(CancellationToken.None)).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Alarmed, "急停有效时 Runtime 没有进入报警状态。");

            safetyStatus.EmergencyStopActive = false;
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await flow.WaitUntilStartedAsync().ConfigureAwait(false);
            safetyStatus.EmergencyStopActive = true;
            await WaitForStateAsync(runtime, MachineRunState.Alarmed).ConfigureAwait(false);
            Ensure(flow.CurrentStepName == "等待上料完成", "急停触发后工位当前步骤发生变化。");

            safetyStatus.EmergencyStopActive = false;
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Ready, "释放急停并复位后没有回到 Ready 状态。");
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认急停状态读取器只读取配置指定的逻辑 DI 名称。</summary>
        private static async Task EmergencyStopStatusUsesConfiguredInputAsync()
        {
            var logicalIo = new FakeLogicalIo();
            logicalIo.SetInput("EmergencyStop", true);
            logicalIo.SetInput("OtherInput", false);
            var safetyStatus = new LogicalIoEmergencyStopStatus(logicalIo, "EmergencyStop");

            var active = await safetyStatus.ReadEmergencyStopActiveAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(active, "急停逻辑输入有效时，安全状态读取器返回了 false。");
            Ensure(logicalIo.LastReadInputName == "EmergencyStop", "安全状态读取器没有使用配置的急停 DI 名称。");
        }

        /// <summary>模拟现场按钮 DI，验证上升沿、复位后启动、停止暂停和读取故障恢复。</summary>
        private static async Task ControlInputsResumeAfterFailedDisconnectAsync()
        {
            var io = new FakeLogicalIo();
            io.SetInput("现场复位", true);
            var axis = new FakeAxis("X");
            var card = new FakeMotionCard("Card", axis);
            var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[0], new MachineAlarmService(), new FakeSafetyStatus(),
                safetyPollInterval: TimeSpan.FromMilliseconds(10), controlInputIo: io, resetInputName: "现场复位");
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            axis.IsMoving = true;
            try
            {
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.DisconnectAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(card.IsConnected && runtime.State == MachineRunState.Alarmed, "断卡失败后应保留连接并进入报警。");
                axis.IsMoving = false;
                await Task.Delay(100).ConfigureAwait(false);
                Ensure(runtime.State == MachineRunState.Alarmed, "监控恢复时不应把已有效的复位电平当作新按键。");
                io.SetInput("现场复位", false);
                await Task.Delay(80).ConfigureAwait(false);
                io.SetInput("现场复位", true);
                await WaitForStateAsync(runtime, MachineRunState.Ready).ConfigureAwait(false);
            }
            finally
            {
                axis.IsMoving = false;
                await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static async Task PhysicalButtonInputsFollowSafeSequenceAsync()
        {
            var logicalIo = new FakeLogicalIo();
            logicalIo.SetInput("现场启动", false);
            logicalIo.SetInput("现场停止", false);
            logicalIo.SetInput("现场复位", true);
            var flow = new WaitingStationFlow("按钮测试工位", "等待按钮测试完成");
            var alarms = new MachineAlarmService();
            var card = new FakeMotionCard("ButtonTestCard", new FakeAxis("ButtonTestAxis"));
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { flow },
                alarms,
                new FakeSafetyStatus(),
                pauseTimeout: TimeSpan.FromSeconds(2),
                safetyPollInterval: TimeSpan.FromMilliseconds(10),
                controlInputIo: logicalIo,
                startInputName: "现场启动",
                pauseInputName: "现场停止",
                resetInputName: "现场复位");

            try
            {
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(60).ConfigureAwait(false);
                Ensure(runtime.State == MachineRunState.ResetRequired,
                    "连接时已为有效电平的复位 DI 被误当成新按键，设备自动复位了。");

                logicalIo.SetInput("现场复位", false);
                await Task.Delay(60).ConfigureAwait(false);
                logicalIo.SetInput("现场启动", true);
                await Task.Delay(60).ConfigureAwait(false);
                Ensure(runtime.State == MachineRunState.ResetRequired,
                    "设备尚未复位时，现场启动按钮不应启动整机。");

                logicalIo.SetInput("现场启动", false);
                await Task.Delay(60).ConfigureAwait(false);
                logicalIo.SetInput("现场复位", true);
                await WaitForStateAsync(runtime, MachineRunState.Ready).ConfigureAwait(false);
                await runtime.RunConnectedConfigurationChangeAsync(() =>
                {
                    logicalIo.SetInput("现场启动", true);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(60).ConfigureAwait(false);
                Ensure(runtime.State == MachineRunState.Ready,
                    "映射切换期间输入由无效变有效，监控重启后错误地自动启动。");
                logicalIo.SetInput("现场启动", false);
                await Task.Delay(60).ConfigureAwait(false);
                logicalIo.SetInput("现场启动", true);
                await WaitForStateAsync(runtime, MachineRunState.Running).ConfigureAwait(false);
                await flow.WaitUntilStartedAsync().ConfigureAwait(false);

                logicalIo.SetInput("现场停止", true);
                await WaitForStateAsync(runtime, MachineRunState.Paused).ConfigureAwait(false);
                Ensure(flow.CurrentStepName == "等待按钮测试完成", "现场停止按钮改变了工位当前步骤。");

                logicalIo.SetInput("现场启动", false);
                await Task.Delay(60).ConfigureAwait(false);
                logicalIo.SetInput("现场启动", true);
                await Task.Delay(60).ConfigureAwait(false);
                Ensure(runtime.State == MachineRunState.Paused,
                    "暂停后未复位时，现场启动按钮不应继续运行。");

                logicalIo.SetInput("现场启动", false);
                logicalIo.SetInput("现场复位", false);
                await Task.Delay(60).ConfigureAwait(false);
                logicalIo.SetInput("现场复位", true);
                await WaitForStateAsync(runtime, MachineRunState.Ready).ConfigureAwait(false);
                logicalIo.SetInput("现场启动", true);
                await WaitForStateAsync(runtime, MachineRunState.Running).ConfigureAwait(false);
                await WaitForStartCountAsync(flow, 2).ConfigureAwait(false);

                logicalIo.SetInput("现场启动", false);
                logicalIo.SetInput("现场复位", false);
                logicalIo.SetInput("现场停止", false);
                await Task.Delay(60).ConfigureAwait(false);
                logicalIo.ReadError = new IOException("模拟 DI 通讯中断");
                await WaitForStateAsync(runtime, MachineRunState.Alarmed).ConfigureAwait(false);
                logicalIo.ReadError = null;

                DateTime recoveryDeadline = DateTime.UtcNow.AddSeconds(3);
                while (alarms.Alarms.Any(alarm => alarm.ConditionActive)
                    && DateTime.UtcNow < recoveryDeadline)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }

                Ensure(alarms.HasLatchedStopAlarm,
                    "按钮 DI 读取恢复后，停机报警不应自行解除，必须等待人工复位。");
                Ensure(alarms.Alarms.All(alarm => !alarm.ConditionActive),
                    "按钮 DI 通讯恢复后仍有报警条件被标记为有效。");

                logicalIo.SetInput("现场复位", true);
                await WaitForStateAsync(runtime, MachineRunState.Ready).ConfigureAwait(false);
                Ensure(!alarms.HasLatchedStopAlarm, "DI 故障恢复并按复位后，停机报警仍未清除。");
            }
            finally
            {
                if (runtime.State == MachineRunState.Running || runtime.State == MachineRunState.Pausing)
                {
                    await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (runtime.State != MachineRunState.Disconnected)
                {
                    await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        /// <summary>确认有效配置可通过校验，并逐项检查无效控制卡、轴、IO 和急停映射会被拒绝。</summary>
        private static Task HardwareConfigurationValidationAsync()
        {
            var cards = new[]
            {
                new MotionCardHardwareDefinition
                {
                    ControllerId = "CardA",
                    AdapterType = "ZMotion",
                    ConnectionConfigurationFile = "ZDevelopSimulation.json"
                }
            };
            var axes = new[]
            {
                new AxisHardwareDefinition
                {
                    ControllerId = "CardA",
                    Name = "X",
                    AxisNumber = 0,
                    MinimumPosition = -100,
                    MaximumPosition = 100,
                    SoftLimitEnabled = true,
                    ManualSpeed = 5,
                    ManualAcceleration = 10,
                    ManualDeceleration = 10
                }
            };
            var inputs = new[]
            {
                new DigitalIoHardwareDefinition
                {
                    ControllerId = "CardA",
                    Name = "EmergencyStop",
                    Channel = 3,
                    ActiveHigh = true
                }
            };

            MachineHardwareConfigurationValidator.Validate(cards, axes, inputs, new DigitalIoHardwareDefinition[0], "EmergencyStop");

            axes[0].SoftLimitEnabled = null;
            ExpectConfigurationFailureContaining(
                () => MachineHardwareConfigurationValidator.Validate(
                    cards, axes, inputs, new DigitalIoHardwareDefinition[0], "EmergencyStop"),
                "尚未配置是否启用软限位");
            axes[0].SoftLimitEnabled = true;

            MachineHardwareConfigurationValidator.Validate(
                cards,
                new[]
                {
                    new AxisHardwareDefinition
                    {
                        ControllerId = "CardA",
                        Name = "UnboundedManualAxis",
                        AxisNumber = 1,
                        SoftLimitEnabled = false,
                        ManualSpeed = 5
                    }
                },
                inputs,
                new DigitalIoHardwareDefinition[0],
                "EmergencyStop");

            ExpectConfigurationFailureContaining(
                () => MachineHardwareConfigurationValidator.Validate(
                    new[]
                    {
                        new MotionCardHardwareDefinition
                        {
                            ControllerId = "CardA",
                            AdapterType = "ZMotion",
                            ConnectionConfigurationFile = "..\\Outside.json"
                        }
                    },
                    axes,
                    inputs,
                    new DigitalIoHardwareDefinition[0],
                    "EmergencyStop"),
                "连接配置必须是设备目录内的相对路径");

            ExpectConfigurationFailureContaining(
                () => MachineHardwareConfigurationValidator.Validate(
                    cards,
                    new[]
                    {
                        axes[0],
                        new AxisHardwareDefinition
                        {
                            ControllerId = "CardA",
                            Name = "Y",
                            AxisNumber = 0,
                            MinimumPosition = -100,
                            MaximumPosition = 100,
                            SoftLimitEnabled = true,
                            ManualSpeed = 5,
                            ManualAcceleration = 10,
                            ManualDeceleration = 10
                        }
                    },
                    inputs,
                    new DigitalIoHardwareDefinition[0],
                    "EmergencyStop"),
                "物理轴号重复");

            ExpectConfigurationFailureContaining(
                () => MachineHardwareConfigurationValidator.Validate(
                    cards,
                    new[]
                    {
                        new AxisHardwareDefinition
                        {
                            ControllerId = "MissingCard",
                            Name = "Y",
                            AxisNumber = 1,
                            MinimumPosition = -100,
                            MaximumPosition = 100,
                            SoftLimitEnabled = true,
                            ManualSpeed = 5,
                            ManualAcceleration = 10,
                            ManualDeceleration = 10
                        }
                    },
                    inputs,
                    new DigitalIoHardwareDefinition[0],
                    "EmergencyStop"),
                "关联的控制卡未配置");

            ExpectConfigurationFailureContaining(
                () => MachineHardwareConfigurationValidator.Validate(
                    cards,
                    axes,
                    new[]
                    {
                        inputs[0],
                        new DigitalIoHardwareDefinition
                        {
                            ControllerId = "CardA",
                            Name = "DoorSensor",
                            Channel = 3,
                            ActiveHigh = true
                        }
                    },
                    new DigitalIoHardwareDefinition[0],
                    "EmergencyStop"),
                "物理通道");

            ExpectConfigurationFailureContaining(
                () => MachineHardwareConfigurationValidator.Validate(
                    cards,
                    axes,
                    inputs,
                    new DigitalIoHardwareDefinition[0],
                    "UnmappedEmergencyStop"),
                "必须在数字输入列表中配置一次");

            ExpectConfigurationFailureContaining(
                () => MachineHardwareConfigurationValidator.Validate(
                    cards,
                    new[]
                    {
                        new AxisHardwareDefinition
                        {
                            ControllerId = "CardA",
                            Name = "X",
                            AxisNumber = 0,
                            MinimumPosition = -100,
                            MaximumPosition = 100,
                            SoftLimitEnabled = true,
                            ManualSpeed = 0,
                            ManualAcceleration = 10,
                            ManualDeceleration = 10
                        }
                    },
                    inputs,
                    new DigitalIoHardwareDefinition[0],
                    "EmergencyStop"),
                "手动速度必须是大于 0 的有限数值");
            return Task.CompletedTask;
        }

        /// <summary>确认硬件配置因指定规则失败，并核对错误信息指向了预期问题。</summary>
        private static void ExpectConfigurationFailureContaining(Action action, string expectedMessage)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException error)
            {
                Ensure(
                    error.Message.Contains(expectedMessage),
                    "配置失败原因不匹配，预期包含 '" + expectedMessage + "'，实际为：" + error.Message);
                return;
            }

            throw new InvalidOperationException(
                "硬件配置没有因为预期问题而被拒绝：" + expectedMessage + "。");
        }

        /// <summary>使用单张模拟卡、指定测试轴和工位创建 Runtime。</summary>
        private static MachineRuntime CreateRuntime(
            IStationFlow flow,
            IMachineAlarmService alarms,
            FakeSafetyStatus safetyStatus = null,
            FakeAxis axis = null)
        {
            var card = new FakeMotionCard("TestCard", axis ?? new FakeAxis("TestAxis"));
            return new MachineRuntime(
                new MotionCardGroup(new[] { card }),
                new[] { flow },
                alarms,
                safetyStatus ?? new FakeSafetyStatus(),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10));
        }

        /// <summary>等待 Runtime 进入目标状态，超时后报告实际状态。</summary>
        private static async Task WaitForStateAsync(MachineRuntime runtime, MachineRunState expectedState)
        {
            var timeout = DateTime.UtcNow.AddSeconds(3);
            while (runtime.State != expectedState && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            Ensure(runtime.State == expectedState, "等待状态 " + expectedState + " 超时，实际为 " + runtime.State + "。");
        }

        /// <summary>等待指定工位流程至少启动到预期次数，超时后报告实际次数。</summary>
        private static async Task WaitForStartCountAsync(WaitingStationFlow flow, int expectedStartCount)
        {
            DateTime timeout = DateTime.UtcNow.AddSeconds(3);
            while (flow.StartCount < expectedStartCount && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            Ensure(
                flow.StartCount >= expectedStartCount,
                "等待工位流程启动 " + expectedStartCount + " 次超时，实际为 " + flow.StartCount + " 次。");
        }

        /// <summary>执行一个应同步抛出指定类型异常的操作。</summary>
        private static void ExpectException<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("操作没有抛出预期异常 " + typeof(TException).Name + "。");
        }

        /// <summary>等待异步操作抛出预期异常类型。</summary>
        private static async Task ExpectExceptionAsync<TException>(Func<Task> action) where TException : Exception
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("操作没有抛出预期异常 " + typeof(TException).Name + "。");
        }

        /// <summary>当检查条件不成立时抛出可直接定位的失败信息。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>提供可成功连接或按设置失败的运动卡替身。</summary>
        private sealed class FakeMotionCard : IMotionCard
        {
            /// <summary>保存卡标识和该卡上的逻辑轴。</summary>
            public FakeMotionCard(string controllerId, params IAxis[] axes)
            {
                ControllerId = controllerId;
                Axes = axes;
            }

            public string ControllerId { get; private set; }

            public bool IsConnected { get; private set; }

            public IReadOnlyCollection<IAxis> Axes { get; private set; }

            public Exception ConnectError { get; set; }

            /// <summary>模拟连接成功或抛出预设的连接异常。</summary>
            public Task ConnectAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ConnectError != null)
                {
                    throw ConnectError;
                }

                IsConnected = true;
                return Task.CompletedTask;
            }

            /// <summary>模拟断开运动卡连接。</summary>
            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IsConnected = false;
                return Task.CompletedTask;
            }
        }

        /// <summary>提供 Runtime 状态检查需要的简单轴替身。</summary>
        private sealed class FakeAxis : IAxis
        {
            /// <summary>保存模拟轴的逻辑名称。</summary>
            public FakeAxis(string name)
            {
                Name = name;
            }

            public string Name { get; private set; }

            /// <summary>设置测试轴是否仍处于运动状态。</summary>
            public bool IsMoving { get; set; }

            /// <summary>模拟控制器报告的速度或未知状态。</summary>
            public double ReportedVelocity { get; set; }
            /// <summary>模拟控制器返回空的轴状态。</summary>
            public bool ReturnNullStatus { get; set; }

            /// <summary>记录测试期间收到的轴报警复位次数。</summary>
            public int AlarmResetCount { get; private set; }

            /// <summary>设置测试轴是否带有硬限位恢复后必须回零的标记。</summary>
            public bool RequiresHomingAfterLimitAlarm { get; set; }

            /// <summary>返回已回零、无报警、无活动限位且按测试设置报告运动状态的轴状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReturnNullStatus) return Task.FromResult<AxisStatus>(null);
                return Task.FromResult(new AxisStatus(
                    0,
                    ReportedVelocity,
                    true,
                    IsMoving,
                    true,
                    false,
                    false,
                    false,
                    false,
                    false,
                    null,
                    RequiresHomingAfterLimitAlarm));
            }

            /// <summary>模拟伺服使能。</summary>
            public Task EnableServoAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟伺服关闭。</summary>
            public Task DisableServoAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟轴回零完成。</summary>
            public Task HomeAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟绝对定位完成。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟相对定位完成。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟开始连续点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟停止轴运动。</summary>
            public Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟轴报警复位并记录调用次数。</summary>
            public Task ResetAlarmAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AlarmResetCount++;
                return Task.CompletedTask;
            }
        }

        /// <summary>提供可由回归用例切换的急停输入状态。</summary>
        private sealed class FakeSafetyStatus : IConfigurableMachineSafetyStatus
        {
            /// <summary>控制回归用例模拟急停逻辑 DI 是否已配置；默认模拟有效配置。</summary>
            public bool IsConfigured { get; set; } = true;

            public bool EmergencyStopActive { get; set; }

            /// <summary>模拟急停输入读取失败时返回的异常。</summary>
            public Exception ReadError { get; set; }

            /// <summary>返回回归用例当前设置的急停状态。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReadError != null)
                {
                    throw ReadError;
                }

                return Task.FromResult(EmergencyStopActive);
            }
        }

        /// <summary>保存测试 DI 状态，并记录最近一次读取的逻辑名称。</summary>
        /// <summary>用内存配置替代设备目录，供设备定义检查读取工位配置和硬件映射。</summary>
        private sealed class FakeEquipmentContext : IEquipmentContext
        {
            /// <summary>本次检查使用的工位配置。</summary>
            private readonly DemoStationSetSettings _stationSet;

            /// <summary>本次检查可用的逻辑轴定义。</summary>
            private readonly IReadOnlyList<AxisHardwareDefinition> _axes;

            /// <summary>本次检查可用的逻辑 DI 定义。</summary>
            private readonly IReadOnlyList<DigitalIoHardwareDefinition> _inputs;

            /// <summary>本次检查可用的逻辑 DO 定义。</summary>
            private readonly IReadOnlyList<DigitalIoHardwareDefinition> _outputs;

            /// <summary>本次检查可用的报警定义。</summary>
            private readonly IReadOnlyList<MachineAlarmDefinition> _alarms;

            /// <summary>本次检查使用的逻辑 IO 替身。</summary>
            private readonly FakeLogicalIo _logicalIo = new FakeLogicalIo();

            /// <summary>本次检查使用的报警服务。</summary>
            private readonly MachineAlarmService _alarmService = new MachineAlarmService();

            /// <summary>创建只服务本次检查的设备环境，不读写真实配置文件。</summary>
            internal FakeEquipmentContext(
                DemoStationSetSettings stationSet,
                IReadOnlyList<AxisHardwareDefinition> axes,
                IReadOnlyList<DigitalIoHardwareDefinition> inputs,
                IReadOnlyList<DigitalIoHardwareDefinition> outputs,
                IReadOnlyList<MachineAlarmDefinition> alarms)
            {
                _stationSet = stationSet;
                _axes = axes;
                _inputs = inputs;
                _outputs = outputs;
                _alarms = alarms;
            }

            /// <summary>检查用设备标识。</summary>
            public string EquipmentId
            {
                get { return "EquipmentDefinitionCheck"; }
            }

            /// <summary>检查可用的逻辑轴定义。</summary>
            public IReadOnlyList<AxisHardwareDefinition> AxisDefinitions
            {
                get { return _axes; }
            }

            /// <summary>检查可用的逻辑 DI 定义。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> InputDefinitions
            {
                get { return _inputs; }
            }

            /// <summary>检查可用的逻辑 DO 定义。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> OutputDefinitions
            {
                get { return _outputs; }
            }

            /// <summary>检查使用的逻辑 IO 替身。</summary>
            public ILogicalIo LogicalIo
            {
                get { return _logicalIo; }
            }

            /// <summary>检查使用的报警服务。</summary>
            public IMachineAlarmService AlarmService
            {
                get { return _alarmService; }
            }

            /// <summary>只有并行工位清单算作存在，用于验证设备定义的路径选择。</summary>
            public bool FileExists(string relativePath)
            {
                return string.Equals(relativePath, DemoEquipmentDefinition.StationSetPath, StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>返回本次检查准备的工位配置；其他类型视为检查未准备。</summary>
            public T LoadJson<T>(string relativePath) where T : class
            {
                var document = _stationSet as T;
                if (document == null)
                {
                    throw new InvalidOperationException("检查未准备该设备配置：" + relativePath);
                }

                return document;
            }

            /// <summary>按代码返回检查准备的报警定义。</summary>
            public MachineAlarmDefinition ResolveAlarm(string alarmCode)
            {
                MachineAlarmDefinition match = _alarms.FirstOrDefault(
                    alarm => string.Equals(alarm.Code, alarmCode, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    throw new InvalidOperationException("检查报警库缺少代码：" + alarmCode);
                }

                return match;
            }

            /// <summary>按名称返回一个新的轴替身，检查不关心轴的真实行为。</summary>
            public IAxis GetAxis(string axisName)
            {
                return new FakeAxis(axisName);
            }
        }

        private sealed class FakeLogicalIo : ILogicalIo
        {
            private readonly Dictionary<string, bool> _inputs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            private readonly object _sync = new object();
            private Exception _readError;

            public string LastReadInputName { get; private set; }

            /// <summary>为 DI 读取注入通讯异常，验证监控报警和恢复逻辑。</summary>
            public Exception ReadError
            {
                get { lock (_sync) { return _readError; } }
                set { lock (_sync) { _readError = value; } }
            }

            /// <summary>设置一个测试逻辑 DI 的有效状态。</summary>
            public void SetInput(string inputName, bool value)
            {
                lock (_sync)
                {
                    _inputs[inputName] = value;
                }
            }

            /// <summary>读取测试逻辑 DI，并保存本次读取的名称。</summary>
            public Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool inputValue;
                Exception readError;
                lock (_sync)
                {
                    LastReadInputName = inputName;
                    readError = _readError;
                    inputValue = _inputs[inputName];
                }

                if (readError != null)
                {
                    throw readError;
                }

                return Task.FromResult(inputValue);
            }

            /// <summary>返回测试 DO 的固定关闭状态。</summary>
            public Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(false);
            }

            /// <summary>接受测试 DO 写入但不连接真实硬件。</summary>
            public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }

        /// <summary>在当前步骤等待暂停，便于检查暂停和复位不会推进工艺步骤。</summary>
        private sealed class WaitingStationFlow : IStationFlow
        {
            private readonly TaskCompletionSource<bool> _started = new TaskCompletionSource<bool>();

            /// <summary>记录工位流程已进入执行步骤的次数。</summary>
            private int _startCount;

            /// <summary>创建有固定工位名称和当前步骤的等待流程。</summary>
            public WaitingStationFlow(string stationName, string stepName)
            {
                StationName = stationName;
                CurrentStepName = stepName;
            }

            public string StationName { get; private set; }

            public string CurrentStepName { get; private set; }

            /// <summary>返回工位流程已经启动执行的次数。</summary>
            public int StartCount
            {
                get { return Interlocked.CompareExchange(ref _startCount, 0, 0); }
            }

            /// <summary>通知检查程序流程已开始，再等待 Runtime 发出暂停取消。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _startCount);
                _started.TrySetResult(true);
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            /// <summary>模拟工位复位，不修改当前步骤。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            /// <summary>等待测试确认工位流程已经进入当前步骤。</summary>
            public Task WaitUntilStartedAsync() { return _started.Task; }
        }

        /// <summary>收到取消后等待测试释放，模拟旧工位或驱动延迟退出。</summary>
        private sealed class DelayedStopStationFlow : IStationFlow
        {
            /// <summary>工位已经开始执行的通知。</summary>
            private readonly TaskCompletionSource<bool> _started = new TaskCompletionSource<bool>();
            /// <summary>允许旧工位结束收尾的通知。</summary>
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>();
            /// <summary>测试工位名称。</summary>
            public string StationName { get { return "延迟退出工位"; } }
            /// <summary>测试工位当前步骤。</summary>
            public string CurrentStepName { get { return "等待设备收尾"; } }
            /// <summary>等待工位首次进入当前步骤。</summary>
            public Task WaitUntilStartedAsync() { return _started.Task; }
            /// <summary>允许测试工位处理取消并退出。</summary>
            public void Release() { _release.TrySetResult(true); }
            /// <summary>取消之后故意延迟返回，重现暂停超时窗口。</summary>
            public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                _started.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await _release.Task.ConfigureAwait(false);
                    throw;
                }
            }
            /// <summary>模拟不改变步骤的工位复位。</summary>
            public Task ResetAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        }

        /// <summary>提供可暂停的设备初始化替身，用于验证步骤保留和连接周期行为。</summary>
        private sealed class FakeEquipmentInitializationFlow : IEquipmentInitializationFlow,
            IRecoverableEquipmentInitializationFlow
        {
            private readonly TaskCompletionSource<bool> _started = new TaskCompletionSource<bool>();

            /// <summary>创建处于第一步且尚未完成的测试初始化流程。</summary>
            public FakeEquipmentInitializationFlow()
            {
                CurrentStepName = "检查轴回零条件";
            }

            /// <summary>当前测试初始化步骤名称。</summary>
            public string CurrentStepName { get; private set; }

            /// <summary>测试初始化步骤全部完成后返回 true。</summary>
            public bool IsCompleted { get; private set; }

            /// <summary>控制本次步骤是否结束等待并完成初始化。</summary>
            public bool AllowCompletion { get; set; }

            /// <summary>记录初始化步骤执行次数，包含暂停后重新进入当前步。</summary>
            public int ExecutionCount { get; private set; }

            /// <summary>记录初始化是否运行在启动调用方的同步上下文中。</summary>
            public bool SawSynchronizationContext { get; private set; }

            /// <summary>控制恢复检查是否确认现场仍满足跳过初始化的条件。</summary>
            public bool CanSkipAfterRestart { get; set; }

            /// <summary>记录设备流程重新检查现场状态的次数。</summary>
            public int RecoveryCheckCount { get; private set; }

            /// <summary>步骤完成时执行的测试状态更新。</summary>
            public Action OnComplete { get; set; }

            /// <summary>恢复检查通过时执行的测试状态更新。</summary>
            public Action OnRecoveryCheck { get; set; }

            /// <summary>等待测试确认初始化流程已经开始执行。</summary>
            public Task WaitUntilStartedAsync()
            {
                return _started.Task;
            }

            /// <summary>模拟重启后读取硬件状态并决定是否可以跳过初始化动作。</summary>
            public Task<bool> CanSkipAfterRestartAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecoveryCheckCount++;
                if (CanSkipAfterRestart && OnRecoveryCheck != null)
                {
                    OnRecoveryCheck();
                }

                return Task.FromResult(CanSkipAfterRestart);
            }

            /// <summary>等待测试许可时保持当前步骤，收到暂停取消后从同一步继续。</summary>
            public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                SawSynchronizationContext = SynchronizationContext.Current != null;
                ExecutionCount++;
                _started.TrySetResult(true);
                if (!AllowCompletion)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (OnComplete != null)
                {
                    OnComplete();
                }

                CurrentStepName = "初始化完成";
                IsCompleted = true;
            }

            /// <summary>模拟复位初始化报警并保留当前步骤和完成状态。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            /// <summary>新连接周期将测试初始化状态恢复到第一步。</summary>
            public void ResetForNewConnection()
            {
                CurrentStepName = "检查轴回零条件";
                IsCompleted = false;
            }
        }

        /// <summary>立即抛出异常的测试工位。</summary>
        private sealed class FailingStationFlow : IStationFlow
        {
            /// <summary>创建带有明确名称的失败工位。</summary>
            public FailingStationFlow(string stationName)
            {
                StationName = stationName;
            }

            public string StationName { get; private set; }

            public string CurrentStepName { get { return "模拟失败步骤"; } }

            /// <summary>返回用于触发 Runtime 故障处理的异常任务。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("测试流程异常");
            }

            /// <summary>模拟清除工位内部错误状态。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }
    }
}
