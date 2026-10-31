using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Equipment.Demo
{
    /// <summary>按逻辑轴、逻辑 DI 和逻辑 DO 执行一次运动、检测及输出，再等待传感器复位。</summary>
    public sealed class DemoStationFlow : IStationFlow, IRecoverableStationFlow, IStationCycleStatistics,
        IStationInputWaitState, IStationAxisRequirements
    {
        /// <summary>示例工位兼容旧配置时使用的默认超时报警代码。</summary>
        public const string SensorTimeoutAlarmCode = DemoStationSettings.DefaultTimeoutAlarmCode;

        /// <summary>未接入设备报警库的直接构造仍使用的默认停机报警。</summary>
        private static readonly MachineAlarmDefinition DefaultSensorTimeoutAlarm = new MachineAlarmDefinition(
            SensorTimeoutAlarmCode, "等待传感器超时", MachineAlarmKind.StopMachine);

        /// <summary>执行定位的逻辑轴，不依赖物理轴号和厂商 API。</summary>
        private readonly IAxis _axis;

        /// <summary>使用映射后的逻辑 DI/DO 读写入口。</summary>
        private readonly ILogicalIo _io;

        /// <summary>向 Runtime 报告可复位的工位超时报警。</summary>
        private readonly IMachineAlarmService _alarms;

        /// <summary>本设备配置的传感器超时报警定义，按设置中的代码顺序保存。</summary>
        private readonly IReadOnlyList<MachineAlarmDefinition> _sensorTimeoutAlarms;

        /// <summary>本工位在当前设备连接周期内使用的参数。</summary>
        private readonly DemoStationSettings _settings;

        /// <summary>当前流程步骤编号；编号是明确跳转目标，不代表步骤数组下标。</summary>
        private int _step = 10;

        /// <summary>当前步骤的中文说明，与步骤编号一起供运行状态和报警定位使用。</summary>
        private string _stepInfo = "正在初始化流程步骤";

        /// <summary>保护步骤编号和说明的一致读取与更新。</summary>
        private readonly object _stepSync = new object();

        /// <summary>测量从一轮定位开始到传感器复位的完整循环时间。</summary>
        private readonly Stopwatch _cycleStopwatch = new Stopwatch();

        /// <summary>本次 Runtime 生命周期内完成的循环数。</summary>
        private long _completedCycles;

        /// <summary>最近一次完整循环的有效测量时长。</summary>
        private TimeSpan? _lastCycleDuration;

        /// <summary>检查依赖与参数，并从定位步骤开始。</summary>
        public DemoStationFlow(
            IAxis axis,
            ILogicalIo io,
            IMachineAlarmService alarms,
            DemoStationSettings settings)
            : this(axis, io, alarms, settings, DefaultSensorTimeoutAlarm)
        {
        }

        /// <summary>使用设备报警库定义创建示例工位流程。</summary>
        public DemoStationFlow(
            IAxis axis,
            ILogicalIo io,
            IMachineAlarmService alarms,
            DemoStationSettings settings,
            MachineAlarmDefinition sensorTimeoutAlarm)
            : this(axis, io, alarms, settings, new[] { sensorTimeoutAlarm })
        {
        }

        /// <summary>使用设备报警库中的一条或多条定义创建示例工位流程。</summary>
        public DemoStationFlow(
            IAxis axis,
            ILogicalIo io,
            IMachineAlarmService alarms,
            DemoStationSettings settings,
            IEnumerable<MachineAlarmDefinition> sensorTimeoutAlarms)
        {
            _axis = axis ?? throw new ArgumentNullException(nameof(axis));
            _io = io ?? throw new ArgumentNullException(nameof(io));
            _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _settings.Validate();
            if (sensorTimeoutAlarms == null)
            {
                throw new ArgumentNullException(nameof(sensorTimeoutAlarms));
            }

            var definitionsByCode = new Dictionary<string, MachineAlarmDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (MachineAlarmDefinition definition in sensorTimeoutAlarms)
            {
                if (definition == null)
                {
                    throw new InvalidOperationException("示例工位超时报警定义不能包含空项。");
                }
                if (definitionsByCode.ContainsKey(definition.Code))
                {
                    throw new InvalidOperationException("示例工位超时报警定义重复：" + definition.Code + "。");
                }
                definitionsByCode.Add(definition.Code, definition);
            }

            var orderedDefinitions = new List<MachineAlarmDefinition>();
            foreach (string code in _settings.TimeoutAlarmCodes)
            {
                MachineAlarmDefinition definition;
                if (!definitionsByCode.TryGetValue(code, out definition))
                {
                    throw new InvalidOperationException("示例工位缺少已关联的报警定义：" + code + "。");
                }
                orderedDefinitions.Add(definition);
            }
            if (definitionsByCode.Count != orderedDefinitions.Count)
            {
                throw new InvalidOperationException("传入的示例工位报警定义与配置关联代码不一致。");
            }
            _sensorTimeoutAlarms = orderedDefinitions.AsReadOnly();

            if (!string.Equals(_axis.Name, _settings.AxisName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("示例工位逻辑轴与配置不一致：" + _settings.AxisName + "。");
            }

            SetStep(10, GetAxisPositionDescription());
        }

        /// <summary>返回该设备中的示例工位名称。</summary>
        public string StationName { get { return _settings.StationName; } }

        /// <summary>返回当前 Runtime 生命周期内的完整循环数。</summary>
        public long CompletedCycles
        {
            get { lock (_stepSync) { return _completedCycles; } }
        }

        /// <summary>返回最近一个完整循环的测量时长。</summary>
        public TimeSpan? LastCycleDuration
        {
            get { lock (_stepSync) { return _lastCycleDuration; } }
        }

        /// <summary>返回操作员能直接理解的当前工位动作。</summary>
        public string CurrentStepName
        {
            get
            {
                lock (_stepSync)
                {
                    return _step + " - " + _stepInfo;
                }
            }
        }

        /// <summary>返回当前等待的逻辑 DI，供操作员从流程步骤直接定位到物理输入。</summary>
        public string WaitingInputName
        {
            get
            {
                lock (_stepSync)
                {
                    return _step == 20 || _step == 40 ? _settings.SensorInputName : null;
                }
            }
        }

        /// <summary>绝对定位工位启动前必须完成回零的逻辑轴。</summary>
        public IReadOnlyCollection<string> RequiredHomedAxes
        {
            get { return new[] { _settings.AxisName }; }
        }

        /// <summary>返回最近一个完整流程步骤边界的编号。</summary>
        public int RecoveryStepNumber
        {
            get
            {
                lock (_stepSync)
                {
                    return _step;
                }
            }
        }

        /// <summary>只允许恢复到当前示例工位明确实现的步骤编号。</summary>
        public bool CanRestoreStep(int stepNumber)
        {
            return stepNumber == 10 || stepNumber == 20 || stepNumber == 30 || stepNumber == 40;
        }

        /// <summary>恢复到步骤起点，不触发运动或 IO，启动后由流程正常执行该步骤。</summary>
        public void RestoreStep(int stepNumber)
        {
            if (!CanRestoreStep(stepNumber))
            {
                throw new ArgumentOutOfRangeException(nameof(stepNumber), "示例工位不支持恢复到该步骤。");
            }

            lock (_stepSync)
            {
                _cycleStopwatch.Reset();
            }

            switch (stepNumber)
            {
                case 10:
                    SetStep(10, GetAxisPositionDescription());
                    break;
                case 20:
                    SetStep(20, "等待传感器 " + _settings.SensorInputName);
                    break;
                case 30:
                    SetStep(30, "触发输出 " + _settings.OutputName);
                    break;
                case 40:
                    SetStep(40, "等待传感器复位 " + _settings.SensorInputName);
                    break;
            }
        }

        /// <summary>按当前步骤编号分派一次流程动作；暂停取消时保留当前步骤和最近完整周期。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentStep;
            lock (_stepSync)
            {
                currentStep = _step;
            }

            try
            {
                switch (currentStep)
                {
                    case 10:
                        await MoveAxisAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    case 20:
                        await WaitForSensorAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    case 30:
                        await PulseOutputAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    case 40:
                        await WaitForSensorReleaseAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    default:
                        throw new InvalidOperationException("示例工位包含未知流程步骤：" + currentStep + "。");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_stepSync)
                {
                    _cycleStopwatch.Reset();
                }

                throw;
            }
        }

        /// <summary>执行绝对定位；同一目标位置可在暂停恢复时安全重发。</summary>
        private async Task MoveAxisAsync(CancellationToken cancellationToken)
        {
            lock (_stepSync)
            {
                if (!_cycleStopwatch.IsRunning)
                {
                    _cycleStopwatch.Restart();
                }
            }

            await _axis.MoveAbsoluteAsync(
                _settings.TargetPosition,
                new AxisMotionProfile(_settings.Speed, _settings.Acceleration, _settings.Deceleration),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SetStep(20, "等待传感器 " + _settings.SensorInputName);
        }

        /// <summary>等待 DI 有效；超时报警后响应整机暂停，清除未完成周期计时并保留当前步骤。</summary>
        private async Task WaitForSensorAsync(CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < _settings.SensorTimeoutMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await _io.ReadInputAsync(_settings.SensorInputName, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ClearRecoveredNonStopAlarms();
                    SetStep(30, "触发输出 " + _settings.OutputName);
                    return;
                }

                await Task.Delay(_settings.SensorPollMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string detail = "逻辑轴 '" + _settings.AxisName + "' 定位后，等待逻辑 DI '"
                + _settings.SensorInputName + "' 超过 " + _settings.SensorTimeoutMilliseconds
                + " 毫秒；请检查传感器及 IO 映射，复位后可从本步骤重试。";
            ReportTimeoutAlarms(detail);

            // 停机报警会同步请求整机暂停；把取消传回流程入口以清除未完成周期计时。
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>同时报告所有关联报警；停机报警作为一次性超时事件锁存，非停机报警等待 DI 恢复。</summary>
        private void ReportTimeoutAlarms(string detail)
        {
            var errors = new List<Exception>();
            try
            {
                ReportConditions(_sensorTimeoutAlarms, true, detail);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            MachineAlarmDefinition[] stopAlarms = _sensorTimeoutAlarms
                .Where(alarm => alarm.Kind == MachineAlarmKind.StopMachine)
                .ToArray();
            if (stopAlarms.Length > 0)
            {
                try
                {
                    // 停机超时保留为已恢复的锁存记录，允许操作员复位并从当前等待步骤重试。
                    ReportConditions(stopAlarms, false, detail);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }

            ThrowAlarmReportingErrors(errors);
        }

        /// <summary>输入满足等待条件后自动清除仍活动的非停机超时报警。</summary>
        private void ClearRecoveredNonStopAlarms()
        {
            string detail = "逻辑 DI '" + _settings.SensorInputName + "' 已恢复为有效状态。";
            MachineAlarmDefinition[] nonStopAlarms = _sensorTimeoutAlarms
                .Where(alarm => alarm.Kind == MachineAlarmKind.NonStop)
                .ToArray();
            if (nonStopAlarms.Length > 0)
            {
                ReportConditions(nonStopAlarms, false, detail);
            }
        }

        /// <summary>优先使用批量服务原子更新；兼容旧报警服务时仍尝试报告整组代码。</summary>
        private void ReportConditions(
            IEnumerable<MachineAlarmDefinition> alarms,
            bool conditionActive,
            string detail)
        {
            var inputContextService = _alarms as IMachineAlarmConditionContextService;
            if (inputContextService != null)
            {
                inputContextService.ReportConditionGroup(alarms, StationName, conditionActive, detail,
                    "sensor-timeout:" + _settings.SensorInputName, _settings.SensorInputName);
                return;
            }
            IMachineAlarmBatchService batchService = _alarms as IMachineAlarmBatchService;
            if (batchService != null)
            {
                batchService.ReportConditions(alarms, StationName, conditionActive, detail);
                return;
            }

            var errors = new List<Exception>();
            foreach (MachineAlarmDefinition alarm in alarms)
            {
                try
                {
                    _alarms.ReportCondition(alarm, StationName, conditionActive, detail);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            ThrowAlarmReportingErrors(errors);
        }

        /// <summary>确保某条报警通知异常不会阻止报告其余关联报警。</summary>
        private static void ThrowAlarmReportingErrors(IList<Exception> errors)
        {
            if (errors.Count == 1)
            {
                throw new InvalidOperationException("更新工位关联报警时发生错误。", errors[0]);
            }
            if (errors.Count > 1)
            {
                throw new AggregateException("更新多条工位关联报警时发生错误。", errors);
            }
        }

        /// <summary>触发一次输出脉冲；无论正常完成还是暂停取消，都会先关闭 DO。</summary>
        private async Task PulseOutputAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _io.WriteOutputAsync(_settings.OutputName, true, cancellationToken).ConfigureAwait(false);
                await Task.Delay(_settings.OutputPulseMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _io.WriteOutputAsync(_settings.OutputName, false, CancellationToken.None).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetStep(40, "等待传感器复位 " + _settings.SensorInputName);
        }

        /// <summary>等待传感器恢复无效；传感器仍有效时留在本动作并继续轮询。</summary>
        private async Task WaitForSensorReleaseAsync(CancellationToken cancellationToken)
        {
            if (!await _io.ReadInputAsync(_settings.SensorInputName, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_stepSync)
                {
                    if (_cycleStopwatch.IsRunning)
                    {
                        _lastCycleDuration = _cycleStopwatch.Elapsed;
                        _cycleStopwatch.Reset();
                    }

                    _completedCycles++;
                }

                SetStep(10, GetAxisPositionDescription());
                return;
            }

            await Task.Delay(_settings.SensorPollMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>设置下一步骤编号和现场说明；不同结果可明确跳转到不同步骤编号。</summary>
        private void SetStep(int step, string info)
        {
            if (step <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(step), "流程步骤编号必须大于零。");
            }

            if (string.IsNullOrWhiteSpace(info))
            {
                throw new ArgumentException("流程步骤说明不能为空。", nameof(info));
            }

            lock (_stepSync)
            {
                _step = step;
                _stepInfo = info;
            }
        }

        /// <summary>明确显示本工位使用的逻辑轴和目标位置，避免多工位步骤文字相同。</summary>
        private string GetAxisPositionDescription()
        {
            string targetPosition = _settings.TargetPosition.ToString("0.###", CultureInfo.InvariantCulture);
            return "逻辑轴 " + _settings.AxisName + " 定位到 " + targetPosition;
        }

        /// <summary>清理本工位的可复位状态；保留当前工艺步骤供暂停或报警后续跑。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

    }
}
