using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Configuration;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>选择一个已映射的逻辑 IO，并填写轴卡片显示文字。</summary>
    public partial class ManualAxisIoAssociationDialog : Window
    {
        /// <summary>可供当前轴关联的逻辑输入。</summary>
        private readonly List<DigitalIoHardwareDefinition> _digitalInputs;

        /// <summary>可供当前轴关联的逻辑输出。</summary>
        private readonly List<DigitalIoHardwareDefinition> _digitalOutputs;

        /// <summary>用于避免同一轴重复关联同一个逻辑点。</summary>
        private readonly List<ManualAxisIoAssociation> _existingAssociations;

        /// <summary>正在编辑的配置；新增 IO 时为空。</summary>
        private readonly ManualAxisIoAssociation _editingAssociation;

        /// <summary>保存按钮验证通过后生成的关联配置。</summary>
        public ManualAxisIoAssociation Result { get; private set; }

        /// <summary>使用当前设备已命名的逻辑 IO 创建新增或编辑窗口。</summary>
        public ManualAxisIoAssociationDialog(
            IEnumerable<DigitalIoHardwareDefinition> digitalInputs,
            IEnumerable<DigitalIoHardwareDefinition> digitalOutputs,
            IEnumerable<ManualAxisIoAssociation> existingAssociations,
            ManualAxisIoAssociation editingAssociation)
        {
            if (digitalInputs == null)
            {
                throw new ArgumentNullException(nameof(digitalInputs));
            }

            if (digitalOutputs == null)
            {
                throw new ArgumentNullException(nameof(digitalOutputs));
            }

            if (existingAssociations == null)
            {
                throw new ArgumentNullException(nameof(existingAssociations));
            }

            _digitalInputs = digitalInputs
                .Where(point => point != null && !string.IsNullOrWhiteSpace(point.Name))
                .ToList();
            _digitalOutputs = digitalOutputs
                .Where(point => point != null && !string.IsNullOrWhiteSpace(point.Name))
                .ToList();
            _existingAssociations = existingAssociations.ToList();
            _editingAssociation = editingAssociation;

            InitializeComponent();
            PointTypeComboBox.SelectedIndex = editingAssociation != null
                && editingAssociation.PointType == ManualAxisIoPointType.DigitalOutput ? 1 : 0;
            DisplayNameTextBox.Text = editingAssociation == null ? string.Empty : editingAssociation.DisplayName;
            bool editingOutput = editingAssociation != null
                && editingAssociation.PointType == ManualAxisIoPointType.DigitalOutput;
            OnActionNameTextBox.Text = editingOutput
                ? editingAssociation.OnActionName
                : GetText("默认逻辑ON按钮名称");
            OffActionNameTextBox.Text = editingOutput
                ? editingAssociation.OffActionName
                : GetText("默认逻辑OFF按钮名称");
            UpdateAvailablePointNames();
        }

        /// <summary>按 DI/DO 类型刷新下拉框，并保留正在编辑的已映射名称。</summary>
        private void PointTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PointNameComboBox == null)
            {
                return;
            }

            UpdateAvailablePointNames();
        }

        /// <summary>刷新候选项，只显示当前轴尚未关联的同类型逻辑点。</summary>
        private void UpdateAvailablePointNames()
        {
            ManualAxisIoPointType pointType = GetSelectedPointType();
            List<DigitalIoHardwareDefinition> availablePoints = pointType == ManualAxisIoPointType.DigitalInput
                ? _digitalInputs
                : _digitalOutputs;
            string editingName = _editingAssociation != null && _editingAssociation.PointType == pointType
                ? _editingAssociation.PointName
                : null;
            var usedNames = new HashSet<string>(
                _existingAssociations
                    .Where(item => item != null && !ReferenceEquals(item, _editingAssociation)
                        && item.PointType == pointType)
                    .Select(item => item.PointName),
                StringComparer.OrdinalIgnoreCase);
            List<DigitalIoHardwareDefinition> choices = availablePoints
                .Where(point => !usedNames.Contains(point.Name)
                    || string.Equals(point.Name, editingName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            PointNameComboBox.ItemsSource = choices;
            PointNameComboBox.SelectedItem = choices.FirstOrDefault(point =>
                string.Equals(point.Name, editingName, StringComparison.OrdinalIgnoreCase));
            OutputActionPanel.Visibility = pointType == ManualAxisIoPointType.DigitalOutput
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>验证当前输入并返回新建或更新后的 IO 关联配置。</summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DigitalIoHardwareDefinition selectedPoint = PointNameComboBox.SelectedItem as DigitalIoHardwareDefinition;
            string displayName = DisplayNameTextBox.Text == null ? string.Empty : DisplayNameTextBox.Text.Trim();
            string onActionName = OnActionNameTextBox.Text == null ? string.Empty : OnActionNameTextBox.Text.Trim();
            string offActionName = OffActionNameTextBox.Text == null ? string.Empty : OffActionNameTextBox.Text.Trim();

            if (selectedPoint == null)
            {
                ValidationMessageText.Text = GetText("请先选择逻辑IO");
                return;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                ValidationMessageText.Text = GetText("IO显示名称不能为空");
                return;
            }

            ManualAxisIoPointType pointType = GetSelectedPointType();
            if (pointType == ManualAxisIoPointType.DigitalOutput
                && (string.IsNullOrWhiteSpace(onActionName)
                    || string.IsNullOrWhiteSpace(offActionName)
                    || string.Equals(onActionName, offActionName, StringComparison.OrdinalIgnoreCase)))
            {
                ValidationMessageText.Text = GetText("DO按钮名称必须不同");
                return;
            }

            ManualAxisIoAssociation association = _editingAssociation == null
                ? new ManualAxisIoAssociation { Id = Guid.NewGuid().ToString("N") }
                : CloneAssociation(_editingAssociation);
            association.PointType = pointType;
            association.PointName = selectedPoint.Name;
            association.DisplayName = displayName;
            association.OnActionName = pointType == ManualAxisIoPointType.DigitalOutput ? onActionName : string.Empty;
            association.OffActionName = pointType == ManualAxisIoPointType.DigitalOutput ? offActionName : string.Empty;
            Result = association;
            DialogResult = true;
        }

        /// <summary>读取类型下拉框当前选中的 DI 或 DO 值。</summary>
        private ManualAxisIoPointType GetSelectedPointType()
        {
            ComboBoxItem selectedItem = PointTypeComboBox.SelectedItem as ComboBoxItem;
            return selectedItem != null && string.Equals(
                Convert.ToString(selectedItem.Tag),
                "DigitalOutput",
                StringComparison.Ordinal)
                ? ManualAxisIoPointType.DigitalOutput
                : ManualAxisIoPointType.DigitalInput;
        }

        /// <summary>复制已有配置，使取消或保存失败时不会提前修改原关联。</summary>
        private static ManualAxisIoAssociation CloneAssociation(ManualAxisIoAssociation association)
        {
            return new ManualAxisIoAssociation
            {
                Id = association.Id,
                PointType = association.PointType,
                PointName = association.PointName,
                DisplayName = association.DisplayName,
                OnActionName = association.OnActionName,
                OffActionName = association.OffActionName
            };
        }

        /// <summary>按当前界面语言读取指定文字资源。</summary>
        private string GetText(string resourceKey)
        {
            object resourceValue = TryFindResource(resourceKey);
            return resourceValue == null ? resourceKey : resourceValue.ToString();
        }
    }
}
