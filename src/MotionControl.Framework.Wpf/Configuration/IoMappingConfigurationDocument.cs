using System.Collections.Generic;
using MotionControl.Framework.Contracts.Configuration;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>保存设备的 DI、DO 逻辑名称与控制卡物理通道映射。</summary>
    public sealed class IoMappingConfigurationDocument
    {
        /// <summary>当前 IO 映射文件格式版本。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>急停状态使用的 DI 逻辑名称。</summary>
        public string EmergencyStopInputName { get; set; }

        /// <summary>设备流程使用的数字输入映射。</summary>
        public List<DigitalIoHardwareDefinition> DigitalInputs { get; set; }

        /// <summary>设备流程使用的数字输出映射。</summary>
        public List<DigitalIoHardwareDefinition> DigitalOutputs { get; set; }

        /// <summary>创建空的首版 IO 映射配置。</summary>
        public IoMappingConfigurationDocument()
        {
            SchemaVersion = 1;
            EmergencyStopInputName = string.Empty;
            DigitalInputs = new List<DigitalIoHardwareDefinition>();
            DigitalOutputs = new List<DigitalIoHardwareDefinition>();
        }
    }
}
