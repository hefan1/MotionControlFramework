using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>安全绑定副本与行说明独立检查，不操作硬件。</summary>
    internal static class V14SafetyUnsavedChecks
    {
        /// <summary>覆盖修改/恢复、冲突、刷新不重置基准及成功保存与失败保留。</summary>
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "SafetyUnsaved-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new MachineConfigurationStore(root);
                string hardware = Path.Combine(store.GetMachineDirectory("Machine01"), "HardwareConfig");
                Directory.CreateDirectory(hardware);
                File.WriteAllText(Path.Combine(hardware, "IoMapping.json"), "{\"schemaVersion\":1,\"digitalInputs\":[{\"name\":\"Start\",\"controllerId\":\"Card\",\"channel\":1}],\"digitalOutputs\":[]}");
                var view = new SafetyInterlockConfigurationView(store, "Machine01");
                if (view.HasUnsavedChanges) throw new InvalidOperationException("载入后误报修改");
                view.StartInputName = "Start"; view.PauseInputName = "start";
                if (!view.HasUnsavedChanges) throw new InvalidOperationException("与载入副本不同的修改未标出：" + view.UnsavedChangesSummary);
                string conflictText = (string)view.FindResource("安全联锁本行冲突");
                if (!view.StartMappingSummary.StartsWith(conflictText, StringComparison.Ordinal)
                    || !view.PauseMappingSummary.StartsWith(conflictText, StringComparison.Ordinal))
                    throw new InvalidOperationException("大小写无关冲突未标出：" + view.StartMappingSummary + " / " + view.PauseMappingSummary);
                if (!view.StartMappingSummary.Contains("Card DI1")) throw new InvalidOperationException("隔离IoMapping未读出真实地址：" + view.StartMappingSummary);
                var validate = typeof(SafetyInterlockConfigurationView).GetMethod("TryValidateConfiguration", BindingFlags.Instance | BindingFlags.NonPublic);
                var validationArgs = new object[] { null };
                if ((bool)validate.Invoke(view, validationArgs)) throw new InvalidOperationException("显示冲突但原保存校验错误放行");
                view.RefreshDigitalInputNames();
                if (!view.HasUnsavedChanges) throw new InvalidOperationException("刷新列表清除了未保存基准");
                view.PauseInputName = string.Empty;
                view.SaveConfigurationAsync = configuration => Task.FromResult<string>(null);
                typeof(SafetyInterlockConfigurationView).GetMethod("SaveConfigurationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { view, new RoutedEventArgs() });
                if (view.HasUnsavedChanges) throw new InvalidOperationException("成功保存没有接受实际副本");
                view.ResetInputName = "Start";
                view.StartInputName = string.Empty;
                view.SaveConfigurationAsync = configuration => { throw new IOException("save failed"); };
                typeof(SafetyInterlockConfigurationView).GetMethod("SaveConfigurationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { view, new RoutedEventArgs() });
                if (!view.HasUnsavedChanges || view.ResetInputName != "Start") throw new InvalidOperationException("保存失败清除了修改或输入");
                view.Resources["安全联锁有未保存修改"] = "Unsaved binding changes";
                view.RefreshLocalizedText();
                if (view.UnsavedChangesSummary != "Unsaved binding changes") throw new InvalidOperationException("语言刷新未更新修改说明");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
