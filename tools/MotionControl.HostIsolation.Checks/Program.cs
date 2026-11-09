using System;
using System.Diagnostics;
using System.IO;
using MotionControl.Host.Wpf;
using MotionControl.Equipment.Demo;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.HostIsolation.Checks
{
    /// <summary>检查测试配置目录的启动边界，不启动上位机或连接控制器。</summary>
    internal static class Program
    {
        /// <summary>验证默认配置、隔离路径以及错误参数不会误用现场目录。</summary>
        private static int Main()
        {
            string drive = Path.GetPathRoot(Environment.CurrentDirectory);
            string live = Path.Combine(drive, "HostIsolationChecks", "Production", "Configurations");
            string isolated = Path.Combine(drive, "HostIsolationChecks", "Isolated");
            try
            {
                Check(SimulationConfigurationRoot.Parse(new string[0], live) == null, "未指定时使用现场配置");
                Check(SimulationConfigurationRoot.Parse(new[] { "--simulation-config-root", isolated }, live) == isolated, "接受独立绝对目录");
                Reject(new[] { "--simulation-config-root" }, live, "拒绝缺少目录");
                Reject(new[] { "--simulation-config-root", isolated, "--simulation-config-root", isolated }, live, "拒绝重复参数");
                Reject(new[] { "--simulation-config-root", "relative" }, live, "拒绝相对目录");
                Reject(new[] { "--simulation-config-root", drive }, live, "拒绝磁盘根目录");
                Reject(new[] { "--simulation-config-root", live }, live, "拒绝现场目录");
                Reject(new[] { "--simulation-config-root", Path.Combine(live, "Machine01") }, live, "拒绝现场子目录");
                Reject(new[] { "--simulation-config-root", Path.GetDirectoryName(live) }, live, "拒绝现场父目录");
                Reject(new[] { "--simulation-config-root", isolated, "--task-simulation" }, live, "拒绝与预览入口混用");
                CheckSavedDefaultProject(live);
                CheckEquipmentSelection();
                Console.WriteLine("隔离配置目录检查全部通过。");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        /// <summary>保存的默认项目只指向现存软件控制器配置，显式参数和独立预览保持优先。</summary>
        private static void CheckSavedDefaultProject(string live)
        {
            string root = Path.Combine(Path.GetTempPath(), "MotionControlDefaultProject-" + Guid.NewGuid().ToString("N"));
            string project = Path.Combine(root, "Project");
            string preference = Path.Combine(root, "default-project.txt");
            string backend = Path.Combine(project, "Machine01", "Parameters", "ControllerBackend.json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backend));
                File.WriteAllText(backend, "{\"backend\":\"Virtual\",\"port\":19580}");
                bool saved;
                Check(SimulationConfigurationRoot.Resolve(new string[0], live, preference, out saved) == null && !saved,
                    "没有默认项目时保留原配置入口");
                File.WriteAllText(preference, project);
                bool identityRejected = false;
                try { SimulationConfigurationRoot.Resolve(new string[0], live, preference, out saved); }
                catch (ArgumentException) { identityRejected = true; }
                Check(identityRejected, "默认项目缺少会话编号时拒绝启动");
                File.WriteAllText(Path.Combine(project, "ProjectSession.id"), Guid.NewGuid().ToString("N"));
                Check(SimulationConfigurationRoot.Resolve(new string[0], live, preference, out saved) == project && saved,
                    "无参数打开已保存的仿真项目");
                string otherProject = Path.Combine(root, "OtherProject");
                Directory.CreateDirectory(otherProject);
                File.Copy(Path.Combine(project, "ProjectSession.id"), Path.Combine(otherProject, "ProjectSession.id"));
                Check(SimulationConfigurationRoot.ReadProjectSession(project) != SimulationConfigurationRoot.ReadProjectSession(otherProject),
                    "不同项目目录不共用软件控制器会话");
                Check(SimulationConfigurationRoot.Resolve(new[] { "--use-production-config" }, live, preference, out saved) == null && !saved,
                    "明确选择原设备配置时不加载仿真项目");
                Check(SimulationConfigurationRoot.Resolve(new[] { "--task-simulation" }, live, preference, out saved) == null && !saved,
                    "独立预览不受默认项目影响");
                string explicitRoot = Path.Combine(root, "Explicit");
                Check(SimulationConfigurationRoot.Resolve(new[] { "--simulation-config-root", explicitRoot }, live, preference, out saved) == explicitRoot && !saved,
                    "显式隔离目录优先于默认项目");
                string outside = Path.Combine(root, "OutsideRuntime");
                string junction = Path.Combine(project, "Machine01", "Runtime");
                Directory.CreateDirectory(outside);
                using (var link = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    string output = link.StandardOutput.ReadToEnd() + link.StandardError.ReadToEnd();
                    link.WaitForExit();
                    if (link.ExitCode != 0) throw new InvalidOperationException("无法创建测试目录联接：" + output);
                }
                try
                {
                    bool linkRejected = false;
                    try { SimulationConfigurationRoot.Resolve(new string[0], live, preference, out saved); }
                    catch (ArgumentException) { linkRejected = true; }
                    Check(linkRejected, "默认项目内部不能含目录联接");
                }
                finally { Directory.Delete(junction); }
                File.WriteAllText(backend, "{\"backend\":\"ZMotion\",\"port\":19580}");
                bool rejected = false;
                try { SimulationConfigurationRoot.Resolve(new string[0], live, preference, out saved); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "默认仿真项目不能连接非软件控制器");
                File.WriteAllText(preference, live);
                rejected = false;
                try { SimulationConfigurationRoot.Resolve(new string[0], live, preference, out saved); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "默认项目不能指向原设备目录");
            }
            finally
            {
                string absolute = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (Directory.Exists(absolute) && string.Equals(Path.GetDirectoryName(absolute), temp, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(absolute).StartsWith("MotionControlDefaultProject-", StringComparison.Ordinal))
                    Directory.Delete(absolute, true);
            }
        }

        /// <summary>用独立临时设备配置检查设备定义和两种自动执行入口，不连接控制器。</summary>
        private static void CheckEquipmentSelection()
        {
            string root = Path.Combine(Path.GetTempPath(), "MotionControlHostSelection-" + Guid.NewGuid().ToString("N"));
            try
            {
                CheckEquipmentSelectionInDirectory(root);
            }
            finally
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporaryDirectory = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (Directory.Exists(fullRoot)
                    && string.Equals(Path.GetDirectoryName(fullRoot), temporaryDirectory, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(fullRoot).StartsWith("MotionControlHostSelection-", StringComparison.Ordinal))
                    Directory.Delete(fullRoot, true);
            }
        }

        /// <summary>在专属临时目录中检查各入口和无效设备名称。</summary>
        private static void CheckEquipmentSelectionInDirectory(string root)
        {
            var store = new MachineConfigurationStore(root);
            const string machineId = "HostSelectionCheck";
            const string path = "Parameters/Equipment.json";
            Check(HostEquipmentSelection.ResolveEquipmentDefinition(store, machineId) is DemoEquipmentDefinition, "默认选择示例设备");
            Check(HostEquipmentSelection.ResolveExecutionOwner(store, machineId) == null, "默认入口按工位自动判断");
            var settings = new EquipmentSelectionSettings { ExecutionOwner = " StationFlows " };
            store.SaveJson(machineId, path, settings);
            Check(HostEquipmentSelection.ResolveExecutionOwner(store, machineId) == MachineControlOwner.StationFlows, "选择 C# 工位入口");
            settings.ExecutionOwner = "taskengine";
            store.SaveJson(machineId, path, settings);
            Check(HostEquipmentSelection.ResolveExecutionOwner(store, machineId) == MachineControlOwner.TaskEngine, "选择任务列表入口");
            settings.ExecutionOwner = "None";
            store.SaveJson(machineId, path, settings);
            Check(HostEquipmentSelection.ResolveExecutionOwner(store, machineId) == MachineControlOwner.None, "选择手动入口");
            settings.ExecutionOwner = "Unknown";
            store.SaveJson(machineId, path, settings);
            bool rejectedEntry = false;
            try { HostEquipmentSelection.ResolveExecutionOwner(store, machineId); }
            catch (InvalidOperationException) { rejectedEntry = true; }
            Check(rejectedEntry, "拒绝未知执行入口");
            settings.Definition = "UnknownDevice";
            store.SaveJson(machineId, path, settings);
            bool rejectedDevice = false;
            try { HostEquipmentSelection.ResolveEquipmentDefinition(store, machineId); }
            catch (InvalidOperationException) { rejectedDevice = true; }
            Check(rejectedDevice, "拒绝未登记设备定义");
        }

        /// <summary>断言不满足时报告具体检查名称。</summary>
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("检查失败：" + name);
            Console.WriteLine("通过：" + name);
        }

        /// <summary>确认无效目录被拒绝，而不是静默回退到现场目录。</summary>
        private static void Reject(string[] args, string productionRoot, string name)
        {
            try
            {
                SimulationConfigurationRoot.Parse(args, productionRoot);
            }
            catch (ArgumentException)
            {
                Console.WriteLine("通过：" + name);
                return;
            }
            throw new InvalidOperationException("检查失败：" + name);
        }
    }
}
