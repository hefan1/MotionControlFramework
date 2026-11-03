using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Host.Wpf;
using MotionControl.Host.Wpf.Configuration;
using MotionControl.TaskEngine.Configuration;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>设备工程页的只读初始状态及导入后重启界面检查。</summary>
    internal static class EquipmentProjectPageChecks
    {
        /// <summary>检查中文、英文、页面控件和重启门禁，不创建或连接控制器。</summary>
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "MotionControlProjectUiCheck-" + Guid.NewGuid().ToString("N"));
            var service = new MachineProjectPackageService(new MachineConfigurationStore(root), "Machine01", new[] { "Demo" }, new[] { "Virtual" });
            int gateCalls = 0, restartCalls = 0;
            var page = new EquipmentProjectView(service, applying => null,
                () => { gateCalls++; return Task.FromResult<IDisposable>(null); }, () => restartCalls++);
            Assert(((Button)page.FindName("ExportButton")).Content.ToString() == "导出当前工程", "导出入口未显示。");
            Assert(((Button)page.FindName("ImportButton")).Content.ToString() == "选择工程并预览", "导入预览入口未显示。");
            Assert(((Button)page.FindName("BackupsButton")).Content.ToString() == "查看备份", "备份入口未显示。");
            Assert(!((Button)page.FindName("ApplyButton")).IsEnabled, "没有预览时不应允许应用。");
            Assert(((TextBlock)page.FindName("FooterText")).Text.Contains("恢复信息")
                && ((TextBlock)page.FindName("FooterText")).Text.Contains("重新打开"), "未说明证据保留和重启要求。");
            var preview = (MachineProjectPreview)Activator.CreateInstance(typeof(MachineProjectPreview), true);
            SetPreview(preview, "EquipmentName", "测试设备");
            SetPreview(preview, "ExecutionOwner", "TaskEngine");
            SetPreview(preview, "ControllerBackend", "Virtual");
            SetPreview(preview, "SourceMachineId", "Source01");
            SetPreview(preview, "Files", new List<string> { "HardwareConfig/AxisParameters.json" }.AsReadOnly());
            SetPreview(preview, "Changes", new List<string> { "修改：轴参数" }.AsReadOnly());
            SetPreview(preview, "Warnings", new List<string> { "核对安全参数" }.AsReadOnly());
            SetPreview(preview, "Contents", new Dictionary<string, string> { ["HardwareConfig/AxisParameters.json"] = "新轴配置" });
            SetPreview(preview, "CurrentContents", new Dictionary<string, string> { ["HardwareConfig/AxisParameters.json"] = "旧轴配置" });
            typeof(EquipmentProjectView).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(page, preview);
            typeof(EquipmentProjectView).GetMethod("RenderPreview", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, null);
            Assert(((TextBox)page.FindName("CurrentFileText")).Text == "旧轴配置"
                && ((TextBox)page.FindName("IncomingFileText")).Text == "新轴配置", "未展示当前及导入的具体配置内容。");
            string detail = "未登记参数：\n" + new string('参', 300) + "参数末尾.json";
            typeof(EquipmentProjectView).GetMethod("ShowStatus", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, new object[] { detail, true });
            Assert(((Expander)page.FindName("StatusDetails")).Visibility == Visibility.Visible
                && ((TextBox)page.FindName("StatusDetailsText")).Text.Contains("参数末尾.json"), "长错误没有显示完整文件列表。");
            var language = typeof(EquipmentProjectView).GetMethod("SetLanguage", BindingFlags.Instance | BindingFlags.NonPublic);
            language.Invoke(page, new object[] { "en-US" });
            Assert(((Button)page.FindName("ImportButton")).Content.ToString() == "Select and review project", "英文入口未更新。");
            page.Measure(new Size(850, 600)); page.Arrange(new Rect(0, 0, 850, 600)); page.UpdateLayout();
            Assert(((Button)page.FindName("ApplyButton")).ActualWidth > 0, "工程页面窄窗口布局不可见。");
            Assert(gateCalls == 0 && restartCalls == 0 && !Directory.Exists(root), "只读显示页面意外修改配置或取得控制权。");
            ValidateCardCompatibility(root);

            var window = new MainWindow();
            var method = typeof(MainWindow).GetMethod("RequireRestartForEquipmentProject", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(window, null);
            Assert(!((StackPanel)window.FindName("PrimaryNavigation")).IsEnabled
                && !((StackPanel)window.FindName("SidebarNavigation")).IsEnabled
                && !((Button)window.FindName("MachineStartButton")).IsEnabled,
                "工程更新后仍允许通过旧内存配置操作设备。");
            window.Close();
        }

        /// <summary>条件不成立时抛出明确原因。</summary>
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        /// <summary>设置服务内部预览测试快照，不访问真实配置。</summary>
        private static void SetPreview(MachineProjectPreview preview, string name, object value)
        { typeof(MachineProjectPreview).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(preview, value); }

        /// <summary>使用 Host 的真实后端选项检查卡型、单卡约束与两种轴数规则；不连接控制卡。</summary>
        private static void ValidateCardCompatibility(string root)
        {
            var selection = (MachineProjectCardSelection)Activator.CreateInstance(typeof(MachineProjectCardSelection), true);
            SetCard(selection, "Backend", "Virtual"); SetCard(selection, "Vendor", "正运动");
            SetCard(selection, "CardType", "总线型"); SetCard(selection, "AdapterType", "ZMotion");
            SetCard(selection, "AxisCount", 13); SetCard(selection, "CardCount", 1);
            Type validator = typeof(MainWindow).Assembly.GetType("MotionControl.Host.Wpf.HostEquipmentProjectCardValidation", true);
            MethodInfo validate = validator.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic);
            var store = new MachineConfigurationStore(root);
            Func<string> check = () => (string)validate.Invoke(null, new object[] { selection, store, "Machine01" });
            Assert(check() == null, "软件控制器支持的 13 轴及正运动映射被拒绝。");
            SetCard(selection, "AdapterType", "Virtual"); Assert(check() != null, "软件后端被误认为硬件适配器。");
            SetCard(selection, "AdapterType", "ZMotion"); SetCard(selection, "AxisCount", 65);
            Assert(check() != null, "软件控制器不支持的轴数被放行。");
            SetCard(selection, "Backend", "ZMotion"); SetCard(selection, "AxisCount", 13);
            Assert(check() != null, "不在正运动卡型选项中的轴数被放行。");
            SetCard(selection, "AxisCount", 64); Assert(check() == null, "支持的正运动卡型被拒绝。");
            SetCard(selection, "CardCount", 2); Assert(check() != null, "多卡被单卡 Host 放行。");
            Assert(!Directory.Exists(root), "卡型兼容检查意外读取或创建设备配置目录。");
        }

        /// <summary>设置只读卡型选择测试快照。</summary>
        private static void SetCard(MachineProjectCardSelection selection, string name, object value)
        { typeof(MachineProjectCardSelection).GetProperty(name).SetValue(selection, value); }
    }
}
