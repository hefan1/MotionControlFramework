using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Host.Wpf;
using MotionControl.Host.Wpf.Recovery;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证退出时不得用旧内存状态覆盖导入工程或隔离交接前的恢复证据。</summary>
    internal static class ShutdownEvidenceChecks
    {
        /// <summary>检查工程应用、待确认资源交接以及普通退出三种分支。</summary>
        internal static void Run()
        {
            CheckCase(true, false, false);
            CheckCase(false, true, false);
            CheckCase(false, false, true);
        }

        /// <summary>无设备连接时直接调用正式退出处理，检查原恢复文件是否被改写。</summary>
        private static void CheckCase(bool projectChanged, bool retainedBlocked, bool shouldSave)
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcf-shutdown-evidence-" + Guid.NewGuid().ToString("N")));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                string folder = Path.Combine(configuration.GetMachineDirectory("Machine01"), "Runtime");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "RecoverySnapshot.json");
                const string oldEvidence = "旧会话恢复证据，不能由过期内存覆盖";
                File.WriteAllText(file, oldEvidence);
                var coordinator = new MachineRecoveryCoordinator(new MachineRecoveryStore(configuration, "Machine01"),
                    new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                var app = (App)FormatterServices.GetUninitializedObject(typeof(App));
                SetField(app, "_machineRecoveryCoordinator", coordinator);
                SetField(app, "_taskEditsCloseConfirmed", true);
                SetField(app, "_equipmentProjectRestartRequired", projectChanged);
                SetField(app, "_shutdownRetainedBlockedEvidence", retainedBlocked);
                MethodInfo close = typeof(App).GetMethod("MainWindow_Closing", BindingFlags.Instance | BindingFlags.NonPublic);
                if (close == null) throw new InvalidOperationException("找不到正式 Host 退出处理。");
                var args = new CancelEventArgs();
                close.Invoke(app, new object[] { null, args });
                if (args.Cancel || (File.ReadAllText(file) != oldEvidence) != shouldSave)
                    throw new InvalidOperationException("退出时恢复证据保存分支错误。");
            }
            finally
            {
                if (root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        /// <summary>设置正式 Host 私有状态，仅供无界面的退出分支检查。</summary>
        private static void SetField(App app, string name, object value)
        {
            FieldInfo field = typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("找不到退出字段：" + name);
            field.SetValue(app, value);
        }
    }
}
