using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>只读检查任务资源关联，并提供当前工位运行状态的直接入口。</summary>
    internal sealed class TaskMonitorLauncher : UserControl
    {
        /// <summary>复用当前设备配置服务。</summary>
        private readonly MachineConfigurationStore _store;
        /// <summary>当前设备标识。</summary>
        private readonly string _machineId;
        /// <summary>负责导航到已有轴、IO 和变量页面的主窗口。</summary>
        private readonly MainWindow _host;
        /// <summary>目录读取结果和错误反馈。</summary>
        private readonly TextBlock _status;
        /// <summary>默认收起的完整错误详情，供维护人员定位配置和异常来源。</summary>
        private readonly Expander _diagnostics;
        /// <summary>可复制的原始异常内容。</summary>
        private readonly TextBox _diagnosticText;
        /// <summary>最近一次失败，用于语言切换后重新生成操作提示。</summary>
        private Exception _lastError;
        /// <summary>最近一次失败所属的操作资源键。</summary>
        private string _failureOperationKey;
        /// <summary>最近一次失败后的建议资源键。</summary>
        private string _failureAdviceKey;
        /// <summary>当前诊断对应的配置页快捷入口。</summary>
        private readonly WrapPanel _nextStepButtons;
        /// <summary>创建明确的配置入口；不会从此处连接控制卡。</summary>
        internal TaskMonitorLauncher(MachineConfigurationStore store, string machineId, MainWindow host)
        {
            _store = store; _machineId = machineId; _host = host;
            var panel = new StackPanel { Margin = new Thickness(24) };
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12), FontSize = 14 };
            note.SetResourceReference(TextBlock.TextProperty, "任务接入状态说明"); panel.Children.Add(note);
            var buttons = new WrapPanel();
            AddButton(buttons, "设备总览", EquipmentOverview_Click);
            AddButton(buttons, "流程状态", FlowStatus_Click);
            AddButton(buttons, "刷新关联配置目录", RefreshCatalog_Click);
            panel.Children.Add(buttons);
            _nextStepButtons = new WrapPanel { Margin = new Thickness(0,10,0,0), Visibility = Visibility.Collapsed };
            panel.Children.Add(_nextStepButtons);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,14,0,0) };
            panel.Children.Add(_status);
            _diagnosticText = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            _diagnostics = new Expander { Margin = new Thickness(0,8,0,0),
                Visibility = Visibility.Collapsed, Content = _diagnosticText };
            _diagnostics.SetResourceReference(HeaderedContentControl.HeaderProperty, "任务关联诊断详情");
            panel.Children.Add(_diagnostics); Content = panel;
            _host.LanguageChanged += Host_LanguageChanged;
            Loaded += Launcher_Loaded;
        }

        /// <summary>用中文资源键创建按钮，语言切换自动刷新。</summary>
        private static void AddButton(Panel panel, string key, RoutedEventHandler handler)
        {
            var button = new Button { Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,10,0) };
            button.SetResourceReference(ContentControl.ContentProperty, key); button.Click += handler; panel.Children.Add(button);
        }
        /// <summary>显示最新目录数量，不缓存为第二份映射。</summary>
        private void Launcher_Loaded(object sender, RoutedEventArgs e) { RefreshCatalog(); }
        /// <summary>重新读取其他页面已保存的配置。</summary>
        private void RefreshCatalog_Click(object sender, RoutedEventArgs e) { RefreshCatalog(); }
        /// <summary>打开实际展示当前设备运行状态的总览页。</summary>
        private void EquipmentOverview_Click(object sender, RoutedEventArgs e) { _host.ShowEquipmentOverview(); }
        /// <summary>打开实际展示当前工位运行步骤的流程状态页。</summary>
        private void FlowStatus_Click(object sender, RoutedEventArgs e) { _host.ShowFlowStatus(); }
        /// <summary>共享目录加载失败时给出明确文件错误。</summary>
        private void RefreshCatalog()
        {
            try
            {
                var namedResources = new MotionControl.TaskEngine.Configuration.TaskMachineConfigurationStore(_store)
                    .LoadNamedResources(_machineId);
                var catalog = new TaskResourceCatalogLoader(_store).Load(_machineId, namedResources);
                ClearError();
                _status.Text = string.Format((string)FindResource("任务关联目录统计"), catalog.Axes.Count, catalog.Inputs.Count,
                    catalog.Outputs.Count, catalog.Variables.Count, catalog.TeachPoints.Count)
                    + "\n" + _store.GetMachineDirectory(_machineId) + "\n" + string.Join("\n", catalog.Notices);
            }
            catch (Exception error)
            {
                ShowError(error, "任务目录读取失败", "任务配置检查建议");
            }
        }
        /// <summary>保留异常详情，同时显示操作人员可执行的下一步。</summary>
        private void ShowError(Exception error, string operationKey, string adviceKey)
        {
            _lastError = error;
            _failureOperationKey = operationKey;
            _failureAdviceKey = adviceKey;
            _diagnosticText.Text = error.ToString();
            _diagnostics.IsExpanded = false;
            _diagnostics.Visibility = Visibility.Visible;
            UpdateNextStepButton(error);
            RefreshErrorText();
        }

        /// <summary>对已知缺失配置提供直达入口，不生成默认映射或自动保存。</summary>
        private void UpdateNextStepButton(Exception error)
        {
            _nextStepButtons.Children.Clear();
            _nextStepButtons.Visibility = Visibility.Collapsed;

            var missingFile = error as FileNotFoundException;
            if (missingFile == null || string.IsNullOrWhiteSpace(missingFile.FileName))
            {
                return;
            }

            string fileName = Path.GetFileName(missingFile.FileName);
            string page;
            string buttonResource;
            if (string.Equals(fileName, "AxisParameters.json", StringComparison.OrdinalIgnoreCase))
            {
                page = "轴映射配置";
                buttonResource = "打开轴映射配置";
            }
            else if (string.Equals(fileName, "IoMapping.json", StringComparison.OrdinalIgnoreCase))
            {
                page = "IO映射";
                buttonResource = "打开IO映射配置";
            }
            else if (string.Equals(fileName, "TaskMachine.json", StringComparison.OrdinalIgnoreCase))
            {
                page = "任务配置";
                buttonResource = "打开任务配置";
            }
            else if (string.Equals(fileName, "DeviceVariables.json", StringComparison.OrdinalIgnoreCase))
            {
                page = "设备变量";
                buttonResource = "打开设备变量";
            }
            else
            {
                return;
            }

            AddButton(_nextStepButtons, buttonResource, (sender, args) => _host.ShowConfigurationPage(page));
            _nextStepButtons.Visibility = Visibility.Visible;
        }

        /// <summary>语言切换后保留当前错误并更新提示，不重新读取或更改配置。</summary>
        private void Host_LanguageChanged(object sender, EventArgs e)
        {
            if (_lastError != null) RefreshErrorText();
        }

        /// <summary>显示失败操作、文件路径或错误原因，以及下一步建议。</summary>
        private void RefreshErrorText()
        {
            string reason = _lastError.Message;
            var missingFile = _lastError as FileNotFoundException;
            if (missingFile != null && !string.IsNullOrWhiteSpace(missingFile.FileName))
                reason = string.Format((string)_host.FindResource("任务配置文件不存在"), missingFile.FileName);

            _status.Text = (string)_host.FindResource(_failureOperationKey) + "\n" + reason + "\n"
                + string.Format((string)_host.FindResource("任务配置设备目录"), _store.GetMachineDirectory(_machineId))
                + "\n" + (string)_host.FindResource(_failureAdviceKey);
        }

        /// <summary>成功后清除过期的错误详情。</summary>
        private void ClearError()
        {
            _lastError = null;
            _failureOperationKey = null;
            _failureAdviceKey = null;
            _diagnosticText.Text = string.Empty;
            _diagnostics.IsExpanded = false;
            _diagnostics.Visibility = Visibility.Collapsed;
            _nextStepButtons.Children.Clear();
            _nextStepButtons.Visibility = Visibility.Collapsed;
        }
    }
}
