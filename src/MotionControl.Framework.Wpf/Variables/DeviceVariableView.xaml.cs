using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;

namespace MotionControl.Framework.Wpf.Variables
{
    /// <summary>显示、维护并监控当前单机的全局设备变量。</summary>
    public partial class DeviceVariableView : UserControl, INotifyPropertyChanged
    {
        /// <summary>运行时与设备流程共用的变量表。</summary>
        private readonly IDeviceVariableStore _variableStore;

        /// <summary>按当前 MachineId 保存变量定义的 JSON 配置服务。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>当前设备的稳定编号，用于隔离不同设备的变量定义。</summary>
        private readonly string _machineId;

        /// <summary>当前设备已连接时的整机运行状态。</summary>
        private MachineRuntime _machineRuntime;

        /// <summary>代码生成的选中行属性变化后通知 WPF 重新读取。</summary>
        private DeviceVariableRow _selectedVariable;

        /// <summary>刷新表格期间暂时屏蔽选中行事件。</summary>
        private bool _isRefreshingRows;

        /// <summary>创建设备变量页面并订阅运行时变量变化通知。</summary>
        public DeviceVariableView(
            IDeviceVariableStore variableStore,
            MachineConfigurationStore configurationStore,
            string machineId)
        {
            if (variableStore == null)
            {
                throw new ArgumentNullException(nameof(variableStore));
            }

            if (configurationStore == null)
            {
                throw new ArgumentNullException(nameof(configurationStore));
            }

            if (string.IsNullOrWhiteSpace(machineId))
            {
                throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            }

            InitializeComponent();
            _variableStore = variableStore;
            _configurationStore = configurationStore;
            _machineId = machineId;
            Variables = new ObservableCollection<DeviceVariableRow>();
            DataContext = this;
            _variableStore.VariablesChanged += VariableStore_VariablesChanged;
            RefreshRows();
            RefreshLocalizedText();
            UpdateActionAvailability();
        }

        /// <summary>当前变量表格行集合。</summary>
        public ObservableCollection<DeviceVariableRow> Variables { get; private set; }

        /// <summary>当前选择的变量，供工具栏编辑和写值操作使用。</summary>
        public DeviceVariableRow SelectedVariable
        {
            get { return _selectedVariable; }
            set
            {
                if (ReferenceEquals(_selectedVariable, value))
                {
                    return;
                }

                _selectedVariable = value;
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(nameof(SelectedVariable)));
                }

