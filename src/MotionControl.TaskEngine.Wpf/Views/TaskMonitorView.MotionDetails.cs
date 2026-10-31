using System.Collections.Generic;
using System.Globalization;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>轴详情复用逻辑轴目录和原示教点，不创建第二份轴参数。</summary>
    public partial class TaskMonitorView
    {
        /// <summary>根据单轴、轴组或轴命令显示相关参数与配置入口。</summary>
        private void AddMotionDetails(TaskNode node, List<TaskDetailField> fields, List<TaskDetailField> parameters,
            List<TaskReferenceEventArgs> references)
        {
            if (node is MoveAxisNode move) AddAxis(move.AxisName, move.Mode, move.Target, move.Profile, parameters, references);
            else if (node is MoveAxisGroupNode group)
            {
                fields.Add(Field("完成条件", T("全部轴到位后继续，独立速度不保证直线路径")));
                foreach (var target in group.Targets) AddAxis(target.AxisName, target.Mode, target.Target, target.Profile, parameters, references);
            }
            else if (node is AxisCommandNode command)
            {
                parameters.Add(Field("轴", command.AxisName));
                parameters.Add(Field("动作", T(command.Command == TaskAxisCommand.Home ? "回零" : "确认停止")));
                fields.Add(Field("执行范围", T("仅内存轴，不模拟厂商回零信号")));
                AxisReferences(command.AxisName, references);
            }
            else if (node is CoordinatedMoveNode) fields.Add(Field("执行范围", T("协调插补未接入")));
        }

        /// <summary>每根轴独立展示单位、目标来源和工艺速度，手动页速度不代替工艺速度。</summary>
        private void AddAxis(string name, TaskMoveMode mode, TaskValueBinding target, TaskMotionProfile profile,
            List<TaskDetailField> parameters, List<TaskReferenceEventArgs> references)
        {
            var axis = _session.TaskDevice.Catalog.Axes[name];
            string unit = axis.EngineeringUnit ?? T("单位未配置");
            parameters.Add(new TaskDetailField(name, T(mode == TaskMoveMode.Absolute ? "绝对定位" : "相对位移")));
            parameters.Add(Field("目标来源", BindingText(target, references) + " " + unit));
            parameters.Add(Field("速度", profile.Speed.ToString(CultureInfo.InvariantCulture) + " " + unit + "/s"));
            parameters.Add(Field("加减速度", profile.Acceleration + " / " + profile.Deceleration + " " + unit + "/s²"));
            parameters.Add(Field("所属控制卡", axis.ControllerId));
            AxisReferences(name, references);
        }

        /// <summary>逻辑轴同时关联轴映射与已有手动示教页面。</summary>
        private void AxisReferences(string name, List<TaskReferenceEventArgs> references)
        {
            references.Add(new TaskReferenceEventArgs("轴映射配置", name));
            references.Add(new TaskReferenceEventArgs("轴手动", name));
        }

        /// <summary>从同一目录解析变量类型和示教点名称，引用值不作为历史动作目标。</summary>
        private string BindingText(TaskValueBinding binding, List<TaskReferenceEventArgs> references)
        {
            if (binding.Source == TaskValueSource.Variable)
            {
                references.Add(new TaskReferenceEventArgs("设备变量", binding.VariableName));
                return binding.VariableName + " (" + VariableTypeName(_session.TaskDevice.Catalog.Variables[binding.VariableName].ToString()) + ")";
            }
            if (binding.Source == TaskValueSource.TeachPoint)
            {
                AxisReferences(binding.AxisName, references);
                var point = _session.TaskDevice.Catalog.FindTeachPoint(binding.AxisName, binding.PointId);
                return binding.AxisName + " / " + point.Name + " [" + point.PointId + "] = " + point.Position;
            }
            return binding.Constant;
        }

        /// <summary>显示熟悉的 .NET 类型名，保持存储名称兼容。</summary>
        internal static string VariableTypeName(string type)
        {
            switch (type)
            {
                case "Boolean": return "bool"; case "Integer": return "int"; case "Number": return "double";
                case "String": return "string"; case "Byte": return "byte"; case "Character": return "char";
                case "UInt32": return "uint"; default: return type;
            }
        }
    }
}
