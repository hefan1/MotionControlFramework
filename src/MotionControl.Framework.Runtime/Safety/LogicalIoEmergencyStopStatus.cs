using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Safety;

namespace MotionControl.Framework.Runtime.Safety
{
    /// <summary>通过设备配置的逻辑 DI 读取急停状态。</summary>
    public sealed class LogicalIoEmergencyStopStatus : IMachineSafetyStatus
    {
        private readonly ILogicalIo _logicalIo;
        private readonly string _emergencyStopInputName;

        /// <summary>创建急停输入读取器，并保存已校验的逻辑输入名称。</summary>
        public LogicalIoEmergencyStopStatus(ILogicalIo logicalIo, string emergencyStopInputName)
        {
            if (logicalIo == null)
            {
                throw new ArgumentNullException(nameof(logicalIo));
            }

            if (string.IsNullOrWhiteSpace(emergencyStopInputName)
                || !string.Equals(emergencyStopInputName, emergencyStopInputName.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("急停逻辑输入名称不能为空或包含首尾空格。", nameof(emergencyStopInputName));
            }

            _logicalIo = logicalIo;
            _emergencyStopInputName = emergencyStopInputName;
        }

        /// <summary>读取配置的逻辑 DI；逻辑 true 表示急停已按下或安全回路断开。</summary>
        public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
        {
            return _logicalIo.ReadInputAsync(_emergencyStopInputName, cancellationToken);
        }
    }
}
