using System;
using System.Collections.Generic;

namespace MotionControl.TaskEngine.Configuration
{
    /// <summary>已经预检的工程包快照；应用只能使用原始快照并再次检查现场配置未变化。</summary>
    public sealed class MachineProjectPreview
    {
        /// <summary>创建只能由工程包服务填充的只读预览。</summary>
        internal MachineProjectPreview() { }
        /// <summary>候选工程的设备名称，来自包内任务清单或设备定义，不代表当前已装载设备。</summary>
        public string EquipmentName { get; internal set; }
        /// <summary>执行方式：任务列表、C# 工位或手动。</summary>
        public string ExecutionOwner { get; internal set; }
        /// <summary>控制器后端名称。</summary>
        public string ControllerBackend { get; internal set; }
        /// <summary>导出设备标识；导入时任务清单可绑定当前设备标识。</summary>
        public string SourceMachineId { get; internal set; }
        /// <summary>实际将接收导入的本机设备标识，与包的导出来源分别展示。</summary>
        public string TargetMachineId { get; internal set; }
        /// <summary>待导入的配置文件。</summary>
        public IReadOnlyList<string> Files { get; internal set; }
        /// <summary>包内文件与已读取现有配置的并集；仅用于预览，不表示这些文件都来自工程包。</summary>
        public IReadOnlyList<string> MergedFiles { get; internal set; }
        /// <summary>包中未包含、应用后继续保留的已读取现有配置文件。</summary>
        public IReadOnlyList<string> RetainedFiles { get; internal set; }
        /// <summary>相对当前设备的新增、修改及保留说明；导入不会删除包外文件。</summary>
        public IReadOnlyList<string> Changes { get; internal set; }
        /// <summary>供工程师核查的不可自动判定事项。</summary>
        public IReadOnlyList<string> Warnings { get; internal set; }
        /// <summary>绑定创建该预览的服务对象，防止跨设备复用。</summary>
        internal object Owner { get; set; }
        /// <summary>经过校验且不对外暴露的文件快照。</summary>
        internal Dictionary<string, string> Contents { get; set; }
        /// <summary>预览时目标设备的配置快照，不含运行或恢复记录。</summary>
        internal Dictionary<string, string> CurrentContents { get; set; }
        /// <summary>预览时原始配置字节，用于在事务备份中保留 BOM 和原始编码字节。</summary>
        internal Dictionary<string, byte[]> CurrentBytes { get; set; }
        /// <summary>预览时设备配置签名，用于拒绝覆盖期间的外部更改。</summary>
        internal string TargetSignature { get; set; }

        /// <summary>读取预览时包内配置原文；不读取其他路径或设备运行记录。</summary>
        public string GetFileContent(string relativePath)
        {
            if (relativePath == null || Contents == null || !Contents.TryGetValue(relativePath, out string value))
                throw new ArgumentException("工程包不包含此配置文件。", nameof(relativePath));
            return value;
        }

        /// <summary>读取预览时当前设备对应配置；文件不存在时返回 null。</summary>
        public string GetCurrentFileContent(string relativePath)
        {
            if (relativePath == null || CurrentContents == null) throw new ArgumentNullException(nameof(relativePath));
            return CurrentContents.TryGetValue(relativePath, out string value) ? value : null;
        }
    }
}
