using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using Newtonsoft.Json;

namespace MotionControl.TaskEngine.Editing
{
    /// <summary>单台设备的完整编辑草稿，运行文件仍使用原有 Tasks 和 TaskMachine 格式。</summary>
    public sealed class TaskWorkspaceDocument
    {
        /// <summary>草稿格式版本，当前为 1。</summary>
        public int SchemaVersion { get; set; } = 1;
        /// <summary>草稿所属设备，不能跨设备覆盖。</summary>
        public string MachineId { get; set; }
        /// <summary>开始编辑时运行定义及关联配置的签名，应用前用于发现外部修改。</summary>
        public string BaseSignature { get; set; }
        /// <summary>本窗口最近载入或保存的草稿文件签名；空值表示当时没有草稿，只存内存，不改变 JSON 格式。</summary>
        [JsonIgnore]
        public string DraftSignature { get; set; }
        /// <summary>最近一次完整保存草稿的本机时间。</summary>
        public DateTime SavedAt { get; set; }
        /// <summary>设备工位安排、初始化与共享资源定义。</summary>
        public TaskMachineConfiguration Machine { get; set; }
        /// <summary>完整任务库，删除任务时从此列表移除，不直接删运行文件。</summary>
        public List<TaskWorkspaceTask> Tasks { get; set; } = new List<TaskWorkspaceTask>();
    }

    /// <summary>一份任务及其稳定文件名，改显示名称时不改文件名。</summary>
    public sealed class TaskWorkspaceTask
    {
        /// <summary>Tasks 目录下的单个 JSON 文件名。</summary>
        public string FileName { get; set; }
        /// <summary>直接复用现有任务模型，不引入第二套步骤模型。</summary>
        public TaskDefinition Definition { get; set; }
    }
}
