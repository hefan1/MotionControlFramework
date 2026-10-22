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

        /// <summary>执行定位的逻辑轴，不依赖物理轴号和厂商 API。</summary>
        private readonly IAxis _axis;

        /// <summary>使用映射后的逻辑 DI/DO 读写入口。</summary>
        private readonly ILogicalIo _io;

        /// <summary>向 Runtime 报告可复位的工位超时报警。</summary>
        private readonly IMachineAlarmService _alarms;

        /// <summary>本工位在当前设备连接周期内使用的参数。</summary>
        private readonly DemoStationSettings _settings;

        /// <summary>当前流程步骤编号；编号是明确跳转目标，不代表步骤数组下标。</summary>
        private int _step = 10;

        /// <summary>当前步骤的中文说明，与步骤编号一起供运行状态和报警定位使用。</summary>
        private string _stepInfo = "正在初始化流程步骤";

        /// <summary>保护步骤编号和说明的一致读取与更新。</summary>
        private readonly object _stepSync = new object();

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

            SetStep(10, "轴定位到 " + _settings.TargetPosition);
        }

        /// <summary>返回该设备中的示例工位名称。</summary>
        public string StationName { get { return _settings.StationName; } }

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

        /// <summary>按当前步骤编号分派一次流程动作；暂停取消后保留当前步骤。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentStep;
            lock (_stepSync)
            {
                currentStep = _step;
            }

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

        /// <summary>执行绝对定位；同一目标位置可在暂停恢复时安全重发。</summary>
        private async Task MoveAxisAsync(CancellationToken cancellationToken)
        {
            await _axis.MoveAbsoluteAsync(
                _settings.TargetPosition,
                new AxisMotionProfile(_settings.Speed, _settings.Acceleration, _settings.Deceleration),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SetStep(20, "等待传感器 " + _settings.SensorInputName);
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
                    cancellationToken.ThrowIfCancellationRequested();
                    SetStep(30, "触发输出 " + _settings.OutputName);
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
                SetStep(10, "轴定位到 " + _settings.TargetPosition);
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

        /// <summary>清理本工位的可复位状态；保留当前工艺步骤供暂停或报警后续跑。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

    }
}
