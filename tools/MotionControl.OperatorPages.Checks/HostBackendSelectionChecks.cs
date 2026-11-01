using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using MotionControl.Adapter.MotionCard.Virtual;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Host.Wpf;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证控制器后端选择必须明确，不能悄悄切换设备，以及设备映射只读保护。</summary>
    internal static class HostBackendSelectionChecks
    {
        /// <summary>在隔离目录检查缺省、明确选择、错误配置和轴映射只读控件。</summary>
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "motion-backend-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new MachineConfigurationStore(root);
                Type selector = typeof(MainWindow).Assembly.GetType("MotionControl.Host.Wpf.HostMotionCardBackendSelection", true);
                MethodInfo create = selector.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
                if (create == null) throw new InvalidOperationException("找不到后端选择入口。");
                Func<IMotionCardHostConnection> select = () => (IMotionCardHostConnection)create.Invoke(null, new object[] { store, "Machine01" });
                if (!(select() is ZMotionAxisMappingCardConnection)) throw new InvalidOperationException("缺省后端没有保持正运动。");
                string folder = Path.Combine(root, "Machine01", "Parameters");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "ControllerBackend.json");
                File.WriteAllText(path, "{\"backend\":\"Virtual\",\"port\":18520}");
                IMotionCardHostConnection virtualConnection = select();
                if (!(virtualConnection is VirtualAxisMappingCardConnection)) throw new InvalidOperationException("明确选择后未使用独立控制器。");
                var page = new AxisMappingConfigurationView(store, "Machine01", virtualConnection);
                page.SetHardwareConfigurationReadOnly(true);
                if (((Button)page.FindName("SaveConfigurationButton")).IsEnabled
                    || !((DataGrid)page.FindName("AxisMappingGrid")).IsReadOnly)
                    throw new InvalidOperationException("独立控制器未保护原设备轴映射。");
                page.SetHardwareConfigurationReadOnly(false);
                if (!((Button)page.FindName("SaveConfigurationButton")).IsEnabled)
                    throw new InvalidOperationException("退出只读后不能恢复正常配置编辑。");
                page.ReportCardConnectionLost("连接中断，状态未确认。");
                if (((Button)page.FindName("ConnectCardButton")).IsEnabled)
                    throw new InvalidOperationException("会话异常中断后不应允许旧运行时直接重连。");

                var oldCard = new VirtualMotionCard("MotionCard01", new AxisHardwareDefinition[0],
                    new DigitalIoHardwareDefinition[0], new DigitalIoHardwareDefinition[0]);
                typeof(VirtualAxisMappingCardConnection).GetField("_card", BindingFlags.NonPublic | BindingFlags.Instance)
                    .SetValue(virtualConnection, oldCard);
                try
                {
                    virtualConnection.ConnectAsync(new MotionCardSettingsDocument(), CancellationToken.None).GetAwaiter().GetResult();
                    throw new InvalidOperationException("旧会话状态未知时错误允许重新连接。");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("原会话已中断")) { }

                var window = new MainWindow();
                try
                {
                    var footer = (TextBlock)window.FindName("MotionCardConnectionStatus");
                    window.SetMotionCardConnectionStatus(true, null, true);
                    if (!footer.Text.Contains("软件控制器") || !footer.Text.Contains("已连接"))
                        throw new InvalidOperationException("连接后没有显示软件控制器身份。");
                    window.SetMotionCardConnectionStatus(false, "通信中断", true);
                    if (!footer.Text.Contains("软件控制器") || !footer.Text.Contains("未连接"))
                        throw new InvalidOperationException("会话中断后没有显示软件控制器未连接。");
                    window.SetMotionCardConnectionStatus(true);
                    if (footer.Text.Contains("软件控制器"))
                        throw new InvalidOperationException("正运动连接错误沿用软件控制器标识。");
                }
                finally { window.Close(); }

                File.WriteAllText(path, "{\"backend\":\"ZMotion\"}");
                if (!(select() is ZMotionAxisMappingCardConnection)) throw new InvalidOperationException("正运动配置未选择厂商适配器。");
                foreach (string json in new[] { "{}", "{\"backend\":\"Unknown\"}", "{\"backend\":\"Virtual\",\"port\":0}" })
                {
                    File.WriteAllText(path, json);
                    try { select(); throw new InvalidOperationException("非法后端配置被接受：" + json); }
                    catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