                UpdateActionAvailability();
            }
        }

        /// <summary>通知 WPF 页面绑定属性发生变化。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>把当前 Runtime 注入页面，用于设备运行期间锁定人工写入。</summary>
        public void SetMachineRuntime(MachineRuntime machineRuntime)
        {
            if (ReferenceEquals(_machineRuntime, machineRuntime))
            {
                UpdateActionAvailability();
                return;
            }

            if (_machineRuntime != null)
            {
                _machineRuntime.StatusChanged -= MachineRuntime_StatusChanged;
            }

            _machineRuntime = machineRuntime;
            if (_machineRuntime != null)
            {
                _machineRuntime.StatusChanged += MachineRuntime_StatusChanged;
            }

            UpdateActionAvailability();
        }

        /// <summary>切换界面语言后刷新表头、运行时数据类型和无效值提示。</summary>
        public void RefreshLocalizedText()
        {
            string[] columnHeaderResourceKeys =
            {
                "变量名称",
                "变量类型",
                "当前值",
                "最后更新时间",
                "变量说明"
            };

            for (int columnIndex = 0; columnIndex < VariablesGrid.Columns.Count; columnIndex++)
            {
                VariablesGrid.Columns[columnIndex].Header = GetLocalizedText(
                    columnHeaderResourceKeys[columnIndex]);
            }

            string noValueText = GetLocalizedText("变量无有效数据");
            foreach (DeviceVariableRow row in Variables)
            {
                row.UpdateTypeDisplay(GetTypeDisplayName(row.DataType));
                row.UpdateValueDisplay(noValueText);
            }

            UpdateActionAvailability();
        }

        /// <summary>运行时变量变化后在 WPF UI 线程刷新监控表格。</summary>
        private void VariableStore_VariablesChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess())
            {
                RefreshRows();
                return;
            }

            Dispatcher.BeginInvoke(new Action(RefreshRows), DispatcherPriority.DataBind);
        }

        /// <summary>整机运行状态变化后在 UI 线程更新人工操作的锁定状态。</summary>
        private void MachineRuntime_StatusChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess())
            {
                UpdateActionAvailability();
                return;
            }

            Dispatcher.BeginInvoke(new Action(UpdateActionAvailability), DispatcherPriority.DataBind);
        }

        /// <summary>按最新变量快照更新表格行，并保留现场人员的当前选择。</summary>
        private void RefreshRows()
        {
            string selectedName = SelectedVariable == null ? null : SelectedVariable.Name;
            var snapshots = _variableStore.GetSnapshot();
            _isRefreshingRows = true;

            foreach (DeviceVariableSnapshot snapshot in snapshots)
            {
                DeviceVariableRow row = Variables.FirstOrDefault(
                    item => string.Equals(item.Name, snapshot.Name, StringComparison.OrdinalIgnoreCase));
                if (row == null)
                {
                    row = new DeviceVariableRow(snapshot);
                    Variables.Add(row);
                }
                else
                {
                    row.Update(snapshot);
                }

                row.UpdateTypeDisplay(GetTypeDisplayName(row.DataType));
                row.UpdateValueDisplay(GetLocalizedText("变量无有效数据"));
            }

            foreach (DeviceVariableRow row in Variables.ToArray())
            {
                bool stillExists = snapshots.Any(
                    snapshot => string.Equals(snapshot.Name, row.Name, StringComparison.OrdinalIgnoreCase));
                if (!stillExists)
                {
                    Variables.Remove(row);
                }
            }

            SelectedVariable = selectedName == null
                ? null
                : Variables.FirstOrDefault(
                    row => string.Equals(row.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            _isRefreshingRows = false;
            UpdateActionAvailability();
        }

        /// <summary>根据设备运行状态和变量权限锁定工具栏操作。</summary>
        private void UpdateActionAvailability()
        {
            bool canEdit = CanEditDefinitions();
            bool canWrite = canEdit && SelectedVariable != null && SelectedVariable.AllowOperatorWrite;
            AddVariableButton.IsEnabled = canEdit;
            EditVariableButton.IsEnabled = canEdit && SelectedVariable != null;
            DeleteVariableButton.IsEnabled = canEdit && SelectedVariable != null;
            ManualValueTextBox.IsEnabled = canWrite;
            WriteValueButton.IsEnabled = canWrite;
            SetStatusResource(canEdit ? "变量状态可编辑" : "变量状态运行时锁定");
        }

        /// <summary>运行时只在停机、待复位或就绪状态允许现场调整变量。</summary>
        private bool CanEditDefinitions()
        {
            if (_machineRuntime == null)
            {
                return true;
            }

            MachineRunState state = _machineRuntime.State;
            return state == MachineRunState.Disconnected
                || state == MachineRunState.ResetRequired
                || state == MachineRunState.Ready;
        }

        /// <summary>新增一条变量定义，并立即保存到当前设备的 JSON 文件。</summary>
        private void AddVariableButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureEditingAllowed())
            {
                return;
            }

            var dialog = new DeviceVariableEditDialog
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true)
            {
                var definitions = _variableStore.GetDefinitions().ToList();
                definitions.Add(dialog.VariableDefinition);
                SaveDefinitions(definitions, "变量新增成功");
            }
        }

        /// <summary>编辑所选变量的名称、类型、初值、说明和现场写入权限。</summary>
        private void EditVariableButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureEditingAllowed() || SelectedVariable == null)
            {
                return;
            }

            var definitions = _variableStore.GetDefinitions().ToList();
            int selectedIndex = definitions.FindIndex(
                definition => string.Equals(
                    definition.Name,
                    SelectedVariable.Name,
                    StringComparison.OrdinalIgnoreCase));
            if (selectedIndex < 0)
            {
                ShowOperationMessage("变量定义已变化，请重新选择。", MessageBoxImage.Warning);
                return;
            }

            var dialog = new DeviceVariableEditDialog(definitions[selectedIndex])
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true)
            {
                definitions[selectedIndex] = dialog.VariableDefinition;
                SaveDefinitions(definitions, "变量保存成功");
            }
        }

        /// <summary>经现场确认后删除所选变量并保存更新后的变量定义。</summary>
        private void DeleteVariableButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureEditingAllowed() || SelectedVariable == null)
            {
                return;
            }

            string prompt = string.Format(
                GetLocalizedText("变量删除确认格式"),
                SelectedVariable.Name);
            MessageBoxResult result = MessageBox.Show(
                Window.GetWindow(this),
                prompt,
                GetLocalizedText("变量删除"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var definitions = _variableStore.GetDefinitions()
                .Where(definition => !string.Equals(
                    definition.Name,
                    SelectedVariable.Name,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            SaveDefinitions(definitions, "变量删除成功");
        }

        /// <summary>把输入框中的值按所选变量类型校验后写入运行时变量表。</summary>
        private void WriteValueButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditDefinitions())
            {
                SetStatusResource("变量状态运行时锁定");
                return;
            }

            if (SelectedVariable == null)
            {
                SetStatusResource("变量请先选择");
                return;
            }

            if (!SelectedVariable.AllowOperatorWrite)
            {
                SetStatusResource("变量禁止现场写入");
                return;
            }

            string errorMessage;
            if (!_variableStore.TrySetOperatorValue(
                SelectedVariable.Name,
                ManualValueTextBox.Text,
                out errorMessage))
            {
                StatusText.Text = errorMessage;
                return;
            }

            ManualValueTextBox.Clear();
            SetStatusResource("变量写入成功");
        }

        /// <summary>切换选择后刷新当前变量对应的编辑和写值按钮状态。</summary>
        private void VariablesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isRefreshingRows)
            {
                SelectedVariable = VariablesGrid.SelectedItem as DeviceVariableRow;
            }
        }

        /// <summary>校验定义列表、保存配置文件，再把新定义应用到本机运行时变量表。</summary>
        private void SaveDefinitions(
            System.Collections.Generic.IEnumerable<DeviceVariableDefinition> definitions,
            string successResourceKey)
        {
            try
            {
                var checkedDefinitions = DeviceVariableStore.ValidateDefinitions(definitions);
                var document = new DeviceVariableConfigurationDocument
                {
                    SchemaVersion = 1,
                    Variables = checkedDefinitions
                };
                document.Validate();
                _configurationStore.SaveJson(
                    _machineId,
                    "Parameters/DeviceVariables.json",
                    document);
                _variableStore.ReplaceDefinitions(checkedDefinitions);
                SetStatusResource(successResourceKey);
            }
            catch (Exception error)
            {
                StatusText.Text = error.Message;
                ShowOperationMessage(error.Message, MessageBoxImage.Error);
            }
        }

        /// <summary>在打开变量编辑窗口前再次确认设备处于允许维护的状态。</summary>
        private bool EnsureEditingAllowed()
        {
            if (CanEditDefinitions())
            {
                return true;
            }

            SetStatusResource("变量状态运行时锁定");
            return false;
        }

        /// <summary>按 Host 当前语言获取数据类型的可读名称。</summary>
        private string GetTypeDisplayName(string typeName)
        {
            switch (typeName)
            {
                case "Boolean": return GetLocalizedText("变量类型布尔");
                case "Integer": return GetLocalizedText("变量类型整数");
                case "Number": return GetLocalizedText("变量类型小数");
                case "String": return GetLocalizedText("变量类型字符串");
                default: return typeName;
            }
        }

        /// <summary>从 WPF 资源中读取当前语言文字，缺失时返回资源键方便定位。</summary>
        private string GetLocalizedText(string resourceKey)
        {
            object localizedValue = FindResource(resourceKey);
            return localizedValue == null ? resourceKey : localizedValue.ToString();
        }

        /// <summary>用 WPF 动态资源设置页面状态文字，使语言切换后仍能更新。</summary>
        private void SetStatusResource(string resourceKey)
        {
            StatusText.SetResourceReference(TextBlock.TextProperty, resourceKey);
        }

        /// <summary>向现场人员显示带有明确操作上下文的提示或错误。</summary>
        private void ShowOperationMessage(string message, MessageBoxImage icon)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                message,
                GetLocalizedText("设备变量"),
                MessageBoxButton.OK,
                icon);
        }
    }
}
