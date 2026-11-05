using System;
using System.IO;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>安全联锁说明的独立检查，不访问硬件，不替换其他测试。</summary>
    internal static class V14SafetyInterlockChecks
    {
        /// <summary>验证空绑定、失效引用与刷新保留编辑文本。</summary>
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "SafetyUi-" + Guid.NewGuid().ToString("N"));
            var view = new SafetyInterlockConfigurationView(new MachineConfigurationStore(root), "Machine01");
            if (!view.PauseMappingSummary.Contains("未配置")) throw new InvalidOperationException("空绑定未说明");
            view.PauseInputName = "尚未映射的现场输入";
            if (!view.PauseMappingSummary.Contains("引用未映射")) throw new InvalidOperationException("失效绑定未说明");
            view.RefreshDigitalInputNames();
            if (view.PauseInputName != "尚未映射的现场输入") throw new InvalidOperationException("刷新替换了失效绑定");
            if (!view.StartMappingSummary.Contains("未配置")) throw new InvalidOperationException("刷新默认绑定了启动输入");
        }
    }
}
