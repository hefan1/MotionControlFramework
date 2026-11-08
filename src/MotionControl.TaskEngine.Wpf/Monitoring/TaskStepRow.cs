using System.Collections.ObjectModel;
using System.ComponentModel;
using System;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Wpf.Monitoring
{
    /// <summary>步骤树中的稳定行；定时刷新只更新文字，保留用户的展开与选择。</summary>
    public sealed class TaskStepRow : INotifyPropertyChanged
    {
        /// <summary>属性刷新事件。</summary>
        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>所属任务编号，子任务使用自己的编号。</summary>
        public string TaskId { get; }
        /// <summary>本次监控冻结的定义。</summary>
        public TaskNode Node { get; }
        /// <summary>所在层级内的显示步序，不作为执行键。</summary>
        public string Number { get; }
        /// <summary>工艺动作名。</summary>
        public string Name => Node.Name;
        /// <summary>父行；顶层步骤为空。</summary>
        public TaskStepRow Parent { get; }
        /// <summary>顺序、并行、分支和子任务的子行。</summary>
        public ObservableCollection<TaskStepRow> Children { get; } = new ObservableCollection<TaskStepRow>();
        /// <summary>对应本轮的唯一调用快照；未进入为空。</summary>
        public TaskNodeSnapshot Snapshot { get; private set; }
        /// <summary>现场可阅读的状态。</summary>
        public string StateText { get; private set; }
        /// <summary>等待或异常说明。</summary>
        public string Detail { get; private set; }
        /// <summary>步骤原始诊断信息，供维护人员悬停查看。</summary>
        public string EngineeringDetail => Snapshot?.Detail ?? Detail;
        /// <summary>有文字对应的状态色。</summary>
        public string StateColor { get; private set; } = "#596B7D";
        /// <summary>该定义在本次条件选择中未被选中。</summary>
        public bool NotSelected { get; private set; }
        /// <summary>记录用户展开状态。</summary>
        public bool IsExpanded { get => _expanded; set { _expanded = value; Changed(); } }
        /// <summary>展开状态的存储值。</summary>
        private bool _expanded = true;
        /// <summary>当前行的选择标记。</summary>
        private bool _selected;
        /// <summary>允许跟随执行或手动选择，不重建树。</summary>
        public bool IsSelected { get => _selected; set { _selected = value; Changed(); } }

        /// <summary>建立定义行，运行前不伪造执行结果。</summary>
        public TaskStepRow(string taskId, TaskNode node, string number, TaskStepRow parent)
        {
            TaskId = taskId; Node = node; Number = number; Parent = parent;
        }

        /// <summary>只替换显示数据，调用编号由父子关系匹配而来。</summary>
        internal void Update(TaskNodeSnapshot snapshot, bool notSelected, string state, string detail, string color)
        {
            Snapshot = snapshot; NotSelected = notSelected; StateText = state; Detail = detail; StateColor = color; Changed();
        }

        /// <summary>通知本行所有派生显示字段刷新。</summary>
        private void Changed() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); }
    }

    /// <summary>右侧简短参数或关联来源的一行。</summary>
    public sealed class TaskDetailField
    {
        /// <summary>字段中文资源对应的显示名称。</summary>
        public string Name { get; }
        /// <summary>格式化后的值，包含单位。</summary>
        public string Value { get; }
        /// <summary>创建只读显示字段。</summary>
        public TaskDetailField(string name, string value) { Name = name; Value = value; }
    }
}
