using System;
using System.Collections.Generic;
using System.Windows.Data;
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

        /// <summary>Host 查询已加载任务或正在装载、关闭的命令，覆盖后台读取目录但尚未接管控制权的阶段。</summary>
        public Func<bool> HasTaskSessionOrCommand { get; set; }

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
            Loaded += DeviceVariableView_Loaded;
            RefreshRows();
            RefreshLocalizedText();
            UpdateActionAvailability();
        }

        /// <summary>视图筛选期间保留隐藏选择身份，避免绑定自动清空目标。</summary>
        private bool _refreshingSearch;
        /// <summary>各真实变量名对应的未提交文本，不写入变量服务。</summary>
        private readonly Dictionary<string, string> _pendingInputByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前变量表格行集合。</summary>
        public ObservableCollection<DeviceVariableRow> Variables { get; private set; }

        /// <summary>当前选择的变量，供工具栏编辑和写值操作使用。</summary>
        public DeviceVariableRow SelectedVariable
        {
            get { return _selectedVariable; }
            set
            {
                if (_refreshingSearch && value == null) return;
                if (ReferenceEquals(_selectedVariable, value))
                {
                    return;
                }

                if (ManualValueTextBox != null && _selectedVariable != null) _pendingInputByName[_selectedVariable.Name] = ManualValueTextBox.Text;
                _selectedVariable = value;
                if (ManualValueTextBox != null)
                {
                    string pending;
                    ManualValueTextBox.Text = value != null && _pendingInputByName.TryGetValue(value.Name, out pending) ? pending : string.Empty;
                }
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(nameof(SelectedVariable)));
                }

                UpdateActionAvailability();
            }
        }

        /// <summary>从任务步骤选择设备变量并滚动到该行，不改写变量定义或当前值。</summary>
        public bool FocusVariableByName(string variableName)
        {
            DeviceVariableRow variable = Variables.FirstOrDefault(item =>
                string.Equals(item.Name, variableName, StringComparison.OrdinalIgnoreCase));
            if (variable == null) return false;
            // 外页明确定位的对象须实际可见；只清显示查询，不改定义、值或权限。
            if (!variable.IsSearchMatch)
            {
                VariableSearchBox.Clear();
                RefreshVariableSearch();
            }
            VariablesGrid.SelectedItem = variable;
            VariablesGrid.ScrollIntoView(variable);
            SelectedVariable = variable;
            return true;
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
                _machineRuntime.PreparationChanged -= MachineRuntime_StatusChanged;
            }

            _machineRuntime = machineRuntime;
            if (_machineRuntime != null)
            {
                _machineRuntime.StatusChanged += MachineRuntime_StatusChanged;
                _machineRuntime.PreparationChanged += MachineRuntime_StatusChanged;
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
                "变量工程单位",
                "变量说明",
                "变量人工可写"
            };

            for (int columnIndex = 0; columnIndex < columnHeaderResourceKeys.Length; columnIndex++)
            {
                VariablesGrid.Columns[columnIndex].Header = GetLocalizedText(
                    columnHeaderResourceKeys[columnIndex]);
            }

            RefreshVariableSearch();
            string noValueText = GetLocalizedText("变量无有效数据");
            foreach (DeviceVariableRow row in Variables)
            {
                row.UpdateTypeDisplay(GetTypeDisplayName(row.DataType));
                row.UpdateValueDisplay(noValueText, GetLocalizedText("变量元数据未定义"), GetLocalizedText("变量单位不适用"));
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

        /// <summary>重新进入页面时读取最新任务会话状态，不沿用离开页面前的按钮许可。</summary>
        private void DeviceVariableView_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateActionAvailability();
        }

        /// <summary>整机状态或准备状态变化后，在 UI 线程更新定义维护与人工写值的独立许可。</summary>
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
                    if (!HasSameInputMeaning(row, snapshot))
                    {
                        _pendingInputByName.Remove(row.Name);
                        if (ReferenceEquals(SelectedVariable, row)) ManualValueTextBox.Clear();
                    }
                    row.Update(snapshot);
                }

                row.UpdateTypeDisplay(GetTypeDisplayName(row.DataType));
                row.UpdateValueDisplay(GetLocalizedText("变量无有效数据"), GetLocalizedText("变量元数据未定义"), GetLocalizedText("变量单位不适用"));
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
            RefreshVariableSearch();
            UpdateActionAvailability();
        }

        /// <summary>搜索只折叠不匹配行，保留绑定集合、选中变量及未提交写入文字。</summary>
        private void VariableSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshVariableSearch();
        }

        /// <summary>清除显示条件，不改变任何变量值或操作门禁。</summary>
        private void ClearVariableSearch_Click(object sender, RoutedEventArgs e)
        {
            VariableSearchBox.Text = string.Empty;
        }

        /// <summary>按名称与说明更新行显示和匹配计数，变量刷新后沿用当前条件。</summary>
        private void RefreshVariableSearch()
        {
            if (Variables == null || VariableSearchBox == null || VariableSearchCountText == null) return;
            string query = (VariableSearchBox.Text ?? string.Empty).Trim();
            foreach (var row in Variables) row.UpdateSearchMatch(query);
            _refreshingSearch = true;
            try
            {
                var view = CollectionViewSource.GetDefaultView(Variables);
                view.Filter = value => ((DeviceVariableRow)value).IsSearchMatch;
                // 明确绑定同一个已筛选视图，避免网格另取集合视图导致筛选只更新计数。
                if (!ReferenceEquals(VariablesGrid.ItemsSource, view)) VariablesGrid.ItemsSource = view;
                view.Refresh();
                VariablesGrid.SelectedItem = SelectedVariable != null && SelectedVariable.IsSearchMatch ? SelectedVariable : null;
            }
            finally { _refreshingSearch = false; }
            UpdateActionAvailability();
            VariableSearchCountText.Text = string.Format(GetLocalizedText("变量筛选数量"), Variables.Count(row => row.IsSearchMatch), Variables.Count);
        }

        /// <summary>根据设备运行状态和变量权限锁定工具栏操作。</summary>
        private void UpdateActionAvailability()
        {
            string definitionBlock = GetDefinitionEditBlockResourceKey();
            bool canEdit = definitionBlock == null;
            bool visibleTarget = SelectedVariable != null && SelectedVariable.IsSearchMatch;
            bool canWrite = CanWriteCurrentValue() && visibleTarget && SelectedVariable.AllowOperatorWrite;
            AddVariableButton.IsEnabled = canEdit;
            EditVariableButton.IsEnabled = canEdit && visibleTarget;
            DeleteVariableButton.IsEnabled = canEdit && visibleTarget;
            ManualValueTextBox.IsEnabled = canWrite;
            WriteValueButton.IsEnabled = canWrite;
            UpdateSelectedVariableContext();
            SetStatusResource(SelectedVariable != null && !SelectedVariable.IsSearchMatch
                ? "变量筛选目标隐藏" : definitionBlock ?? "变量状态可编辑");
        }

        /// <summary>显示当前选中变量的名称、类型、值和人工写入权限。</summary>
        private void UpdateSelectedVariableContext()
        {
            UpdateSelectedDefinitionSummary();
            if (SelectedVariable == null)
            {
                SelectedVariableContextText.Text = GetLocalizedText("变量写入上下文未选择");
                return;
            }

            string currentValue = SelectedVariable.HasValue
                ? GetConfirmationValue(SelectedVariable.DataType, SelectedVariable.CurrentValue)
                : GetLocalizedText("变量无有效数据");
            string resourceKey = SelectedVariable.AllowOperatorWrite
                ? "变量写入上下文可写"
                : "变量写入上下文只读";
            SelectedVariableContextText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                GetLocalizedText(resourceKey),
                SelectedVariable.Name,
                SelectedVariable.DataTypeDisplay,
                currentValue);
        }

        /// <summary>只读展示当前服务定义与内存值，缺项不以初始值冒充运行值。</summary>
        private void UpdateSelectedDefinitionSummary()
        {
            if (SelectedVariable == null)
            {
                SelectedDefinitionSummaryText.Text = GetLocalizedText("变量摘要未选择");
                return;
            }
            string missing = GetLocalizedText("变量摘要未提供");
            string initial = SelectedVariable.InitialValue == null
                ? missing : SelectedVariable.InitialValue.Length == 0 && !string.Equals(SelectedVariable.DataType, "String", StringComparison.OrdinalIgnoreCase)
                    ? missing : GetConfirmationValue(SelectedVariable.DataType, SelectedVariable.InitialValue);
            string description = string.IsNullOrWhiteSpace(SelectedVariable.Description) ? missing : SelectedVariable.Description;
            string current = SelectedVariable.HasValue ? GetConfirmationValue(SelectedVariable.DataType, SelectedVariable.CurrentValue) : GetLocalizedText("变量无有效数据");
            string undefined = GetLocalizedText("变量元数据未定义");
            SelectedDefinitionSummaryText.Text = string.Format(GetLocalizedText("变量定义运行摘要格式"), SelectedVariable.NameDisplay, SelectedVariable.DataTypeDisplay, initial, description, current)
                + Environment.NewLine + string.Format(GetLocalizedText("变量元数据摘要格式"), SelectedVariable.EngineeringUnitDisplay,
                    string.IsNullOrWhiteSpace(SelectedVariable.Dimension) ? undefined : SelectedVariable.Dimension,
                    SelectedVariable.MinimumValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? undefined,
                    SelectedVariable.MaximumValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? undefined,
                    string.IsNullOrWhiteSpace(SelectedVariable.SourceId) ? undefined : SelectedVariable.SourceId,
                    string.IsNullOrWhiteSpace(SelectedVariable.SourceDescription) ? undefined : SelectedVariable.SourceDescription)
                + (string.IsNullOrWhiteSpace(SelectedVariable.ValueNotice) ? string.Empty : Environment.NewLine + SelectedVariable.ValueNotice);
        }

        /// <summary>输入框内容属于原变量含义；单位、类型、来源或范围改变后不能复用旧的待写文本。</summary>
        private static bool HasSameInputMeaning(DeviceVariableRow row, DeviceVariableSnapshot snapshot)
        {
            return string.Equals(row.DataType, snapshot.DataType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NumericValueMetadata.NormalizeUnit(row.EngineeringUnit), NumericValueMetadata.NormalizeUnit(snapshot.EngineeringUnit), StringComparison.Ordinal)
                && string.Equals(NumericValueMetadata.NormalizeText(row.Dimension), NumericValueMetadata.NormalizeText(snapshot.Dimension), StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.SourceId ?? string.Empty, snapshot.SourceId ?? string.Empty, StringComparison.Ordinal)
                && row.MinimumValue == snapshot.MinimumValue && row.MaximumValue == snapshot.MaximumValue;
        }

        /// <summary>当前值仍使用原整机状态许可；定义被任务目录锁定时，不额外禁止合法的现场写值。</summary>
        private bool CanWriteCurrentValue()
        {
            return DeviceVariableEditAccess.CanWriteCurrentValue(_machineRuntime?.State);
        }

        /// <summary>任务会话使用冻结目录；关闭会话并完成设备操作后，才允许更改定义并重新读取任务。</summary>
        private string GetDefinitionEditBlockResourceKey()
        {
            bool taskContextActive = (HasTaskSessionOrCommand != null && HasTaskSessionOrCommand())
                || (_machineRuntime != null && _machineRuntime.IsTaskEngineControlClaimed);
            bool deviceOperationActive = _machineRuntime != null && (_machineRuntime.IsPreparing
                || _machineRuntime.IsClearing || _machineRuntime.IsShutdownInProgress);
            return DeviceVariableEditAccess.DefinitionBlockResourceKey(_machineRuntime?.State,
                taskContextActive, deviceOperationActive);
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

        /// <summary>编辑显示元数据、类型、初值和权限；既有稳定名称不能在此绕过引用迁移。</summary>
        private void EditVariableButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureVisibleVariableTarget() || !EnsureEditingAllowed() || SelectedVariable == null)
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
            if (!EnsureVisibleVariableTarget() || !EnsureEditingAllowed() || SelectedVariable == null)
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

        /// <summary>校验待写入值，向现场人员展示新旧值并在确认后更新运行时变量。</summary>
        private void WriteValueButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureVisibleVariableTarget()) return;
            if (!CanWriteCurrentValue())
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

            string targetName = SelectedVariable.Name;
            DeviceVariableDefinition expectedDefinition = _variableStore.GetDefinitions().FirstOrDefault(
                item => string.Equals(item.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (expectedDefinition == null)
            {
                StatusText.Text = GetLocalizedText("变量写入定义已变化");
                return;
            }
            if (!MatchesWriteDefinition(SelectedVariable, expectedDefinition))
            {
                ManualValueTextBox.Clear();
                _pendingInputByName.Remove(targetName);
                RefreshRows();
                StatusText.Text = GetLocalizedText("变量写入定义已变化");
                return;
            }
            DeviceVariableDefinition inputDefinition = expectedDefinition.Copy();
            inputDefinition.InitialValue = ManualValueTextBox.Text;
            string normalizedValue;
            try
            {
                normalizedValue = DeviceVariableStore.ValidateDefinitions(new[] { inputDefinition })[0].InitialValue;
            }
            catch (Exception error)
            {
                StatusText.Text = error.Message;
                ShowOperationMessage(error.Message, MessageBoxImage.Warning);
                return;
            }

            string previousValue = SelectedVariable.HasValue
                ? GetConfirmationValue(SelectedVariable.DataType, SelectedVariable.CurrentValue)
                : GetLocalizedText("变量无有效数据");
            string nextValue = GetConfirmationValue(SelectedVariable.DataType, normalizedValue);
            if (!string.IsNullOrWhiteSpace(expectedDefinition.EngineeringUnit))
            {
                previousValue += " " + expectedDefinition.EngineeringUnit;
                nextValue += " " + expectedDefinition.EngineeringUnit;
            }
            string confirmationText = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                GetLocalizedText("变量写入确认格式"),
                targetName,
                SelectedVariable.DataTypeDisplay,
                previousValue,
                nextValue);
            MessageBoxResult confirmation = MessageBox.Show(
                Window.GetWindow(this),
                confirmationText,
                GetLocalizedText("变量写入确认"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            if (!CanWriteCurrentValue() || SelectedVariable == null
                || !string.Equals(SelectedVariable.Name, targetName, StringComparison.OrdinalIgnoreCase)
                || !SelectedVariable.IsSearchMatch)
            {
                StatusText.Text = GetLocalizedText("变量写入定义已变化");
                return;
            }

            string errorMessage;
            if (!_variableStore.TrySetOperatorValue(
                targetName,
                normalizedValue,
                out errorMessage,
                expectedDefinition))
            {
                StatusText.Text = errorMessage;
                return;
            }

            ManualValueTextBox.Clear();
            SetStatusResource("变量写入成功");
        }

        /// <summary>点击时核对表格显示的含义与服务当前定义一致，不能用尚未刷新的旧输入提交到新来源。</summary>
        private static bool MatchesWriteDefinition(DeviceVariableRow row, DeviceVariableDefinition definition)
        {
            return string.Equals(row.DataType, definition.DataType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NumericValueMetadata.NormalizeUnit(row.EngineeringUnit), NumericValueMetadata.NormalizeUnit(definition.EngineeringUnit), StringComparison.Ordinal)
                && string.Equals(NumericValueMetadata.NormalizeText(row.Dimension), NumericValueMetadata.NormalizeText(definition.Dimension), StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.SourceId ?? string.Empty, definition.SourceId ?? string.Empty, StringComparison.Ordinal)
                && row.MinimumValue == definition.MinimumValue && row.MaximumValue == definition.MaximumValue
                && row.AllowOperatorWrite == definition.AllowOperatorWrite;
        }

        /// <summary>在确认内容中明确显示空字符串，并保留其他类型的原始格式。</summary>
        private string GetConfirmationValue(string dataType, string valueText)
        {
            if (string.Equals(dataType, "String", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(valueText))
            {
                return GetLocalizedText("变量空字符串");
            }

            return valueText ?? string.Empty;
        }

        /// <summary>事件提交时再次拒绝隐藏变量，防止绕过按钮禁用误改不可见对象。</summary>
        private bool EnsureVisibleVariableTarget()
        {
            if (SelectedVariable == null || SelectedVariable.IsSearchMatch) return true;
            SetStatusResource("变量筛选目标隐藏");
            return false;
        }
        /// <summary>切换选择后刷新当前变量对应的编辑和写值按钮状态。</summary>
        private void VariablesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isRefreshingRows && !_refreshingSearch)
            {
                SelectedVariable = VariablesGrid.SelectedItem as DeviceVariableRow;
            }
        }

        /// <summary>弹窗返回后重新核对定义维护许可，再保存文件并应用；不自动替换已加载任务的冻结目录。</summary>
        private void SaveDefinitions(
            System.Collections.Generic.IEnumerable<DeviceVariableDefinition> definitions,
            string successResourceKey)
        {
            if (!EnsureEditingAllowed()) return;
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

        /// <summary>打开编辑窗口和最终保存都核对任务会话与设备状态，禁止把旧任务目录配上新变量含义。</summary>
        private bool EnsureEditingAllowed()
        {
            string block = GetDefinitionEditBlockResourceKey();
            if (block == null)
            {
                return true;
            }

            SetStatusResource(block);
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
                case "Byte": return GetLocalizedText("变量类型字节");
                case "Character": return GetLocalizedText("变量类型字符");
                case "DateTime": return GetLocalizedText("变量类型日期时间");
                case "UInt32": return GetLocalizedText("变量类型无符号32位整数");
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
