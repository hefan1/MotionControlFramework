using System;
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

        /// <summary>使用已有定义填充编辑窗口。</summary>
        public DeviceVariableEditDialog(DeviceVariableDefinition definition)
            : this()
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            NameTextBox.Text = definition.Name;
            InitialValueTextBox.Text = definition.InitialValue ?? string.Empty;
            DescriptionTextBox.Text = definition.Description;
            AllowOperatorWriteCheckBox.IsChecked = definition.AllowOperatorWrite;

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
                DeviceVariableStore.ValidateDefinitions(new[] { definition });
                VariableDefinition = definition;
                DialogResult = true;
            }
            catch (Exception error)
            {
                ErrorText.Text = error.Message;
            }
        }
    }
}
