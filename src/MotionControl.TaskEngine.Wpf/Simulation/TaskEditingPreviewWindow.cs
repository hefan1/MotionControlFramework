using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Editing;

namespace MotionControl.TaskEngine.Wpf.Simulation
{
    /// <summary>独立任务编辑预览，目录与现场 Machine01 隔离，不创建控制卡。</summary>
    public sealed class TaskEditingPreviewWindow : Window
    {
        /// <summary>供人员查看与试编辑的独立页面。</summary>
        private readonly TaskConfigurationView _page;
        /// <summary>页面语言选择。</summary>
        private readonly ComboBox _language;
        /// <summary>建立只编辑文件的预览窗口。</summary>
        public TaskEditingPreviewWindow()
        {
            Title = "任务配置与列表编辑 — 独立预览"; Width = 1440; Height = 900; MinWidth = 1024; MinHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MotionControlFramework", "TaskEditingPreview");
            var store = CreatePreviewStore(root);
            _page = new TaskConfigurationView(store, "TaskEditorPreview", () => null);
            var panel = new DockPanel(); var footer = new DockPanel { Margin = new Thickness(10) }; DockPanel.SetDock(footer, Dock.Bottom);
            _language = new ComboBox { Width = 100, ItemsSource = new[] { "zh-CN", "en-US" }, SelectedIndex = 0 }; DockPanel.SetDock(_language, Dock.Right);
            footer.Children.Add(_language); footer.Children.Add(new TextBlock { Text = "独立预览 / Preview · " + root, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(footer); panel.Children.Add(_page); Content = panel;
            _language.SelectionChanged += LanguageChanged; Closing += PreviewClosing;
        }
        /// <summary>只切换页面资源。</summary>
        private void LanguageChanged(object sender, SelectionChangedEventArgs e) { _page.SetLanguage(_language.SelectedItem as string); }
        /// <summary>关闭前保留人员对草稿的决定。</summary>
        private void PreviewClosing(object sender, CancelEventArgs e) { e.Cancel = !_page.ConfirmClose(); }

        /// <summary>为独立预览和离线检查建立一致示例文件，已存在的任务不会被覆盖。</summary>
        public static MachineConfigurationStore CreatePreviewStore(string root)
        {
            const string machineId = "TaskEditorPreview";
            var store = new MachineConfigurationStore(root);
            if (File.Exists(Path.Combine(store.GetMachineDirectory(machineId), "Parameters/TaskMachine.json"))) return store;
            var session = TaskMonitorExample.Create(); var catalog = session.Device.Catalog;
            try
            {
                var axes = catalog.Axes.Values.ToList();
                store.SaveJson(machineId, "HardwareConfig/AxisParameters.json", new { schemaVersion = 1,
                    axes = axes.Select((axis, number) => new { name = axis.Name, controllerId = "Memory", axisNumber = number, engineeringUnit = axis.EngineeringUnit, minimumPosition = axis.MinimumPosition, maximumPosition = axis.MaximumPosition }).ToArray() });
                store.SaveJson(machineId, "HardwareConfig/IoMapping.json", new { schemaVersion = 1,
                    digitalInputs = catalog.Inputs.Select((name, channel) => new { name, controllerId = "Memory", channel, activeHigh = true }).ToArray(),
                    digitalOutputs = catalog.Outputs.Select((name, channel) => new { name, controllerId = "Memory", channel, activeHigh = true }).ToArray() });
                store.SaveJson(machineId, "Parameters/DeviceVariables.json", new { schemaVersion = 1,
                    variables = catalog.Variables.Select(item => new { name = item.Key, dataType = item.Value.ToString(), initialValue = item.Value == DeviceVariableType.Boolean ? "true" : null, allowOperatorWrite = true }).ToArray() });
                store.SaveJson(machineId, "Parameters/ManualAxisPositions.json", new { schemaVersion = 1,
                    axes = axes.Select((axis, number) => new { axisKey = "Memory.Axis" + number, positions = catalog.TeachPoints.Where(point => point.AxisName == axis.Name).Select(point => new { id = point.PointId, name = point.Name, position = point.Position }).ToArray() }).ToArray() });
                var definitions = session.Library.Values.ToList();
                foreach (var definition in definitions) new TaskDefinitionStore(store).Save(machineId, definition.TaskId + ".json", definition, catalog, definitions.Where(item => !ReferenceEquals(item, definition)));
                var config = new TaskMachineConfiguration { MachineId = machineId, Name = "任务编辑独立预览", NamedResources = new List<string> { "拍照区域" },
                    Stations = new List<StationTaskConfiguration> { PreviewStation("load", "上料工位", "Load"), PreviewStation("transfer", "搬运工位", "Transfer"), PreviewStation("inspect", "检测工位", "Inspect") } };
                new TaskMachineConfigurationStore(store).Save(machineId, config, definitions, catalog);
                return store;
            }
            finally { session.Machine.CloseAsync().GetAwaiter().GetResult(); }
        }
        /// <summary>预览只定义独立循环，不发起运行。</summary>
        private static StationTaskConfiguration PreviewStation(string id, string name, string task)
        { return new StationTaskConfiguration { StationId = id, Name = name, TaskId = task, Mode = StationTaskMode.Cyclic }; }
    }
}
