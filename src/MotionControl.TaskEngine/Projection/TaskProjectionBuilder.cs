using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Projection
{
    /// <summary>每次重建独占的定义树读取器；完成后释放所有源对象。</summary>
    internal sealed partial class TaskProjectionBuilder
    {
        /// <summary>限制共享任务反复展开产生的总工作量。</summary>
        private const int MaxVisits = 100000;
        /// <summary>累加节点、条件和引用，超过上限则整份结果明确标记截断。</summary>
        private int visits;
        /// <summary>累计路径字符预算，防止长标识和多路径组合占用过量内存。</summary>
        private long pathCharacters;
        /// <summary>结构深度截断后跳过需要完整树的全库校验。</summary>
        private bool depthTruncated;
        /// <summary>当前结构祖先；坏草稿的节点对象环不能重复展开。</summary>
        private readonly HashSet<TaskNode> nodeStack = new HashSet<TaskNode>();
        /// <summary>当前配置目录，允许缺失。</summary>
        private readonly TaskResourceCatalog catalog;
        /// <summary>只包含同名无歧义的任务。</summary>
        private readonly Dictionary<string, TaskDefinition> tasks = new Dictionary<string, TaskDefinition>(StringComparer.OrdinalIgnoreCase);
        /// <summary>同名任务不能按出现顺序择一。</summary>
        private readonly HashSet<string> ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>当前调用栈，仅检测本路径中的递归。</summary>
        private readonly HashSet<string> active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>已从工位或初始化访问的任务，剩余定义单独从任务库查看。</summary>
        private readonly HashSet<string> linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>各独立路径的解析记录。</summary>
        private readonly List<TaskProjectReference> rows = new List<TaskProjectReference>();
        /// <summary>源数据或投影能力的具体限制。</summary>
        private readonly List<string> issues = new List<string>();
        /// <summary>区分未提供任务库与已提供的空库。</summary>
        private bool libraryProvided;

        /// <summary>接受无设备控制能力的配置目录。</summary>
        internal TaskProjectionBuilder(TaskResourceCatalog catalog) { this.catalog = catalog; }

        /// <summary>限制重复调用带来的乘法展开；保留首个超限位置。</summary>
        private bool Visit(string path)
        {
            if (visits > MaxVisits) return false;
            pathCharacters += path.Length;
            if (visits == MaxVisits || path.Length > 8192 || pathCharacters > 16000000)
            {
                issues.Add(path.Substring(0, Math.Min(path.Length, 256)) + "：投影超过工作量或路径大小上限，结果未完整展开。");
                visits = MaxVisits + 1;
                return false;
            }
            return ++visits <= MaxVisits;
        }

        /// <summary>索引任务、校验定义，然后从明确的工位与设备初始化入口展开。</summary>
        internal TaskProjectProjection Build(string source, string machineId, TaskMachineConfiguration machine,
            IEnumerable<TaskDefinition> definitions, string signature, string initializationStationId = null)
        {
            libraryProvided = definitions != null;
            if (!libraryProvided) issues.Add("任务库未提供。");
            if (catalog == null) issues.Add("资源目录未提供，无法核实配置对象。");
            else issues.AddRange(catalog.Notices);
            if (string.IsNullOrWhiteSpace(signature)) issues.Add("来源文件签名未提供。");
            if (string.IsNullOrWhiteSpace(machineId)) issues.Add("设备标识未提供。");
            var library = (definitions ?? Enumerable.Empty<TaskDefinition>()).Take(129).ToList();
            if (library.Count > 128)
            {
                issues.Add("任务库超过现有校验器的 128 份定义上限，未解析。");
                libraryProvided = false;
                library.Clear();
            }
            foreach (var task in library)
            {
                if (string.IsNullOrWhiteSpace(task?.TaskId) || task.TaskId != task.TaskId.Trim())
                {
                    issues.Add("任务库包含空标识或带首尾空格的定义：" + task?.TaskId);
                    continue;
                }
                if (tasks.ContainsKey(task.TaskId)) ambiguous.Add(task.TaskId);
                else tasks.Add(task.TaskId, task);
            }
            foreach (string id in ambiguous) { tasks.Remove(id); issues.Add("任务标识重复：" + id); }
            if (machine?.Stations == null) issues.Add("工位清单未提供。");
            if (machine != null && machine.SchemaVersion != 1) issues.Add("工位清单版本不受支持，未关联工位或初始化。");
            else if (machine != null)
            {
                if (machine.InitializationTaskId != null)
                    ExpandTask(initializationStationId, null, null, "Initialization", machine.InitializationTaskId, 0);
                var stations = machine.Stations ?? new List<StationTaskConfiguration>();
                if (stations.Count > 128)
                {
                    issues.Add("工位清单超过 128 个工位上限，未解析工位归属。");
                    stations = new List<StationTaskConfiguration>();
                }
                var duplicateIds = new HashSet<string>(stations.Where(s => s != null).GroupBy(s => s.StationId,
                    StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key), StringComparer.OrdinalIgnoreCase);
                foreach (var station in stations)
                {
                    if (!Visit("Stations")) break;
                    if (string.IsNullOrWhiteSpace(station?.StationId) || station.StationId != station.StationId.Trim() || duplicateIds.Contains(station.StationId))
                    {
                        issues.Add("工位标识为空或重复，无法确定归属：" + station?.StationId);
                        continue;
                    }
                    string id = station.StationId;
                    string path = "Station/" + Segment(id);
                    Row(id, null, null, path, "Station", id, "已定位", station.Name);
                    if (station.Mode == StationTaskMode.OnRequest)
                    {
                        Handshake(path + "/RequestVariable", station.RequestVariable);
                        Handshake(path + "/CompletedVariable", station.CompletedVariable);
                    }
                    else if (station.RequestVariable != null || station.CompletedVariable != null)
                        issues.Add(path + "：当前工位模式不使用 RequestVariable/CompletedVariable，已填写的交接信号未关联。");
                    ExpandTask(id, null, null, path, station.TaskId, 0);

                    // 工位交接需要已定义的布尔变量；目标存在与类型正确分别检查。
                    void Handshake(string fieldPath, string name)
                    {
                        Reference(id, null, null, fieldPath, "Variable", name);
                        if (name != null && catalog != null && catalog.Variables.TryGetValue(name, out DeviceVariableType type)
                            && type != DeviceVariableType.Boolean)
                            issues.Add(fieldPath + "：交接信号必须引用布尔变量：" + name);
                    }
                }
            }
            foreach (var task in tasks.Values.Where(task => !linked.Contains(task.TaskId)).ToList())
            {
                if (visits > MaxVisits) break;
                ExpandTask(null, null, null, "Library", task.TaskId, 0);
            }
            // 先完成有界遍历，校验器仅接收当前能解释且标识无歧义的定义。
            var supported = tasks.Values.Where(task => task.SchemaVersion == 1).ToList();
            if (catalog != null && supported.Count > 0 && visits <= MaxVisits && !depthTruncated)
                issues.AddRange(new TaskDefinitionValidator().Validate(supported[0], catalog, supported.Skip(1))
                    .Select(issue => issue.ToString()));
            else if (visits > MaxVisits || depthTruncated) issues.Add("投影已截断，未执行完整任务库校验。");
            return new TaskProjectProjection(source, machineId, machine?.Revision > 0 ? machine.Revision : (int?)null, signature, rows, issues);
        }

        /// <summary>转义路径分隔符，防止源标识中的斜线与结构路径混淆。</summary>
        private static string Segment(string value)
        {
            return (value ?? "").Replace("%", "%25").Replace("/", "%2F");
        }

        /// <summary>保留空引用及其定位，解析失败不能转换为成功。</summary>
        private void Row(string station, TaskDefinition task, TaskNode node, string path, string kind, string target,
            string status, string displayName = null, string axis = null)
        {
            if (!Visit(path)) return;
            rows.Add(new TaskProjectReference { StationId = station, TaskId = task?.TaskId, TaskRevision = task?.Revision > 0 ? task.Revision : (int?)null,
                NodeId = node?.Id, Path = path, Kind = kind, Target = target, AxisName = axis, Status = status, DisplayName = displayName });
            if (status != "已定位") issues.Add(path + "：" + kind + " [" + target + "] " + status);
        }

        /// <summary>按目录核对显式对象引用；同名 DI/DO 或不同轴的同名点位分别匹配。</summary>
        private void Reference(string station, TaskDefinition task, TaskNode node, string path, string kind, string target, string axis = null)
        {
            bool exists = false;
            if (catalog != null && !string.IsNullOrEmpty(target))
            {
                switch (kind)
                {
                    case "Axis": exists = catalog.Axes.ContainsKey(target); break;
                    case "Variable": exists = catalog.Variables.ContainsKey(target); break;
                    case "TeachPoint": exists = catalog.FindTeachPoint(axis, target) != null; break;
                    case "Input": exists = catalog.Inputs.Contains(target, StringComparer.OrdinalIgnoreCase); break;
                    case "Output": exists = catalog.Outputs.Contains(target, StringComparer.OrdinalIgnoreCase); break;
                    case "Resource": exists = catalog.Resources.Contains(target, StringComparer.OrdinalIgnoreCase); break;
                    case "Alarm": exists = catalog.AlarmCodes.Contains(target, StringComparer.OrdinalIgnoreCase); break;
                    default: throw new ArgumentException("未支持的引用类型：" + kind);
                }
            }
            Row(station, task, node, path, kind, target, exists ? "已定位" : catalog == null ? "目录未提供" : "引用失效", axis: axis);
        }

        /// <summary>分别展开每次调用，在当前路径栈中拒绝递归、歧义和无来源定义。</summary>
        private void ExpandTask(string station, TaskDefinition caller, TaskNode call, string parent, string id, int depth)
        {
            string path = parent + "/Task/" + Segment(id);
            if (!Visit(path)) return;
            TaskDefinition task = null;
            string status = "已定位";
            if (!libraryProvided) status = "任务库未提供";
            else if (ambiguous.Contains(id)) status = "任务标识有歧义";
            else if (id == null || !tasks.TryGetValue(id, out task)) status = "引用失效";
            else if (active.Contains(id)) status = "递归调用";
            else if (task.SchemaVersion != 1) status = "不支持的任务版本";
            else if (depth > 128) { depthTruncated = true; status = "调用或结构深度超限"; }
            Row(station, caller, call, path, "Task", id, status, task?.Name);
            if (status != "已定位") return;
            linked.Add(id);
            active.Add(id);
            Node(station, task, task.Root, path + "/Root", depth + 1);
            active.Remove(id);
        }

        /// <summary>按实际分支和节点标识展开；数组位置保留为诊断路径，不作为业务标识。</summary>
        private void Node(string station, TaskDefinition task, TaskNode node, string parent, int depth)
        {
            string path = parent + "/" + Segment(node?.Id);
            if (!Visit(path)) return;
            if (depth > 128) { depthTruncated = true; issues.Add(path + "：结构过深，未完整展开。"); return; }
            if (node == null) { issues.Add(path + "：节点缺失。"); return; }
            if (!nodeStack.Add(node)) { issues.Add(path + "：节点对象形成循环。"); return; }
            Row(station, task, node, path, "Node", node.Id, string.IsNullOrWhiteSpace(node.Id) ? "缺少标识" : "已定位", node.Name);
            if (node is CallTaskNode call) ExpandTask(station, task, node, path, call.TaskId, depth + 1);
            else if (node is SequenceNode sequence) Children(sequence.Children, "/Children");
            else if (node is ParallelGroupNode parallel) Children(parallel.Branches, "/Branches");
            else if (node is ConditionBranchNode branch)
            {
                Node(station, task, branch.WhenTrue, path + "/WhenTrue", depth + 1);
                if (branch.WhenFalse != null) Node(station, task, branch.WhenFalse, path + "/WhenFalse", depth + 1);
            }
            else if (node is ResourceScopeNode scope) Node(station, task, scope.Body, path + "/Body", depth + 1);
            if (!(node is CallTaskNode)) ReadFields(station, task, node, path);
            nodeStack.Remove(node);

            // 分支列表中的每次出现都独立保留，超限后停止本轮展开。
            void Children(List<TaskNode> list, string field)
            {
                if (list == null) { issues.Add(path + field + "：子节点集合缺失。"); return; }
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < list.Count && visits <= MaxVisits; i++)
                {
                    string childPath = path + field;
                    string id = list[i]?.Id;
                    if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    {
                        childPath += "/InvalidIndex/" + i;
                        issues.Add(childPath + "：子节点标识为空或重复。");
                    }
                    Node(station, task, list[i], childPath, depth + 1);
                }
            }
        }
    }
}
