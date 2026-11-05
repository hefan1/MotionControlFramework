using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Host.Wpf.Configuration;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证目录与搜索空态分离，以及类型单元格使用语言资源。</summary>
    internal static class V14AlarmCatalogDisplayChecks
    {
        /// <summary>使用隔离空目录，并实例化真实类型模板切换类别及语言。</summary>
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "AlarmDisplay-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new MachineAlarmCatalogStore(new MachineConfigurationStore(root));
                store.Save("Machine", new MachineAlarmCatalogDocument());
                var view = new AlarmCatalogConfigurationView(store, "Machine");
                view.Resources["报警库空目录"] = "EMPTY CATALOG";
                view.Resources["报警库搜索无匹配"] = "NO MATCH";
                view.RefreshLocalizedText();
                Require(((TextBlock)view.FindName("CatalogEmptyText")).Text == "EMPTY CATALOG", "空目录未独立显示");
                Require(((TextBlock)view.FindName("SearchEmptyText")).Text.Length == 0, "空目录误报筛选无匹配");
                view.Alarms.Add(new MachineAlarmCatalogEntry { Code = "ONE", Message = "真实报警" });
                ((TextBox)view.FindName("SearchBox")).Text = "MISSING";
                Require(((TextBlock)view.FindName("CatalogEmptyText")).Text.Length == 0 && ((TextBlock)view.FindName("SearchEmptyText")).Text == "NO MATCH", "真实有目录的零匹配未正确区分");
                var grid = (DataGrid)view.FindName("AlarmGrid");
                var column = grid.Columns[2] as DataGridTemplateColumn;
                Require(column != null, "类型列仍使用固定中文模型字段");
                var cell = (TextBlock)column.CellTemplate.LoadContent();
                cell.Resources["停机报警"] = "Stop alarm";
                cell.Resources["非停机报警"] = "Non-stop alarm";
                cell.DataContext = new MachineAlarmCatalogEntry { Kind = MachineAlarmKind.NonStop };
                cell.Measure(new Size(200, 100));
                // 模板独立实例的触发器绑定也需等待Dispatcher执行，不能用Measure代替绑定传播。
                var frame = new System.Windows.Threading.DispatcherFrame();
                cell.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Require(cell.Text == "Non-stop alarm", "非停机类别未使用英文资源，实际=" + cell.Text);
                cell.Resources["非停机报警"] = "非停机报警";
                Require(cell.Text == "非停机报警", "类型列语言资源未动态刷新");
                store.Save("Broken", new MachineAlarmCatalogDocument());
                string brokenPath = Path.Combine(root, "Broken", "Parameters", "AlarmCatalog.json");
                File.WriteAllText(brokenPath, "{invalid");
                var broken = new AlarmCatalogConfigurationView(store, "Broken");
                broken.Resources["报警库目录未知"] = "UNKNOWN CATALOG";
                broken.Resources["报警库空目录"] = "EMPTY CATALOG";
                broken.RefreshLocalizedText();
                Require(((TextBlock)broken.FindName("CatalogEmptyText")).Text == "UNKNOWN CATALOG", "读取失败误报空目录");
                typeof(AlarmCatalogConfigurationView).GetMethod("SaveAlarms_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(broken, new object[] { broken, new RoutedEventArgs() });
                Require(((TextBlock)broken.FindName("CatalogEmptyText")).Text == "EMPTY CATALOG", "成功保存后仍误报目录未知");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        /// <summary>报告真实展示边界失败。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
