using System.Collections.Generic;
using MotionControl.Framework.Contracts.Variables;

namespace MotionControl.Framework.Runtime.Variables
{
    /// <summary>每台设备独立保存的设备变量定义文件结构。</summary>
    public sealed class DeviceVariableConfigurationDocument
    {
        /// <summary>当前 JSON 配置结构版本。</summary>
        public int SchemaVersion { get; set; } = 1;

        /// <summary>设备变量定义列表；运行时当前值不写入此文件。</summary>
        public List<DeviceVariableDefinition> Variables { get; set; } = new List<DeviceVariableDefinition>();

        /// <summary>检查配置版本和每条变量定义，避免无效配置进入运行时。</summary>
        public void Validate()
        {
            if (SchemaVersion != 1)
            {
                throw new System.InvalidOperationException("设备变量配置版本不受支持：" + SchemaVersion + "。");
            }

            if (Variables == null)
            {
                throw new System.InvalidOperationException("设备变量配置缺少 variables 列表。");
            }

            DeviceVariableStore.ValidateDefinitions(Variables);
        }
    }
}
