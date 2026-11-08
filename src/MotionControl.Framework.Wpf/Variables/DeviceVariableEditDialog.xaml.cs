using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Variables;

namespace MotionControl.Framework.Wpf.Variables
{
    /// <summary>编辑一条变量定义，并在保存前校验名称、类型和初始值。</summary>
    public partial class DeviceVariableEditDialog : Window
    {
        /// <summary>保存通过输入校验后的变量定义，供调用页面写入配置。</summary>
        public DeviceVariableDefinition VariableDefinition { get; private set; }

        /// <summary>创建空白变量定义窗口。</summary>
        public DeviceVariableEditDialog()
        {
            InitializeComponent();
            DataTypeComboBox.SelectedIndex = 0;
            AllowOperatorWriteCheckBox.IsChecked = false;
        }

        /// <summary>填充全部定义字段；既有 Name 为稳定引用键，只允许改独立显示名称。</summary>
        public DeviceVariableEditDialog(DeviceVariableDefinition definition)
            : this()
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            NameTextBox.Text = definition.Name;
            NameTextBox.IsReadOnly = true;
            InitialValueTextBox.Text = definition.InitialValue ?? string.Empty;
            DescriptionTextBox.Text = definition.Description;
            AllowOperatorWriteCheckBox.IsChecked = definition.AllowOperatorWrite;
            DisplayNameTextBox.Text = definition.DisplayName;
            EngineeringUnitTextBox.Text = definition.EngineeringUnit;
            DimensionTextBox.Text = definition.Dimension;
            MinimumValueTextBox.Text = definition.MinimumValue?.ToString(CultureInfo.InvariantCulture);
            MaximumValueTextBox.Text = definition.MaximumValue?.ToString(CultureInfo.InvariantCulture);
            DecimalPlacesTextBox.Text = definition.DecimalPlaces?.ToString(CultureInfo.InvariantCulture);
            SourceIdTextBox.Text = definition.SourceId;
            SourceDescriptionTextBox.Text = definition.SourceDescription;

            foreach (ComboBoxItem item in DataTypeComboBox.Items)
            {
                if (string.Equals(item.Tag as string, definition.DataType, StringComparison.OrdinalIgnoreCase))
                {
                    DataTypeComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        /// <summary>校验输入内容并把当前编辑的变量定义返回给调用页面。</summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ComboBoxItem selectedType = DataTypeComboBox.SelectedItem as ComboBoxItem;
            if (selectedType == null)
            {
                ErrorText.Text = FindResource("变量请选择类型").ToString();
                return;
            }

            var definition = new DeviceVariableDefinition
            {
                Name = NameTextBox.Text == null ? string.Empty : NameTextBox.Text.Trim(),
                DataType = selectedType.Tag as string,
                InitialValue = string.IsNullOrEmpty(InitialValueTextBox.Text)
                    ? null
                    : string.Equals(selectedType.Tag as string, "String", StringComparison.OrdinalIgnoreCase)
                        ? InitialValueTextBox.Text
                        : InitialValueTextBox.Text.Trim(),
                Description = DescriptionTextBox.Text == null ? string.Empty : DescriptionTextBox.Text.Trim(),
                AllowOperatorWrite = AllowOperatorWriteCheckBox.IsChecked == true
            };

            try
            {
                definition.DisplayName = DisplayNameTextBox.Text;
                definition.EngineeringUnit = EngineeringUnitTextBox.Text;
                definition.Dimension = DimensionTextBox.Text;
                definition.MinimumValue = ReadOptionalNumber(MinimumValueTextBox, "数值下限");
                definition.MaximumValue = ReadOptionalNumber(MaximumValueTextBox, "数值上限");
                definition.DecimalPlaces = ReadOptionalPrecision();
                definition.SourceId = SourceIdTextBox.Text;
                definition.SourceDescription = SourceDescriptionTextBox.Text;
                VariableDefinition = DeviceVariableStore.ValidateDefinitions(new[] { definition })[0];
                DialogResult = true;
            }
            catch (Exception error)
            {
                ErrorText.Text = error.Message;
            }
        }

        /// <summary>空文本保留未定义，非空值必须是有限数，不把输入错误改成零。</summary>
        private static double? ReadOptionalNumber(TextBox input, string field)
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return null;
            double value;
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException(field + "必须是有限数，或留空表示未定义。");
            return value;
        }

        /// <summary>读取可选显示精度；合法范围由统一定义检查器验证。</summary>
        private int? ReadOptionalPrecision()
        {
            if (string.IsNullOrWhiteSpace(DecimalPlacesTextBox.Text)) return null;
            int value;
            if (!int.TryParse(DecimalPlacesTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new InvalidOperationException("显示小数位必须是 0 到 15 的整数，或留空表示未定义。");
            return value;
        }
    }
}
