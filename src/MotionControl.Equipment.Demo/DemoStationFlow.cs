using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Equipment.Demo
{
    /// <summary>按逻辑轴、逻辑 DI 和逻辑 DO 执行一次运动、检测及输出，再等待传感器复位。</summary>
    public sealed class DemoStationFlow : IStationFlow
    {
        /// <summary>传感器等待超时使用的停机报警，同次超时只保留一条待复位记录。</summary>
        private static readonly MachineAlarmDefinition SensorTimeoutAlarm = new MachineAlarmDefinition(
            "DEMO_SENSOR_TIMEOUT", "等待传感器超时", MachineAlarmKind.StopMachine);

        /// <summary>当前步骤的有限集合，避免流程动作被隐藏在通用编排器中。</summary>
        private enum Step
        {
            MoveAxis,
            WaitSensor,
            PulseOutput,
            WaitSensorRelease
        }

        /// <summary>执行定位的逻辑轴，不依赖物理轴号和厂商 API。</summary>
        private readonly IAxis _axis;

        /// <summary>使用映射后的逻辑 DI/DO 读写入口。</summary>
        private readonly ILogicalIo _io;

        /// <summary>向 Runtime 报告可复位的工位超时报警。</summary>
        private readonly IMachineAlarmService _alarms;

        /// <summary>本工位在当前设备连接周期内使用的参数。</summary>
        private readonly DemoStationSettings _settings;

        /// <summary>供流程页读取的当前步骤；只有当前步骤完成后才改变。</summary>
        private volatile Step _currentStep;

        /// <summary>检查依赖与参数，并从定位步骤开始。</summary>
        public DemoStationFlow(
            IAxis axis,
            ILogicalIo io,
            IMachineAlarmService alarms,
            DemoStationSettings settings)
        {
            _axis = axis ?? throw new ArgumentNullException(nameof(axis));
            _io = io ?? throw new ArgumentNullException(nameof(io));
            _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _settings.Validate();
            if (!string.Equals(_axis.Name, _settings.AxisName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("示例工位逻辑轴与配置不一致：" + _settings.AxisName + "。");
            }

            _currentStep = Step.MoveAxis;
        }

        /// <summary>返回该设备中的示例工位名称。</summary>
        public string StationName { get { return _settings.StationName; } }

        /// <summary>返回操作员能直接理解的当前工位动作。</summary>
        public string CurrentStepName
        {
            get
            {
                switch (_currentStep)
                {
                    case Step.MoveAxis: return "轴定位到 " + _settings.TargetPosition;
                    case Step.WaitSensor: return "等待传感器 " + _settings.SensorInputName;
                    case Step.PulseOutput: return "触发输出 " + _settings.OutputName;
                    default: return "等待传感器复位 " + _settings.SensorInputName;
                }
            }
        }

        /// <summary>每次只执行当前一步；暂停取消保留步骤，输出步骤会先关闭 DO。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            switch (_currentStep)
            {
                case Step.MoveAxis:
                    await _axis.MoveAbsoluteAsync(
                        _settings.TargetPosition,
                        new AxisMotionProfile(_settings.Speed, _settings.Acceleration, _settings.Deceleration),
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    _currentStep = Step.WaitSensor;
                    return;

                case Step.WaitSensor:
                    await WaitForSensorAsync(cancellationToken).ConfigureAwait(false);
                    return;

                case Step.PulseOutput:
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
                    _currentStep = Step.WaitSensorRelease;
                    return;

                case Step.WaitSensorRelease:
                    if (!await _io.ReadInputAsync(_settings.SensorInputName, cancellationToken).ConfigureAwait(false))
                    {
                        _currentStep = Step.MoveAxis;
                        return;
                    }

                    await Task.Delay(_settings.SensorPollMilliseconds, cancellationToken).ConfigureAwait(false);
                    return;

                default:
                    throw new InvalidOperationException("未知示例工位步骤：" + _currentStep + "。");
            }
        }

        /// <summary>在当前启动周期内等待 DI 有效；超时报警锁存，当前步骤留待复位后重试。</summary>
        private async Task WaitForSensorAsync(CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < _settings.SensorTimeoutMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await _io.ReadInputAsync(_settings.SensorInputName, cancellationToken).ConfigureAwait(false))
                {
                    _currentStep = Step.PulseOutput;
                    return;
                }

                await Task.Delay(_settings.SensorPollMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string detail = "逻辑轴 '" + _settings.AxisName + "' 定位后，等待逻辑 DI '"
                + _settings.SensorInputName + "' 超过 " + _settings.SensorTimeoutMilliseconds
                + " 毫秒；请检查传感器及 IO 映射，复位后可从本步骤重试。";
            try
            {
                _alarms.ReportCondition(SensorTimeoutAlarm, StationName, true, detail);
            }
            finally
            {
                // 超时是一次性事件；记录继续锁存，条件结束后允许操作员复位重试。
                _alarms.ReportCondition(SensorTimeoutAlarm, StationName, false, detail);
            }
        }

        /// <summary>清理本工位的可复位状态；保留当前工艺步骤供暂停或报警后续跑。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
