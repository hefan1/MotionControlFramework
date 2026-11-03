using System;
using System.Collections.Generic;

namespace MotionControl.Host.Wpf.Recovery
{
    /// <summary>保存一次受控暂停后的单机流程步和设备变量运行值。</summary>
    public sealed class MachineRecoveryDocument
    {
        /// <summary>恢复文件格式版本，变更字段含义时需要升级。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>该恢复文件所属的稳定设备编号。</summary>
        public string MachineId { get; set; }

        /// <summary>保存时设备配置文件集合的校验值。</summary>
        public string ConfigurationSignature { get; set; }

        /// <summary>加载时发现签名不匹配；仅用于本次会话保护原恢复记录，不写入文件。</summary>
        [Newtonsoft.Json.JsonIgnore]
        internal bool ConfigurationSignatureMismatch { get; set; }

        /// <summary>最近一次保存恢复文件的本机时间。</summary>
        public DateTime SavedAt { get; set; }

        /// <summary>当前流程步是否允许在复位后由操作员手动启动续跑。</summary>
        public bool CanResume { get; set; }

        /// <summary>上次受控暂停时设备启动初始化是否已经完成。</summary>
        public bool EquipmentInitializationCompleted { get; set; }

        /// <summary>不能续跑时显示给维护人员的具体原因。</summary>
        public string ResumeBlockedReason { get; set; }

        /// <summary>设备变量关闭前的有效值、类型和最后更新时间。</summary>
        public List<DeviceVariableCheckpoint> DeviceVariables { get; set; }

        /// <summary>各工位最近一个完整流程步骤边界。</summary>
        public List<StationFlowCheckpoint> StationFlows { get; set; }

        /// <summary>创建带有当前文件格式版本和空集合的恢复文档。</summary>
        public MachineRecoveryDocument()
        {
            SchemaVersion = 1;
            DeviceVariables = new List<DeviceVariableCheckpoint>();
            StationFlows = new List<StationFlowCheckpoint>();
        }
    }

    /// <summary>保存单个设备变量在本次关闭前的运行值。</summary>
    public sealed class DeviceVariableCheckpoint
    {
        /// <summary>设备流程引用变量时使用的稳定名称。</summary>
        public string Name { get; set; }

        /// <summary>设备变量的 .NET 数据类型名称。</summary>
        public string DataType { get; set; }

        /// <summary>按不受系统区域影响的文本格式保存的运行值。</summary>
        public string CurrentValue { get; set; }

        /// <summary>关闭前运行值是否有效。</summary>
        public bool HasValue { get; set; }

        /// <summary>运行值最后一次更新的本机时间。</summary>
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>保存一个可恢复工位的最近步骤编号。</summary>
    public sealed class StationFlowCheckpoint
    {
        /// <summary>在当前设备内唯一的工位名称。</summary>
        public string StationName { get; set; }

        /// <summary>由工位代码明确管理的流程步骤编号。</summary>
        public int StepNumber { get; set; }
    }
}
