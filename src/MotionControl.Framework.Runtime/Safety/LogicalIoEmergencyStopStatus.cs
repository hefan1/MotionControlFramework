using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Safety;

namespace MotionControl.Framework.Runtime.Safety
{
    /// <summary>通过设备配置的逻辑 DI 读取急停状态。</summary>
    public sealed class LogicalIoEmergencyStopStatus : IConfigurableMachineSafetyStatus
    {
        /// <summary>负责将逻辑输入名称映射到控制卡 DI 的读取服务。</summary>
        private readonly ILogicalIo _logicalIo;

        /// <summary>设备配置中代表急停回路状态的逻辑 DI 名称。</summary>
        private string _emergencyStopInputName;

        /// <summary>保护运行中更新急停 DI 名称时的配置锁。</summary>
        private readonly object _configurationLock = new object();

        /// <summary>创建急停输入读取器；名称为空时表示未启用软件急停监控。</summary>
        public LogicalIoEmergencyStopStatus(ILogicalIo logicalIo, string emergencyStopInputName)
        {
            if (logicalIo == null)
            {
                throw new ArgumentNullException(nameof(logicalIo));
            }

            _logicalIo = logicalIo;
            _emergencyStopInputName = string.Empty;
            UpdateEmergencyStopInputName(emergencyStopInputName);
        }

        /// <summary>更新运行中的急停 DI；名称为空表示没有可读取的联锁输入。</summary>
        public void UpdateEmergencyStopInputName(string emergencyStopInputName)
        {
            string inputName = string.IsNullOrWhiteSpace(emergencyStopInputName)
                ? string.Empty
                : emergencyStopInputName.Trim();

            lock (_configurationLock)
            {
                _emergencyStopInputName = inputName;
            }
        }

        /// <summary>返回当前是否配置了用于软件监控的逻辑 DI 名称。</summary>
        public bool IsConfigured
        {
            get
            {
                lock (_configurationLock)
                {
                    return !string.IsNullOrWhiteSpace(_emergencyStopInputName);
                }
            }
        }

        /// <summary>读取配置的逻辑 DI；逻辑 true 表示急停触发，未配置时明确报告配置缺失。</summary>
        public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string inputName;
            lock (_configurationLock)
            {
                inputName = _emergencyStopInputName;
            }

            if (string.IsNullOrWhiteSpace(inputName))
            {
                return Task.FromException<bool>(new InvalidOperationException(
                    "软件急停联锁未配置逻辑 DI，无法读取急停输入。"));
            }

            return _logicalIo.ReadInputAsync(inputName, cancellationToken);
        }
    }
}
