using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>步骤详情按动作类型直接分派，状态记录与定义参数分开显示。</summary>
    public partial class TaskMonitorView
    {
        /// <summary>会话引用不可变，只缓存当前选择与语言对应的有界显示字段。</summary>
        private readonly TaskProjectionDetailCache _projectionDetailCache = new TaskProjectionDetailCache();
        /// <summary>保留同一投影字段的诊断列表，刷新时不打断展开和滚动。</summary>
        private IReadOnlyList<TaskDetailField> _projectionDiagnosticsSource;

        /// <summary>只有选择变化时重建关联按钮；刷新数值不会打断键盘焦点。</summary>
        private TaskStepRow _referenceRow;
        /// <summary>上次构建关联入口时的界面语言标记。</summary>
        private string _referenceLanguage;

        /// <summary>刷新采样表和类型专属内容；非运行状态下不把暂停轴显示成仍在运动。</summary>
        private void ShowDetails(TaskStepRow row)
        {
            UpdateNodeSummary(row);
            if (row == null)
            {
                DetailTitle.Text = T("请选择步骤");
                DetailKind.Text = string.Empty;
                DetailSummary.Text = string.Empty;
                RecordNote.Text = string.Empty;
                ObservationTable.ItemsSource = null;
                ObservationTable.Visibility = Visibility.Collapsed;
                FlowDetails.ItemsSource = null;
                GlobalProjectionDiagnostics.ItemsSource = null;
                GlobalProjectionDiagnosticsExpander.Visibility = Visibility.Collapsed;
                _projectionDiagnosticsSource = null;
                ParametersList.ItemsSource = null;
                DetailNotice.Text = T("请选择步骤");
                ReferencesPanel.Children.Clear();
                _referenceRow = null;
                return;
            }
            TaskNode node = row.Node;
            var diagnostic = row.Snapshot?.Diagnostics;
            DetailTitle.Text = row.Number + " · " + row.Name;
            DetailKind.Text = KindName(node);
            string detail = (row.Snapshot?.State == TaskNodeState.Failed
                || (node is WaitConditionNode && row.Snapshot?.State == TaskNodeState.Waiting)
                ? row.Detail : diagnostic?.Summary ?? row.Detail) ?? string.Empty;
            DetailSummary.Text = row.StateText == T("已暂停") ? T("已暂停") + " · " + detail : detail;
            RecordNote.Text = diagnostic == null ? T("尚无执行采样") : T("最后执行采样") + " "
                + diagnostic.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                + ((row.Snapshot.State == TaskNodeState.Completed || row.Snapshot.State == TaskNodeState.Cancelled) ? " · " + T("历史记录") : string.Empty);
            bool motion = node is MoveAxisNode || node is MoveAxisGroupNode;
            ItemColumn.Header = T(motion ? "轴" : "项目");
            ExpectedColumn.Header = T(motion ? "接受的终点" : "目标或期望");
            ActualColumn.Header = T(motion ? "采样位置" : "采样值");
            var values = new List<ObservationRow>();
            if (diagnostic != null)
                foreach (var value in diagnostic.Values) values.Add(CreateObservationRow(node, value));
            ObservationTable.ItemsSource = values;
            ObservationTable.Visibility = values.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var fields = new List<TaskDetailField>();
            var parameters = new List<TaskDetailField> { Field("步骤编号", node.Id), Field("超时时限", node.TimeoutMs + " ms") };
            var references = new List<TaskReferenceEventArgs>();
            AddMotionDetails(node, fields, parameters, references);
            AddSignalDetails(node, diagnostic, fields, parameters, references);
            AddFlowDetails(row, fields, parameters, references);
            AddWaitDiagnostics(row.Snapshot, fields, references);
            var selectedStation = StationsGrid.SelectedItem as MotionControl.TaskEngine.Wpf.Monitoring.TaskStationRow;
            var projectionFields = _projectionDetailCache.Read(_session.Projection, _session.ProjectionError,
                selectedStation?.Id, row.TaskId, node.Id, _guidanceEnglish);
            string diagnosticName = _guidanceEnglish ? "Diagnostic" : "诊断";
            fields.AddRange(projectionFields.Where(item => item.Name != diagnosticName));
            FlowDetails.ItemsSource = fields;
            if (!ReferenceEquals(_projectionDiagnosticsSource, projectionFields))
            {
                var projectDiagnostics = projectionFields.Where(item => item.Name == diagnosticName).ToList();
                GlobalProjectionDiagnostics.ItemsSource = projectDiagnostics;
                GlobalProjectionDiagnosticsExpander.Visibility = projectDiagnostics.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                _projectionDiagnosticsSource = projectionFields;
            }
            ParametersList.ItemsSource = parameters;
            DetailNotice.Text = row.Snapshot?.State == TaskNodeState.Failed ? T("故障恢复提示")
                : T("监控只读说明");
            if (_referenceRow != row || _referenceLanguage != T("关联配置"))
            {
                _referenceRow = row; _referenceLanguage = T("关联配置");
                ReferencesPanel.Children.Clear();
                foreach (var reference in references.GroupBy(item => item.Page + "\n" + item.Name + "\n" + item.Direction).Select(group => group.First()))
                {
                    string suffix = string.IsNullOrEmpty(reference.Direction) ? string.Empty : " (" + reference.Direction + ")";
                    var button = new Button { Content = T(reference.Page) + " · " + reference.Name + suffix, Tag = reference, ToolTip = reference.Name };
                    button.Click += Reference_Click;
                    ReferencesPanel.Children.Add(button);
                }
            }
        }

        /// <summary>运行状态取所选工位的快照，查看位置仅取当前步骤树选择。</summary>
        private void UpdateNodeSummary(TaskStepRow viewed)
        {
            if (_refreshFailed)
            {
                ExecutingNodesText.Text = ViewedNodeText.Text = T("节点摘要读取失败");
                return;
            }
            var station = (StationsGrid.SelectedItem as TaskStationRow)?.Snapshot;
            var run = station?.Run;
            if (run == null)
                ExecutingNodesText.Text = T("没有节点运行快照");
            else if (_snapshot?.State != TaskMachineState.Running || station.State != StationTaskState.Executing
                || run.State != TaskRunState.Running)
                ExecutingNodesText.Text = T("留存运行快照");
            else
            {
                var active = run.Nodes.Where(n => n.State == TaskNodeState.Running || n.State == TaskNodeState.Moving
                    || n.State == TaskNodeState.Waiting).ToList();
                var parents = new HashSet<string>(active.Select(n => n.ParentExecutionId), StringComparer.Ordinal);
                var leaves = active.Where(n => !parents.Contains(n.ExecutionId))
                    .Select(n => n.Name + " · " + n.TaskId + " / " + n.NodeId + " · " + n.ExecutionId);
                string text = string.Join(Environment.NewLine, leaves);
                ExecutingNodesText.Text = text.Length == 0 ? T("无活动节点记录") : text;
            }
            ViewedNodeText.Text = viewed == null ? T("未选择查看节点")
                : viewed.Node.Name + " · " + viewed.TaskId + " / " + viewed.Node.Id + " · "
                    + (viewed.Snapshot?.ExecutionId ?? T("定义无执行记录"));
        }

        /// <summary>关联点击仅通知窗口查看配置，不发送运动命令。</summary>
        private void Reference_Click(object sender, RoutedEventArgs e)
        { ReferenceRequested?.Invoke(this, (TaskReferenceEventArgs)((Button)sender).Tag); }

        /// <summary>把布尔值和比较操作转换为可读文字，保留用户自己的名称。</summary>
        private string TranslateValue(string value)
        {
            if (value == "True" || value == "true") return T("有效");
            if (value == "False" || value == "false") return T("无效");
            foreach (TaskComparison comparison in Enum.GetValues(typeof(TaskComparison)))
                if (value.StartsWith(comparison + " ", StringComparison.Ordinal))
                    return ComparisonText(comparison) + " " + TranslateValue(value.Substring(comparison.ToString().Length + 1));
            return value;
        }

        /// <summary>创建统一的参数名称与值。</summary>
        private TaskDetailField Field(string name, string value) { return new TaskDetailField(T(name), value); }

        /// <summary>给每种明确的节点类型提供现场动作名称。</summary>
        private string KindName(TaskNode node)
        {
            switch (node.Kind)
            {
                case "Sequence": return T("顺序步骤"); case "ParallelGroup": return T("并行组");
                case "ConditionBranch": return T("条件分支"); case "ResourceScope": return T("共享资源");
                case "CallTask": return T("调用子任务"); case "Delay": return T("工艺延时");
                case "WaitInput": return T("等待输入"); case "WaitOutput": return T("等待读回输出"); case "ReadInput": return T("读取输入到变量"); case "ReadInputWord": return T("读取锁存编码"); case "WaitAxisPosition": return T("等待轴到位"); case "WaitExclusiveInputs": return T("等待互斥输入"); case "WaitCondition": return T("等待条件");
                case "WriteRecord": return T("保存记录"); case "SetOutput": return T("设置输出"); case "SetVariable": return T("变量赋值");
                case "MoveAxis": return T("单轴定位"); case "MoveAxisGroup": return T("多轴分别定位");
                case "AxisCommand": return T("轴命令"); case "CoordinatedMove": return T("协调插补未接入");
                default: return node.Kind;
            }
        }
    }

    /// <summary>已经本地化的采样表行，不包含设备控制能力。</summary>
    public sealed class ObservationRow
    {
        /// <summary>采样对象。</summary>
        public string Name { get; }
        /// <summary>期望值。</summary>
        public string Expected { get; }
        /// <summary>采样时实际值。</summary>
        public string Actual { get; }
        /// <summary>满足、不满足或未知。</summary>
        public string Result { get; }
        /// <summary>创建只读显示行。</summary>
        public ObservationRow(string name, string expected, string actual, string result)
        { Name = name; Expected = expected; Actual = actual; Result = result; }
    }
}
