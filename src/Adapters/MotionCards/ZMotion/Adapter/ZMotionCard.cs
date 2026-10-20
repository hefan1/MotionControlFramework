using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
    public sealed partial class ZMotionCard : IMotionCard, ILogicalIo, IMotionCardIoCapabilityProvider
    {
        /// <summary>读取正运动控制器的单个浮点轴参数。</summary>
        private delegate int AxisFloatParameterReader(IntPtr handle, int axisNumber, ref float value);

        /// <summary>当前适配器允许连接的 ZDevelop 仿真控制器型号。</summary>
        public const string SimulationControllerType = "VPLC5xx-Simu";

        /// <summary>AXISSTATUS 第 2 位，表示总线轴通讯错误。</summary>
        private const int RemoteAxisCommunicationErrorBit = 2;
        /// <summary>AXISSTATUS 第 3 位，表示总线伺服驱动器报警。</summary>
        private const int RemoteDriveErrorBit = 3;
        /// <summary>AXISSTATUS 第 4 位，表示正向硬限位触发。</summary>
        private const int PositiveLimitBit = 4;
        /// <summary>AXISSTATUS 第 5 位，表示负向硬限位触发。</summary>
        private const int NegativeLimitBit = 5;
        /// <summary>AXISSTATUS 第 8 位，表示跟随误差超限。</summary>
        private const int FollowingErrorBit = 8;
        /// <summary>AXISSTATUS 第 9 位，表示正向软限位触发。</summary>
        private const int PositiveSoftLimitBit = 9;
        /// <summary>AXISSTATUS 第 10 位，表示负向软限位触发。</summary>
        private const int NegativeSoftLimitBit = 10;
        /// <summary>AXISSTATUS 第 18 位，表示控制器电源异常。</summary>
        private const int PowerAbnormalBit = 18;
        /// <summary>AXISSTATUS 第 20 位，表示轴速度保护触发。</summary>
        private const int AxisSpeedProtectionBit = 20;
        /// <summary>AXISSTATUS 第 21 位，表示运动指令或运动配置异常。</summary>
        private const int SpecialMotionCommandErrorBit = 21;
        /// <summary>AXISSTATUS 第 22 位，表示轴报警输入有效。</summary>
        private const int AlarmSignalInputBit = 22;
        /// <summary>正向硬限位状态对应的 AXISSTATUS 位掩码。</summary>
        private const int PositiveHardLimitMask = 1 << PositiveLimitBit;
        /// <summary>负向硬限位状态对应的 AXISSTATUS 位掩码。</summary>
        private const int NegativeHardLimitMask = 1 << NegativeLimitBit;
        /// <summary>正向软限位状态对应的 AXISSTATUS 位掩码。</summary>
        private const int PositiveSoftLimitMask = 1 << PositiveSoftLimitBit;
        /// <summary>负向软限位状态对应的 AXISSTATUS 位掩码。</summary>
        private const int NegativeSoftLimitMask = 1 << NegativeSoftLimitBit;
        /// <summary>读取轴状态时需要检查的硬限位和软限位位掩码。</summary>
        private const int ActiveLimitMask = PositiveHardLimitMask
            | NegativeHardLimitMask
            | PositiveSoftLimitMask
            | NegativeSoftLimitMask;
        /// <summary>读取轴状态时用于判断报警的 AXISSTATUS 位掩码。</summary>
        private const int ActiveAlarmStatusMask = (1 << RemoteAxisCommunicationErrorBit)
            | (1 << RemoteDriveErrorBit)
            | PositiveHardLimitMask
            | NegativeHardLimitMask
            | (1 << FollowingErrorBit)
            | PositiveSoftLimitMask
            | NegativeSoftLimitMask
            | (1 << PowerAbnormalBit)
            | (1 << AxisSpeedProtectionBit)
            | (1 << SpecialMotionCommandErrorBit)
            | (1 << AlarmSignalInputBit);
        /// <summary>回零过程中需要判定为失败的通讯、驱动和报警输入位掩码。</summary>
        private const int HomingFailureStatusMask = (1 << RemoteAxisCommunicationErrorBit)
            | (1 << RemoteDriveErrorBit)
            | (1 << AlarmSignalInputBit);
        /// <summary>总线驱动器报警对应的 AXISSTATUS 位掩码。</summary>
        private const int BusDriveAlarmMask = 1 << RemoteDriveErrorBit;
        /// <summary>外部报警输入对应的 AXISSTATUS 位掩码。</summary>
        private const int AlarmSignalInputMask = 1 << AlarmSignalInputBit;
        /// <summary>控制器历史停止原因中的正负硬限位位掩码。</summary>
        private const int HardLimitStopReasonMask = PositiveHardLimitMask | NegativeHardLimitMask;
        /// <summary>当前仿真器使用的驱动报警清除模式编号。</summary>
        private const uint CurrentDriveAlarmClearMode = 0;
        /// <summary>当前仿真适配器点动时使用的默认加速度，沿用原仿真样例值。</summary>
        private const double DefaultJogAcceleration = 10;
        /// <summary>当前仿真适配器点动时使用的默认减速度，沿用原仿真样例值。</summary>
        private const double DefaultJogDeceleration = 10;
        /// <summary>清除驱动器报警后等待控制器更新状态的时间，单位为毫秒。</summary>
        private const int DriveAlarmResetDelayMilliseconds = 50;
        /// <summary>读取控制器轴状态时两次轮询之间的间隔，单位为毫秒。</summary>
        private const int PollIntervalMilliseconds = 20;
        /// <summary>发送回零命令后等待控制器进入回零状态的最长时间，单位为毫秒。</summary>
        private const int HomingStartTimeoutMilliseconds = 1000;
        /// <summary>发送停止命令后等待控制器确认轴空闲的最长时间，单位为毫秒。</summary>
        private const int StopConfirmationTimeoutMilliseconds = 5000;
        /// <summary>正运动 DATUM 返回的成功状态编号。</summary>
        private const uint HomeStatusSucceeded = 1;
        /// <summary>正运动 DATUM 返回的完成状态编号。</summary>
        private const uint HomeStatusCompleted = 2;

        /// <summary>保护原生句柄访问，避免连接、断开与 API 调用并发冲突。</summary>
        private readonly object _nativeLock = new object();
        /// <summary>设备配置中用于标识当前控制卡的名称。</summary>
        private readonly string _controllerId;
        /// <summary>当前仿真连接地址和运动等待超时配置。</summary>
        private readonly ZMotionCardConfiguration _configuration;
        /// <summary>当前控制卡创建的逻辑轴只读列表。</summary>
        private readonly ReadOnlyCollection<IAxis> _axes;
        /// <summary>按物理轴号查找正运动轴对象。</summary>
        private readonly Dictionary<int, ZMotionAxis> _axesByNumber;
        /// <summary>按逻辑名称查找数字输入映射；在原生锁内整体替换，避免读到半更新配置。</summary>
        private Dictionary<string, ZMotionIoPointConfiguration> _inputsByName;
        /// <summary>按逻辑名称查找数字输出映射；在原生锁内整体替换，避免读到半更新配置。</summary>
        private Dictionary<string, ZMotionIoPointConfiguration> _outputsByName;
        /// <summary>正运动 SDK 返回的原生连接句柄；零值表示未连接。</summary>
        private IntPtr _handle;
        /// <summary>当前轴参数是否已成功写入控制器。</summary>
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

            _axes = axes.AsReadOnly();
        }

        /// <summary>设备配置中分配给当前运动卡的唯一标识。</summary>
        public string ControllerId
        {
            get { return _controllerId; }
        }

        /// <summary>原生连接句柄有效时为 true。</summary>
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

        /// <summary>当前运动卡创建的逻辑轴列表。</summary>
        public IReadOnlyCollection<IAxis> Axes
        {
            get { return _axes; }
        }

        /// <summary>连接后从控制器读取到的型号；仿真器应报告 VPLC5xx-Simu。</summary>
        public string ActualControllerType { get; private set; }

        /// <summary>连接后从控制器读取到的软件版本。</summary>
        public string ActualControllerVersion { get; private set; }

        /// <summary>连接后从控制器读取到的控制器标识。</summary>
        public string ActualControllerId { get; private set; }

        /// <summary>所有轴参数成功应用后为 true；重新连接后会重新应用参数。</summary>
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



































        /// <summary>单次轴运动等待到完成的超时时间，单位为毫秒。</summary>
        internal int MotionTimeoutMilliseconds
        {
            get { return _configuration.MotionTimeoutMilliseconds; }
        }









        /// <summary>停止命令等待轴空闲的确认时限，单位为毫秒。</summary>
        internal int StopConfirmationTimeout
        {
            get { return StopConfirmationTimeoutMilliseconds; }
        }






































































    }
}
