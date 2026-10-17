using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.VendorApi.ZMotion.Native;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>
    /// 正运动 ZDevelop 本机仿真器适配器。当前只接受 127.0.0.1 上的 VPLC5xx-Simu，
    /// 不连接实体控制卡。
    /// </summary>
    public sealed class ZMotionCard : IMotionCard, ILogicalIo
    {
        public const string SimulationControllerType = "VPLC5xx-Simu";

        private const int PositiveLimitBit = 4;
        private const int NegativeLimitBit = 5;
        private const int ServoAlarmBit = 22;
        private const int PollIntervalMilliseconds = 20;
        private const int HomingStartTimeoutMilliseconds = 1000;
        private const int StopConfirmationTimeoutMilliseconds = 5000;
        // 正运动直接回零结果：1 和 2 均由现有正运动参考适配器识别为完成。
        private const uint HomeStatusSucceeded = 1;
        private const uint HomeStatusCompleted = 2;

        private readonly object _nativeLock = new object();
        private readonly string _controllerId;
        private readonly ZMotionCardConfiguration _configuration;
        private readonly ReadOnlyCollection<IAxis> _axes;
        private readonly Dictionary<int, ZMotionAxis> _axesByNumber;
        private readonly Dictionary<string, ZMotionIoPointConfiguration> _inputsByName;
        private readonly Dictionary<string, ZMotionIoPointConfiguration> _outputsByName;
        private IntPtr _handle;
        private bool _axisParametersApplied;

        /// <summary>
        /// 使用连接参数、设备轴参数和设备 IO 映射创建仿真卡；轴和 IO 配置由调用方从设备配置加载。
        /// </summary>
        public ZMotionCard(
            string controllerId,
            ZMotionCardConfiguration configuration,
            IEnumerable<ZMotionAxisConfiguration> axisConfigurations,
            IEnumerable<ZMotionIoPointConfiguration> digitalInputs,
            IEnumerable<ZMotionIoPointConfiguration> digitalOutputs)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (string.IsNullOrWhiteSpace(controllerId) || controllerId != controllerId.Trim())
            {
                throw new ArgumentException("正运动控制卡标识不能为空或包含首尾空格。", nameof(controllerId));
            }

            if (axisConfigurations == null)
            {
                throw new ArgumentNullException(nameof(axisConfigurations));
            }

            if (digitalInputs == null)
            {
                throw new ArgumentNullException(nameof(digitalInputs));
            }

            if (digitalOutputs == null)
            {
                throw new ArgumentNullException(nameof(digitalOutputs));
            }

            configuration.Validate();
            _controllerId = controllerId;
            _configuration = configuration;
            _axesByNumber = new Dictionary<int, ZMotionAxis>();
            _inputsByName = CreateIoPointMap(digitalInputs.ToList(), "输入", controllerId);
            _outputsByName = CreateIoPointMap(digitalOutputs.ToList(), "输出", controllerId);

            var axes = new List<IAxis>();
            var axisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var axisConfiguration in axisConfigurations)
            {
                if (axisConfiguration == null)
                {
                    throw new ArgumentException("正运动轴配置中包含空对象。", nameof(axisConfigurations));
                }

                axisConfiguration.Validate();
                if (!string.Equals(
                    axisConfiguration.ControllerId,
                    controllerId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "逻辑轴 '" + axisConfiguration.Name + "' 属于控制卡 '"
                        + axisConfiguration.ControllerId + "'，不能交给控制卡 '"
                        + controllerId + "'。",
                        nameof(axisConfigurations));
                }

                if (!axisNames.Add(axisConfiguration.Name))
                {
                    throw new ArgumentException("正运动逻辑轴名称重复：" + axisConfiguration.Name + "。", nameof(axisConfigurations));
                }

                if (_axesByNumber.ContainsKey(axisConfiguration.AxisNumber))
                {
                    throw new ArgumentException("正运动物理轴号重复：" + axisConfiguration.AxisNumber + "。", nameof(axisConfigurations));
                }

                var axis = new ZMotionAxis(this, axisConfiguration);
                axes.Add(axis);
                _axesByNumber.Add(axis.AxisNumber, axis);
            }

            if (axes.Count == 0)
            {
                throw new ArgumentException(
                    "控制卡 '" + controllerId + "' 至少需要配置一个逻辑轴。",
                    nameof(axisConfigurations));
            }

            _axes = axes.AsReadOnly();
        }

        public string ControllerId
        {
            get { return _controllerId; }
        }

        public bool IsConnected
        {
            get
            {
                lock (_nativeLock)
                {
                    return _handle != IntPtr.Zero;
                }
            }
        }

        public IReadOnlyCollection<IAxis> Axes
        {
            get { return _axes; }
        }

        public string ActualControllerType { get; private set; }

        public string ActualControllerVersion { get; private set; }

        public string ActualControllerId { get; private set; }

        public bool AxisParametersApplied
        {
            get
            {
                lock (_nativeLock)
                {
                    return _axisParametersApplied;
                }
            }
        }

        /// <summary>异步连接本机仿真器并读取控制器身份。</summary>
        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() => Connect(cancellationToken), cancellationToken);
        }

        /// <summary>异步断开仿真器连接并清除当前控制器信息。</summary>
        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() => Disconnect(cancellationToken), cancellationToken);
        }

        /// <summary>
        /// 显式把样例中的 UNITS 和软限位应用到仿真器。
        /// ConnectAsync 本身只连接并读取身份，不会写入控制器参数。
        /// </summary>
        public Task ApplyAxisParametersAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() => ApplyAxisParameters(cancellationToken), cancellationToken);
        }

        /// <summary>按逻辑名称读取数字输入，并转换为配置的有效电平。</summary>
        public Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken)
        {
            var point = FindIoPoint(_inputsByName, inputName, "输入");
            return Task.Run(() => ReadIoBit(point, true), cancellationToken);
        }

        /// <summary>按逻辑名称读取数字输出，并转换为配置的有效电平。</summary>
        public Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
        {
            var point = FindIoPoint(_outputsByName, outputName, "输出");
            return Task.Run(() => ReadIoBit(point, false), cancellationToken);
        }

        /// <summary>按逻辑名称写入数字输出，并转换为控制卡物理电平。</summary>
        public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
        {
            var point = FindIoPoint(_outputsByName, outputName, "输出");
            return Task.Run(() => WriteIoBit(point, value), cancellationToken);
        }

        /// <summary>异步读取指定逻辑轴的状态。</summary>
        internal Task<AxisStatus> ReadAxisStatusAsync(
            ZMotionAxis axis,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => ReadAxisStatus(axis), cancellationToken);
        }

        /// <summary>使能或关闭指定逻辑轴的伺服驱动。</summary>
        internal Task EnableServoAsync(ZMotionAxis axis, bool enabled, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    EnsureAxisParametersApplied(axis);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetAxisEnable(handle, axis.AxisNumber, enabled ? 1 : 0),
                        enabled ? "使能伺服" : "关闭伺服",
                        axis.Name);
                }
            }, cancellationToken);
        }

        /// <summary>按轴配置下发正运动 DATUM 指令，并等待控制器报告回零完成。</summary>
        internal async Task HomeAsync(ZMotionAxis axis, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    axis.Configuration.ValidateHomeParameters();
                    EnsureAxisParametersApplied(axis);
                    EnsureAxisIdle(handle, axis);
                    EnsureServoEnabled(handle, axis);
                    axis.ClearHomedState();
                    SetHomeProfile(handle, axis);
                    CheckResult(
                        zmcaux.ZAux_Direct_Single_Datum(
                            handle,
                            axis.AxisNumber,
                            axis.Configuration.HomeMode),
                        "启动 DATUM 回零（模式 " + axis.Configuration.HomeMode + "）",
                        axis.Name);
                }
            }, cancellationToken).ConfigureAwait(false);

            try
            {
                await WaitForHomingAsync(axis, cancellationToken).ConfigureAwait(false);
                axis.MarkHomed();
            }
            catch (OperationCanceledException cancellationError)
            {
                await StopHomingAfterFailureAsync(axis, cancellationError).ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException timeoutError)
            {
                await StopHomingAfterFailureAsync(axis, timeoutError).ConfigureAwait(false);
                throw;
            }
            catch (Exception homingError)
            {
                await StopHomingAfterFailureAsync(axis, homingError).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>清除指定逻辑轴的驱动报警并清除回零标记。</summary>
        internal Task ResetAxisAlarmAsync(ZMotionAxis axis, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    CheckResult(
                        zmcaux.ZAux_BusCmd_DriveClear(handle, (uint)axis.AxisNumber, 0),
                        "清除轴报警",
                        axis.Name);
                }
            }, cancellationToken);
        }

        /// <summary>检查运动参数和软限位，然后下发绝对或相对定位命令。</summary>
        internal Task StartMoveAsync(
            ZMotionAxis axis,
            double positionOrDistance,
            AxisMotionProfile profile,
            bool absolute,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    EnsureAxisParametersApplied(axis);
                    EnsureServoEnabled(handle, axis);

                    double targetPosition = positionOrDistance;
                    if (!absolute)
                    {
                        float currentPosition = 0;
                        CheckResult(
                            zmcaux.ZAux_Direct_GetDpos(handle, axis.AxisNumber, ref currentPosition),
                            "读取相对运动起始位置",
                            axis.Name);
                        targetPosition = currentPosition + positionOrDistance;
                    }

                    EnsureTargetWithinLimits(axis, targetPosition);
                    SetMotionProfile(handle, axis, profile.Speed, profile.Acceleration, profile.Deceleration);

                    int result = absolute
                        ? zmcaux.ZAux_Direct_Single_MoveAbs(handle, axis.AxisNumber, (float)positionOrDistance)
                        : zmcaux.ZAux_Direct_Single_Move(handle, axis.AxisNumber, (float)positionOrDistance);
                    CheckResult(result, absolute ? "绝对运动" : "相对运动", axis.Name);
                }
            }, cancellationToken);
        }

        /// <summary>检查点动参数、轴状态后下发连续点动命令。</summary>
        internal Task StartJogAsync(
            ZMotionAxis axis,
            MotionDirection direction,
            double speed,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    EnsureAxisParametersApplied(axis);
                    EnsureServoEnabled(handle, axis);
                    SetMotionProfile(
                        handle,
                        axis,
                        speed,
                        axis.Configuration.JogAcceleration,
                        axis.Configuration.JogDeceleration);
                    CheckResult(
                        zmcaux.ZAux_Direct_Single_Vmove(
                            handle,
                            axis.AxisNumber,
                            direction == MotionDirection.Positive ? 1 : -1),
                        "连续点动",
                        axis.Name);
                }
            }, cancellationToken);
        }

        /// <summary>向指定逻辑轴发送受控停止或中止命令。</summary>
        internal Task StopAxisAsync(
            ZMotionAxis axis,
            AxisStopMode stopMode,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    var nativeMode = stopMode == AxisStopMode.Controlled ? 0 : 2;
                    CheckResult(
                        zmcaux.ZAux_Direct_Single_Cancel(handle, axis.AxisNumber, nativeMode),
                        stopMode == AxisStopMode.Controlled ? "减速停止" : "中止运动指令",
                        axis.Name);
                }
            }, cancellationToken);
        }

        internal int MotionTimeoutMilliseconds
        {
            get { return _configuration.MotionTimeoutMilliseconds; }
        }

        /// <summary>轮询指定逻辑轴，直到确认停止或超过等待时限。</summary>
        internal Task WaitForAxisIdleAsync(ZMotionAxis axis, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                var startedAt = DateTime.UtcNow;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var status = await ReadAxisStatusAsync(axis, cancellationToken).ConfigureAwait(false);
                    if (!status.Moving)
                    {
                        return;
                    }

                    if ((DateTime.UtcNow - startedAt).TotalMilliseconds >= timeoutMilliseconds)
                    {
                        throw new TimeoutException(
                            "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                            + "' 等待停止超时，超时毫秒数：" + timeoutMilliseconds + "。");
                    }

                    await Task.Delay(PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken);
        }

        /// <summary>等待正运动 HOMESTATUS 报告回零完成，或在轴停止后报告回零失败。</summary>
        private async Task WaitForHomingAsync(ZMotionAxis axis, CancellationToken cancellationToken)
        {
            var startedAt = DateTime.UtcNow;
            var observedMotion = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint homeStatus = 0;
                int idle = 1;
                int axisStatus = 0;
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    CheckResult(
                        zmcaux.ZAux_Direct_GetHomeStatus(handle, axis.AxisNumber, ref homeStatus),
                        "读取回零结果",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_GetIfIdle(handle, axis.AxisNumber, ref idle),
                        "读取回零运动状态",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_GetAxisStatus(handle, axis.AxisNumber, ref axisStatus),
                        "读取回零轴状态",
                        axis.Name);
                }

                if (idle != 0
                    && (homeStatus == HomeStatusSucceeded || homeStatus == HomeStatusCompleted))
                {
                    return;
                }

                if ((axisStatus & (1 << ServoAlarmBit)) != 0)
                {
                    throw new InvalidOperationException(
                        "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                        + "' 回零期间出现伺服报警，轴状态字：0x" + axisStatus.ToString("X8") + "。");
                }

                if (idle == 0)
                {
                    observedMotion = true;
                }
                else if (observedMotion)
                {
                    throw new InvalidOperationException(
                        "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                        + "' 已停止，但控制器回零状态不是成功（HOMESTATUS=" + homeStatus + "）。");
                }
                else if ((DateTime.UtcNow - startedAt).TotalMilliseconds >= HomingStartTimeoutMilliseconds)
                {
                    throw new InvalidOperationException(
                        "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                        + "' 没有开始回零运动（HOMESTATUS=" + homeStatus + "）。");
                }

                if ((DateTime.UtcNow - startedAt).TotalMilliseconds >= _configuration.MotionTimeoutMilliseconds)
                {
                    throw new TimeoutException(
                        "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                        + "' 回零超时，超时毫秒数：" + _configuration.MotionTimeoutMilliseconds + "。");
                }

                await Task.Delay(PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>把该轴配置的回零搜索速度、爬行速度和加减速度写入控制器。</summary>
        private void SetHomeProfile(IntPtr handle, ZMotionAxis axis)
        {
            CheckResult(
                zmcaux.ZAux_Direct_SetSpeed(handle, axis.AxisNumber, (float)axis.Configuration.HomeSearchSpeed),
                "设置回零搜索速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetCreep(handle, axis.AxisNumber, (float)axis.Configuration.HomeCreepSpeed),
                "设置回零爬行速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetAccel(handle, axis.AxisNumber, (float)axis.Configuration.HomeAcceleration),
                "设置回零加速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetDecel(handle, axis.AxisNumber, (float)axis.Configuration.HomeDeceleration),
                "设置回零减速度",
                axis.Name);
        }

        /// <summary>回零取消或超时后请求中止，并确认轴停下；停止失败时保留原始故障。</summary>
        private async Task StopHomingAfterFailureAsync(ZMotionAxis axis, Exception originalError)
        {
            try
            {
                await StopAxisAsync(axis, AxisStopMode.Abort, CancellationToken.None).ConfigureAwait(false);
                using (var stopTimeout = new CancellationTokenSource(StopConfirmationTimeoutMilliseconds))
                {
                    await WaitForAxisIdleAsync(
                        axis,
                        StopConfirmationTimeoutMilliseconds,
                        stopTimeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception stopError)
            {
                throw new AggregateException(
                    "正运动逻辑轴 '" + axis.Name + "' 回零失败后未能确认停止。",
                    originalError,
                    stopError);
            }
        }

        internal int StopConfirmationTimeout
        {
            get { return StopConfirmationTimeoutMilliseconds; }
        }

        /// <summary>通过 ZAux_OpenEth 连接配置地址，并拒绝非 ZDevelop 仿真控制器。</summary>
        private void Connect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_nativeLock)
            {
                if (_handle != IntPtr.Zero)
                {
                    return;
                }

                IntPtr openedHandle = IntPtr.Zero;
                try
                {
                    CheckResult(
                        zmcaux.ZAux_OpenEth(_configuration.Address, out openedHandle),
                        "连接 ZDevelop 仿真器",
                        null);
                    if (openedHandle == IntPtr.Zero)
                    {
                        throw new InvalidOperationException(
                            "正运动控制卡 '" + ControllerId + "' 连接成功码返回，但没有取得有效句柄。");
                    }

                    var type = new StringBuilder(256);
                    var version = new StringBuilder(256);
                    var controller = new StringBuilder(256);
                    CheckResult(
                        zmcaux.ZAux_GetControllerInfo(openedHandle, type, version, controller),
                        "读取控制器身份",
                        null);

                    var actualType = type.ToString().Trim();
                    if (!string.Equals(actualType, SimulationControllerType, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "拒绝连接正运动控制器 '" + ControllerId + "'：期望仿真器 '"
                            + SimulationControllerType + "'，实际身份为 '" + actualType + "'。"
                            + "当前适配器仅支持 ZDevelop 仿真，不支持实体控制卡。");
                    }

                    ActualControllerType = actualType;
                    ActualControllerVersion = version.ToString().Trim();
                    ActualControllerId = controller.ToString().Trim();
                    _handle = openedHandle;
                    openedHandle = IntPtr.Zero;
                    _axisParametersApplied = false;
                    foreach (var axis in _axesByNumber.Values)
                    {
                        axis.ClearHomedState();
                    }
                }
                catch (DllNotFoundException error)
                {
                    CloseFailedConnection(openedHandle, error);
                    throw new InvalidOperationException(
                        "正运动原生库或其依赖项未找到。请按厂商 SDK 部署匹配当前进程位数的运行库。",
                        error);
                }
                catch (BadImageFormatException error)
                {
                    CloseFailedConnection(openedHandle, error);
                    throw new InvalidOperationException(
                        "正运动原生库位数与上位机进程不匹配，请核对 SDK DLL 与 Host 的 x86/x64 配置。",
                        error);
                }
                catch (Exception error)
                {
                    CloseFailedConnection(openedHandle, error);
                    throw;
                }
            }
        }

        /// <summary>释放原生连接句柄，并清除控制器身份和轴回零状态。</summary>
        private void Disconnect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_nativeLock)
            {
                if (_handle == IntPtr.Zero)
                {
                    return;
                }

                var handle = _handle;
                CheckResult(zmcaux.ZAux_Close(handle), "断开仿真器", null);
                _handle = IntPtr.Zero;
                _axisParametersApplied = false;
                ActualControllerType = null;
                ActualControllerVersion = null;
                ActualControllerId = null;
                foreach (var axis in _axesByNumber.Values)
                {
                    axis.ClearHomedState();
                }
            }
        }

        /// <summary>在所有仿真轴空闲时应用工程单位和正负软件限位。</summary>
        private void ApplyAxisParameters(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_nativeLock)
            {
                var handle = GetConnectedHandle();
                _axisParametersApplied = false;

                foreach (var axis in _axesByNumber.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureAxisIdle(handle, axis);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetUnits(
                            handle,
                            axis.AxisNumber,
                            (float)axis.Configuration.UnitsPerEngineeringUnit),
                        "应用工程单位换算",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetRsLimit(
                            handle,
                            axis.AxisNumber,
                            (float)axis.Configuration.MinimumPosition),
                        "应用负向软件限位",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetFsLimit(
                            handle,
                            axis.AxisNumber,
                            (float)axis.Configuration.MaximumPosition),
                        "应用正向软件限位",
                        axis.Name);
                }

                _axisParametersApplied = true;
            }
        }

        /// <summary>调用正运动 API 读取轴位置、速度、伺服和状态字。</summary>
        private AxisStatus ReadAxisStatus(ZMotionAxis axis)
        {
            lock (_nativeLock)
            {
                var handle = GetConnectedHandle();
                float actualPosition = 0;
                float velocity = 0;
                int servoEnabled = 0;
                int idle = 1;
                int statusBits = 0;

                CheckResult(
                    zmcaux.ZAux_Direct_GetMpos(handle, axis.AxisNumber, ref actualPosition),
                    "读取反馈位置",
                    axis.Name);
                CheckResult(
                    zmcaux.ZAux_Direct_GetMspeed(handle, axis.AxisNumber, ref velocity),
                    "读取轴速度",
                    axis.Name);
                CheckResult(
                    zmcaux.ZAux_Direct_GetAxisEnable(handle, axis.AxisNumber, ref servoEnabled),
                    "读取伺服使能状态",
                    axis.Name);
                CheckResult(
                    zmcaux.ZAux_Direct_GetIfIdle(handle, axis.AxisNumber, ref idle),
                    "读取轴运动状态",
                    axis.Name);
                CheckResult(
                    zmcaux.ZAux_Direct_GetAxisStatus(handle, axis.AxisNumber, ref statusBits),
                    "读取轴状态字",
                    axis.Name);

                var alarmActive = (statusBits & (1 << ServoAlarmBit)) != 0;
                var alarmCode = alarmActive ? "0x" + statusBits.ToString("X8") : null;
                var alarmMessage = alarmActive ? "正运动轴状态字报告伺服报警。" : null;

                return new AxisStatus(
                    actualPosition,
                    velocity,
                    servoEnabled != 0,
                    idle == 0,
                    axis.IsHomed,
                    (statusBits & (1 << PositiveLimitBit)) != 0,
                    (statusBits & (1 << NegativeLimitBit)) != 0,
                    alarmActive,
                    alarmCode,
                    alarmMessage);
            }
        }

        /// <summary>读取物理 DI 或 DO，并按逻辑 IO 的有效电平转换结果。</summary>
        private bool ReadIoBit(ZMotionIoPointConfiguration point, bool input)
        {
            lock (_nativeLock)
            {
                var handle = GetConnectedHandle();
                uint physicalValue = 0;
                var result = input
                    ? zmcaux.ZAux_Direct_GetIn(handle, point.Channel, ref physicalValue)
                    : zmcaux.ZAux_Direct_GetOp(handle, point.Channel, ref physicalValue);
                CheckIoResult(result, input ? "读取 DI" : "读取 DO", point.Name);

                var physicalHigh = physicalValue != 0;
                return physicalHigh == point.ActiveHigh;
            }
        }

        /// <summary>将逻辑 DO 值转换为物理电平后写入正运动控制卡。</summary>
        private void WriteIoBit(ZMotionIoPointConfiguration point, bool value)
        {
            lock (_nativeLock)
            {
                var handle = GetConnectedHandle();
                var physicalHigh = value == point.ActiveHigh;
                CheckResult(
                    zmcaux.ZAux_Direct_SetOp(handle, point.Channel, physicalHigh ? 1u : 0u),
                    "写入 DO",
                    "逻辑 IO",
                    point.Name);
            }
        }

        /// <summary>阻止在工程单位和软件限位应用前操作逻辑轴。</summary>
        private void EnsureAxisParametersApplied(ZMotionAxis axis)
        {
            if (!_axisParametersApplied)
            {
                throw new InvalidOperationException(
                    "正运动控制卡 '" + ControllerId + "' 尚未应用仿真轴参数，不能操作逻辑轴 '"
                    + axis.Name + "'。请先调用 ApplyAxisParametersAsync。");
            }
        }

        /// <summary>读取控制卡状态并确认指定逻辑轴当前空闲。</summary>
        private void EnsureAxisIdle(IntPtr handle, ZMotionAxis axis)
        {
            int idle = 1;
            CheckResult(
                zmcaux.ZAux_Direct_GetIfIdle(handle, axis.AxisNumber, ref idle),
                "确认轴空闲",
                axis.Name);
            if (idle == 0)
            {
                throw new InvalidOperationException(
                    "正运动逻辑轴 '" + axis.Name + "' 正在运动，不能应用参数或清零坐标。");
            }
        }

        /// <summary>确认指定逻辑轴已使能伺服后才允许下发运动命令。</summary>
        private void EnsureServoEnabled(IntPtr handle, ZMotionAxis axis)
        {
            int enabled = 0;
            CheckResult(
                zmcaux.ZAux_Direct_GetAxisEnable(handle, axis.AxisNumber, ref enabled),
                "确认伺服使能",
                axis.Name);
            if (enabled == 0)
            {
                throw new InvalidOperationException(
                    "正运动逻辑轴 '" + axis.Name + "' 尚未使能，拒绝下发运动命令。");
            }
        }

        /// <summary>检查目标位置是否落在轴配置的仿真软件限位范围内。</summary>
        private void EnsureTargetWithinLimits(ZMotionAxis axis, double targetPosition)
        {
            if (targetPosition < axis.Configuration.MinimumPosition
                || targetPosition > axis.Configuration.MaximumPosition)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetPosition),
                    "逻辑轴 '" + axis.Name + "' 的目标位置 " + targetPosition
                    + " 超出仿真软限位 [" + axis.Configuration.MinimumPosition + ", "
                    + axis.Configuration.MaximumPosition + "]。");
            }
        }

        /// <summary>将速度、加速度和减速度写入指定轴的运动参数。</summary>
        private void SetMotionProfile(
            IntPtr handle,
            ZMotionAxis axis,
            double speed,
            double acceleration,
            double deceleration)
        {
            CheckResult(
                zmcaux.ZAux_Direct_SetSpeed(handle, axis.AxisNumber, (float)speed),
                "设置运动速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetAccel(handle, axis.AxisNumber, (float)acceleration),
                "设置运动加速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetDecel(handle, axis.AxisNumber, (float)deceleration),
                "设置运动减速度",
                axis.Name);
        }

        /// <summary>取得当前连接句柄；未连接时抛出带控制卡标识的异常。</summary>
        private IntPtr GetConnectedHandle()
        {
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("正运动控制卡 '" + ControllerId + "' 尚未连接。");
            }

            return _handle;
        }

        /// <summary>检查轴操作的厂商错误码，并保留控制卡和轴名称。</summary>
        private void CheckResult(int result, string operation, string axisName)
        {
            if (result != 0)
            {
                throw new ZMotionApiException(ControllerId, operation, axisName, result);
            }
        }

        /// <summary>检查通用操作的厂商错误码，并保留操作上下文。</summary>
        private void CheckResult(int result, string operation, string contextType, string contextName)
        {
            if (result != 0)
            {
                throw new ZMotionApiException(ControllerId, operation, contextType, contextName, result);
            }
        }

        /// <summary>检查 IO 操作的厂商错误码，并注明逻辑点位名称。</summary>
        private void CheckIoResult(int result, string operation, string pointName)
        {
            CheckResult(result, operation, "逻辑 IO", pointName);
        }

        /// <summary>按逻辑名称查找已配置 IO 点位，找不到时列出当前可用名称。</summary>
        private ZMotionIoPointConfiguration FindIoPoint(
            IDictionary<string, ZMotionIoPointConfiguration> points,
            string pointName,
            string direction)
        {
            if (string.IsNullOrWhiteSpace(pointName))
            {
                throw new ArgumentException("逻辑" + direction + "名称不能为空。", nameof(pointName));
            }

            ZMotionIoPointConfiguration point;
            if (points.TryGetValue(pointName, out point))
            {
                return point;
            }

            var names = string.Join(", ", points.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            throw new KeyNotFoundException(
                "正运动逻辑" + direction + " '" + pointName + "' 未配置。已配置名称：" + names + "。");
        }

        /// <summary>校验一组逻辑 IO 配置，并按逻辑名称建立查找表。</summary>
        private static Dictionary<string, ZMotionIoPointConfiguration> CreateIoPointMap(
            IReadOnlyCollection<ZMotionIoPointConfiguration> points,
            string direction,
            string controllerId)
        {
            var pointsByName = new Dictionary<string, ZMotionIoPointConfiguration>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in points)
            {
                if (point == null)
                {
                    throw new ArgumentException("正运动逻辑" + direction + "配置中包含空对象。", nameof(points));
                }

                point.Validate(direction);
                if (!string.Equals(point.ControllerId, controllerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "逻辑" + direction + " '" + point.Name + "' 属于控制卡 '"
                        + point.ControllerId + "'，不能交给控制卡 '" + controllerId + "'。",
                        nameof(points));
                }

                if (pointsByName.ContainsKey(point.Name))
                {
                    throw new ArgumentException(
                        "正运动逻辑" + direction + "名称重复：" + point.Name + "。",
                        nameof(points));
                }

                pointsByName.Add(point.Name, point);
            }

            return pointsByName;
        }

        /// <summary>连接或身份校验失败时回收已打开句柄，并保留清理异常。</summary>
        private void CloseFailedConnection(IntPtr handle, Exception originalError)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var closeResult = zmcaux.ZAux_Close(handle);
                if (closeResult != 0)
                {
                    throw new ZMotionApiException(ControllerId, "回收失败的连接句柄", null, closeResult);
                }
            }
            catch (Exception closeError)
            {
                throw new AggregateException(
                    "正运动连接或身份核对失败，且回收连接句柄时也发生错误。",
                    originalError,
                    closeError);
            }
        }
    }
}
