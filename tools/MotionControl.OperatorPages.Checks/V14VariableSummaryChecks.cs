using System;
using System.IO;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Framework.Wpf.Variables;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>只读变量定义摘要的独立检查，不保存配置或下发设备动作。</summary>
    internal static class V14VariableSummaryChecks
    {
        /// <summary>验证初值与内存值分离、选中与定义更新及语言刷新。</summary>
        internal static void Run()
        {
            var definition = new DeviceVariableDefinition { Name = "Flag", DataType = "Boolean", InitialValue = "false", Description = "初始说明" };
            var store = new DeviceVariableStore(new[] { definition, new DeviceVariableDefinition { Name = "Other", DataType = "Integer" } });
            var view = new DeviceVariableView(store, new MachineConfigurationStore(Path.Combine(Path.GetTempPath(), "VariableSummary-" + Guid.NewGuid().ToString("N"))), "Machine01");
            view.FocusVariableByName("Flag");
            store.SetBoolean("Flag", true);
            var text = (TextBlock)view.FindName("SelectedDefinitionSummaryText");
            if (text.Text.IndexOf("false", StringComparison.OrdinalIgnoreCase) < 0 || text.Text.IndexOf("true", StringComparison.OrdinalIgnoreCase) < 0 || !text.Text.Contains("初始说明"))
                throw new InvalidOperationException("摘要未区分真实初值和内存值");
            definition.Description = "新说明";
            store.ReplaceDefinitions(new[] { definition, new DeviceVariableDefinition { Name = "Other", DataType = "Integer" } });
            if (!text.Text.Contains("新说明")) throw new InvalidOperationException("定义更新未刷新摘要");
            view.FocusVariableByName("Other");
            if (!text.Text.Contains("未配置 / 未提供") || text.Text.Contains("新说明")) throw new InvalidOperationException("选择切换沿用旧定义或隐藏缺项");
            view.Resources["变量定义运行摘要格式"] = "Definition: {0} / {1}; initial: {2}; description: {3}; current memory: {4}";
            view.Resources["变量摘要未提供"] = "Not configured / not provided";
            view.RefreshLocalizedText();
            if (!text.Text.StartsWith("Definition:") || !text.Text.Contains("Not configured")) throw new InvalidOperationException("语言刷新未更新摘要");
        }
    }
}
