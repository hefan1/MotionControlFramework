using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Projection
{
    /// <summary>从调用者提供的定义重建工程引用；仅保留定位字段，不读写文件或设备。</summary>
    public sealed class TaskProjectProjection
    {
        /// <summary>标记编辑草稿或已保存定义，不表示设备已加载。</summary>
        public string Source { get; }
        /// <summary>源文档设备标识，未提供时为空。</summary>
        public string MachineId { get; }
        /// <summary>源清单修订号；不代表整个工程或目录版本。</summary>
        public int? MachineRevision { get; }
        /// <summary>调用方核实的已保存定义签名，内存草稿或未知时为空。</summary>
        public string SourceSignature { get; }
        /// <summary>保留每次出现的节点及其引用，顺序来自源定义。</summary>
        public IReadOnlyList<TaskProjectReference> References { get; }
        /// <summary>源缺失、校验问题及未完整展开的原因。</summary>
        public IReadOnlyList<string> Diagnostics { get; }

        /// <summary>只接收一次解析的结果，不保留可编辑定义。</summary>
        internal TaskProjectProjection(string source, string machineId, int? revision, string signature,
            List<TaskProjectReference> references, List<string> diagnostics)
        {
            Source = source;
            MachineId = machineId;
            MachineRevision = revision;
            SourceSignature = string.IsNullOrWhiteSpace(signature) ? null : signature;
            References = references.AsReadOnly();
            Diagnostics = diagnostics.AsReadOnly();
        }

        /// <summary>从编辑器当前草稿派生；草稿文件签名不能证明未保存编辑的版本。</summary>
        public static TaskProjectProjection FromDraft(TaskWorkspaceDocument document, TaskResourceCatalog catalog)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document.SchemaVersion != 1) throw new ArgumentException("不支持的编辑草稿版本。", nameof(document));
            var builder = new TaskProjectionBuilder(catalog);
            if (document.Machine != null && !string.Equals(document.MachineId, document.Machine.MachineId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("草稿与工位清单设备标识不一致。", nameof(document));
            return builder.Build("编辑草稿", document.MachineId, document.Machine,
                document.Tasks?.Select(item => item?.Definition), null);
        }

        /// <summary>从同一工程已保存的清单、任务库及目录派生，不表示已应用或加载到会话。</summary>
        public static TaskProjectProjection FromSaved(TaskMachineConfiguration machine, IEnumerable<TaskDefinition> tasks,
            TaskResourceCatalog catalog, string sourceSignature = null)
        {
            return new TaskProjectionBuilder(catalog).Build("已保存定义", machine?.MachineId, machine, tasks, sourceSignature);
        }

        /// <summary>从会话构造时同批输入派生；仅为已加载定义引用，不表示活动执行实例或硬件生效版本。</summary>
        public static TaskProjectProjection FromSession(TaskMachineConfiguration machine, IEnumerable<TaskDefinition> tasks,
            TaskResourceCatalog executionCatalog)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));
            if (tasks == null) throw new ArgumentNullException(nameof(tasks));
            if (executionCatalog == null) throw new ArgumentNullException(nameof(executionCatalog));
            return new TaskProjectionBuilder(executionCatalog).Build("已加载会话", machine.MachineId, machine, tasks,
                null, TaskMachineController.InitializationStationId);
        }

        /// <summary>按对象类型、稳定键及点位所属轴查询全部引用方，保留原调用路径。</summary>
        public IEnumerable<TaskProjectReference> FindReferences(string kind, string target, string axisName = null)
        {
            return References.Where(row => row.Kind == kind &&
                string.Equals(row.Target, target, StringComparison.OrdinalIgnoreCase) &&
                (kind != "TeachPoint" || string.Equals(row.AxisName, axisName, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>配置树中一次具体出现的位置，不代表正在执行的实例。</summary>
    public sealed class TaskProjectReference
    {
        /// <summary>明确关联的工位；初始化仅在会话来源上带有控制器保留键，独立任务库路径为空。</summary>
        public string StationId { get; internal set; }
        /// <summary>引用方所在任务标识。</summary>
        public string TaskId { get; internal set; }
        /// <summary>引用方所在任务定义的源修订号，未知时为空。</summary>
        public int? TaskRevision { get; internal set; }
        /// <summary>引用方所在节点标识。</summary>
        public string NodeId { get; internal set; }
        /// <summary>工位、任务调用、结构分支和字段路径。</summary>
        public string Path { get; internal set; }
        /// <summary>目标类型，如 Axis、Input、TeachPoint。</summary>
        public string Kind { get; internal set; }
        /// <summary>目标的源标识，缺失时保留空值。</summary>
        public string Target { get; internal set; }
        /// <summary>示教点复合键的轴名称，其余类型为空。</summary>
        public string AxisName { get; internal set; }
        /// <summary>节点、工位或任务的源显示名称。</summary>
        public string DisplayName { get; internal set; }
        /// <summary>仅说明引用解析状态，不能证明设备可运行。</summary>
        public string Status { get; internal set; }
    }
}
