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
        /// <summary>只有选择变化时重建关联按钮；刷新数值不会打断键盘焦点。</summary>
        private TaskStepRow _referenceRow;
        /// <summary>上次构建关联入口时的界面语言标记。</summary>
        private string _referenceLanguage;

        /// <summary>刷新采样表和类型专属内容；非运行状态下不把暂停轴显示成仍在运动。</summary>
        private void ShowDetails(TaskStepRow row)
        {
            if (row == null)
            {
                DetailTitle.Text = T("请选择步骤");
                DetailKind.Text = string.Empty;
                DetailSummary.Text = string.Empty;
                RecordNote.Text = string.Empty;
                ObservationTable.ItemsSource = null;
                ObservationTable.Visibility = Visibility.Collapsed;
                FlowDetails.ItemsSource = null;
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
            if (_snapshot != null && _snapshot.State != TaskMachineState.Running)
                detail = detail.Replace("（运动中）", "（设备未运行，目标未到位）")
                    .Replace("运动中：", "设备未运行，目标未到位：");
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
                foreach (var value in diagnostic.Values) values.Add(new ObservationRow(value.Name,
                    node is SetVariableNode || node is WaitConditionNode || node is ConditionBranchNode ? value.Expected : TranslateValue(value.Expected),
                    value.Actual == null ? T("未知") : node is SetVariableNode || node is WaitConditionNode || node is ConditionBranchNode ? value.Actual : TranslateValue(value.Actual),
                    T(!value.Satisfied.HasValue ? "未知" : value.Satisfied.Value ? "满足" : "未满足")));
            ObservationTable.ItemsSource = values;
            ObservationTable.Visibility = values.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var fields = new List<TaskDetailField>();
            var parameters = new List<TaskDetailField> { Field("步骤编号", node.Id), Field("超时时限", node.TimeoutMs + " ms") };
            var references = new List<TaskReferenceEventArgs>();
            AddMotionDetails(node, fields, parameters, references);
            AddSignalDetails(node, diagnostic, fields, parameters, references);
            AddFlowDetails(row, fields, parameters, references);
            FlowDetails.ItemsSource = fields;
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
                case "WaitInput": return T("等待输入"); case "WaitOutput": return T("等待读回输出"); case "ReadInput": return T("读取输入到变量"); case "WaitExclusiveInputs": return T("等待互斥输入"); case "WaitCondition": return T("等待条件");
                case "SetOutput": return T("设置输出"); case "SetVariable": return T("变量赋值");
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
