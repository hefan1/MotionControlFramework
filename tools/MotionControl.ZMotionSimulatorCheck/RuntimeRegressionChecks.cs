using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Host.Wpf;

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
            await RunCheckAsync("任一轴仍在运动时禁止启动自动流程", MovingAxisBlocksAutomaticStartAsync, failures);
            await RunCheckAsync("多卡断开先检查所有轴，不留下半连接状态", MovingAxisPreventsPartialCardDisconnectAsync, failures);
            await RunCheckAsync("必须复位后启动，暂停后保留当前步骤", ResetAndPauseKeepCurrentStepAsync, failures);
            await RunCheckAsync("停机报警暂停流程并要求复位", StopAlarmPausesMachineUntilResetAsync, failures);
            await RunCheckAsync("非停机报警在条件恢复后自动清除", NonStopAlarmClearsWhenRecoveredAsync, failures);
            await RunCheckAsync("工位流程异常使整机进入报警", StationFailureAlarmsMachineAsync, failures);
            await RunCheckAsync("手动轴和 DO 操作实时检查急停并等待复位", EmergencyStopBlocksManualOperationsAsync, failures);
            await RunCheckAsync("急停有效时禁止复位和启动，运行期间触发会暂停流程", EmergencyStopBlocksAndStopsMachineAsync, failures);
            await RunCheckAsync("急停状态通过配置的逻辑 DI 读取", EmergencyStopStatusUsesConfiguredInputAsync, failures);
            await RunCheckAsync("设备硬件配置关联和急停输入校验", HardwareConfigurationValidationAsync, failures);
            await RunCheckAsync("正运动轴号超出控制器范围时在连接前被拒绝", ZMotionAxisNumberOutsideControllerRangeIsRejectedAsync, failures);
            return failures.AsReadOnly();
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

        /// <summary>使用单张模拟卡、一个测试轴和指定工位创建 Runtime。</summary>
        private static MachineRuntime CreateRuntime(
            IStationFlow flow,
            IMachineAlarmService alarms,
            FakeSafetyStatus safetyStatus = null)
        {
            var card = new FakeMotionCard("TestCard", new FakeAxis("TestAxis"));
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

            /// <summary>返回已回零、无报警、无活动限位且按测试设置报告运动状态的轴状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new AxisStatus(0, 0, true, IsMoving, true, false, false, false, false, false, null, false));
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
            public Task StartJogAsync(MotionDirection direction, double speed, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟停止轴运动。</summary>
            public Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟轴报警复位。</summary>
            public Task ResetAlarmAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        }

        /// <summary>提供可由回归用例切换的急停输入状态。</summary>
        private sealed class FakeSafetyStatus : IMachineSafetyStatus
        {
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
        private sealed class FakeLogicalIo : ILogicalIo
        {
            private readonly Dictionary<string, bool> _inputs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            public string LastReadInputName { get; private set; }

            /// <summary>设置一个测试逻辑 DI 的有效状态。</summary>
            public void SetInput(string inputName, bool value)
            {
                _inputs[inputName] = value;
            }

            /// <summary>读取测试逻辑 DI，并保存本次读取的名称。</summary>
            public Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastReadInputName = inputName;
                return Task.FromResult(_inputs[inputName]);
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

            /// <summary>创建有固定工位名称和当前步骤的等待流程。</summary>
            public WaitingStationFlow(string stationName, string stepName)
            {
                StationName = stationName;
                CurrentStepName = stepName;
            }

            public string StationName { get; private set; }

            public string CurrentStepName { get; private set; }

            /// <summary>通知检查程序流程已开始，再等待 Runtime 发出暂停取消。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
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
