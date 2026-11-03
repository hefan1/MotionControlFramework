using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.MachineProject.Checks
{
    /// <summary>使用独立临时目录检查设备工程包，不访问控制器和用户配置。</summary>
    internal static class Program
    {
        /// <summary>执行导出、预检、应用、失败回滚和中断恢复检查。</summary>
        private static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--interrupt") return InterruptAfterFirstWrite(args[1], args[2]);
            string temporary = Path.Combine(Path.GetTempPath(), "MotionControlProjectChecks-" + Guid.NewGuid().ToString("N"));
            try
            {
                string root = Path.Combine(temporary, "config");
                var store = new MachineConfigurationStore(root);
                string sourceRoot = store.GetMachineDirectory("Source01"), targetRoot = store.GetMachineDirectory("Machine01");
                CreateFixture(sourceRoot, "Source01", "SourceAxis", "TaskEngine");
                CreateFixture(targetRoot, "Machine01", "OldAxis", "StationFlows");
                byte[] originalConnection = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("{\r\n  \"schemaVersion\":1,\"note\":\"未引用的原文件\"\r\n}")).ToArray();
                File.WriteAllBytes(Path.Combine(targetRoot, "HardwareConfig/Connection.json"), originalConnection);
                Write(sourceRoot, "Parameters/Unregistered.json", "{\"private\":\"未登记状态\"}");
                Write(targetRoot, "Runtime/RecoverySnapshot.json", "{\"preserve\":\"现场证据\"}");
                Write(targetRoot, "Drafts/TaskWorkspace.json", "{\"preserve\":\"任务草稿\"}");
                Write(targetRoot, "Parameters/KeepUser.json", "{\"schemaVersion\":1,\"user\":\"保留文件\"}");
                Write(targetRoot, "Parameters/DemoStations.json", "{\"schemaVersion\":1,\"user\":\"原工位配置\"}");
                Write(targetRoot, "Parameters/Old.before-test.json", "{\"user\":\"旧配置备份\"}");
                var source = NewService(store, "Source01");
                var target = NewService(store, "Machine01");
                string package = Path.Combine(temporary, "设备工程.mcpkg.json");
                ExpectFailure(() => source.Export(package), "未登记");
                File.Delete(Path.Combine(sourceRoot, "Parameters/Unregistered.json"));
                source.Export(package);
                string packageText = File.ReadAllText(package);
                Require(!packageText.Contains("现场证据") && !packageText.Contains("任务草稿") && !packageText.Contains("未登记状态"), "工程包含不应导出的现场数据。 ");
                Write(sourceRoot, "Parameters/CustomSetting.json", "{\"schemaVersion\":1,\"value\":1}");
                var sourceWithCustom = new MachineProjectPackageService(store, "Source01", new[] { "Demo" }, new[] { "ZMotion", "Virtual" }, new[] { "Parameters/CustomSetting.json" }, CheckCard);
                string customPackage = Path.Combine(temporary, "设备扩展参数.mcpkg.json"); sourceWithCustom.Export(customPackage);
                ExpectFailure(() => target.Preview(customPackage), "未登记");
                var targetWithCustom = new MachineProjectPackageService(store, "Machine01", new[] { "Demo" }, new[] { "ZMotion", "Virtual" }, new[] { "Parameters/CustomSetting.json" }, CheckCard);
                Require(targetWithCustom.Preview(customPackage).Files.Contains("Parameters/CustomSetting.json"), "已登记设备配置未导出。 ");
                File.Delete(Path.Combine(sourceRoot, "Parameters/CustomSetting.json"));
                MachineProjectPreview preview = target.Preview(package);
                Require(preview.SourceMachineId == "Source01" && preview.ExecutionOwner == "TaskEngine" && preview.Changes.Count > 0,
                    "工程预览缺少设备、方式或差异。 ");
                Require(preview.GetFileContent("HardwareConfig/AxisParameters.json").Contains("SourceAxis")
                    && preview.GetCurrentFileContent("HardwareConfig/AxisParameters.json").Contains("OldAxis")
                    && preview.GetCurrentFileContent("Runtime/RecoverySnapshot.json") == null, "工程对比内容不正确或泄露运行记录。 ");
                ExpectFailure(() => preview.GetFileContent("Runtime/RecoverySnapshot.json"), "不包含");
                Require(preview.Changes.Any(item => item.Contains("保留") && item.Contains("KeepUser.json")), "未告知保留包外文件。 ");
                Require(preview.Changes.Any(item => item.Contains("保留") && item.Contains("OldConnection.json")), "未告知保留旧连接配置。 ");
                Require(preview.Changes.Any(item => item.Contains("修改") && item.Contains("HardwareConfig/Connection.json")), "未报告未引用文件的同名覆盖。 ");
                ExpectFailure(() => target.Apply(preview, () => false), "不能应用");
                Require(!target.HasPendingApply, "门禁拒绝时不应创建事务标记。 ");

                CheckConcurrentChangeBeforeWrite(target, targetRoot, package);
                CheckConcurrentChangeAfterWrite(target, targetRoot, package);
                preview = target.Preview(package);

                string oldAxis = File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/AxisParameters.json"));
                int calls = 0;
                ExpectFailure(() => target.Apply(preview, () => ++calls < 5), "已恢复原配置");
                Require(File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/AxisParameters.json")) == oldAxis && !target.HasPendingApply,
                    "写入中断后没有恢复原文件。 ");
                Require(File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/Connection.json")).Contains("未引用的原文件"), "回滚时丢失原有同名硬件文件。 ");
                Require(File.ReadAllBytes(Path.Combine(targetRoot, "HardwareConfig/Connection.json")).SequenceEqual(originalConnection), "回滚未保留原文件 BOM 和字节。 ");
                Require(File.ReadAllText(Path.Combine(targetRoot, "Runtime/RecoverySnapshot.json")).Contains("现场证据")
                    && File.ReadAllText(Path.Combine(targetRoot, "Drafts/TaskWorkspace.json")).Contains("任务草稿"), "运行证据或草稿被改写。 ");
                preview = target.Preview(package);
                string backup = target.Apply(preview, () => true);
                Require(Directory.Exists(backup) && target.ListBackups().Contains(backup) && !target.HasPendingApply, "应用或备份失败。 ");
                Require(File.ReadAllBytes(Path.Combine(backup, "HardwareConfig/Connection.json")).SequenceEqual(originalConnection), "备份未保留原始字节。 ");
                Require(File.ReadAllText(Path.Combine(backup, "HardwareConfig/OldConnection.json")).Contains("schemaVersion")
                    && File.ReadAllText(Path.Combine(backup, "Parameters/DemoStations.json")).Contains("原工位配置"), "完整备份缺少原卡连接或已登记工位配置。 ");
                Require(File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/AxisParameters.json")).Contains("SourceAxis"), "轴配置未应用。 ");
                Require((string)JObject.Parse(File.ReadAllText(Path.Combine(targetRoot, "Parameters/TaskMachine.json")))["machineId"] == "Machine01", "任务清单没有绑定目标设备。 ");
                Require(File.ReadAllText(Path.Combine(targetRoot, "Runtime/RecoverySnapshot.json")).Contains("现场证据")
                    && File.ReadAllText(Path.Combine(targetRoot, "Drafts/TaskWorkspace.json")).Contains("任务草稿")
                    && File.ReadAllText(Path.Combine(targetRoot, "Parameters/KeepUser.json")).Contains("保留文件")
                    && File.Exists(Path.Combine(targetRoot, "HardwareConfig/OldConnection.json"))
                    && File.ReadAllText(Path.Combine(targetRoot, "Parameters/Old.before-test.json")).Contains("旧配置备份"), "成功应用时覆盖了运行数据或包外文件。 ");

                preview = target.Preview(package);
                string selectionPath = Path.Combine(targetRoot, "Parameters/Equipment.json");
                string previousSelection = File.ReadAllText(selectionPath);
                File.WriteAllText(selectionPath, previousSelection + " ");
                ExpectFailure(() => target.Apply(preview, () => true), "发生变化");
                File.WriteAllText(selectionPath, previousSelection);
                JObject corrupt = JObject.Parse(packageText);
                corrupt["files"]["HardwareConfig/AxisParameters.json"] = "{}";
                string corruptPath = Path.Combine(temporary, "损坏.json"); File.WriteAllText(corruptPath, corrupt.ToString());
                ExpectFailure(() => target.Preview(corruptPath), "完整性");
                var unsupported = new MachineProjectPackageService(store, "Machine01", new[] { "Other" }, new[] { "ZMotion", "Virtual" }, null, CheckCard);
                ExpectFailure(() => unsupported.Preview(package), "未安装");
                var missingCardValidator = new MachineProjectPackageService(store, "Machine01", new[] { "Demo" }, new[] { "ZMotion", "Virtual" });
                ExpectFailure(() => missingCardValidator.Preview(package), "未提供控制卡");
                var blankCardError = new MachineProjectPackageService(store, "Machine01", new[] { "Demo" }, new[] { "ZMotion", "Virtual" }, null, value => " ");
                ExpectFailure(() => blankCardError.Preview(package), "不支持工程要求的运动卡");
                string invalidBackupRoot = store.GetMachineDirectory("BadBackup"); Directory.CreateDirectory(invalidBackupRoot);
                File.WriteAllText(Path.Combine(invalidBackupRoot, "Backups"), "not a directory");
                ExpectFailure(() => NewService(store, "BadBackup").ListBackups(), "不是目录");
                ExpectFailure(() => source.Export(Path.Combine(sourceRoot, "HardwareConfig/AxisParameters.json")), "配置目录内");
                JObject invalidTask = JObject.Parse(packageText);
                string taskPath = "Parameters/TaskMachine.json";
                JObject taskMachine = JObject.Parse((string)invalidTask["files"][taskPath]);
                taskMachine["stations"][0]["taskId"] = "MissingTask";
                SetPackageFile(invalidTask, taskPath, taskMachine.ToString());
                string invalidPath = Path.Combine(temporary, "任务引用错误.json"); File.WriteAllText(invalidPath, invalidTask.ToString());
                ExpectFailure(() => target.Preview(invalidPath), "任务引用检查失败");
                JObject invalidIo = JObject.Parse(packageText);
                string ioPath = "HardwareConfig/IoMapping.json";
                JObject io = JObject.Parse((string)invalidIo["files"][ioPath]);
                ((JArray)io["digitalInputs"]).Add(JObject.Parse("{\"controllerId\":\"Card\",\"name\":\"重复输入\",\"channel\":0,\"activeHigh\":true}"));
                SetPackageFile(invalidIo, ioPath, io.ToString());
                File.WriteAllText(invalidPath, invalidIo.ToString());
                ExpectFailure(() => target.Preview(invalidPath), "重复使用物理通道");
                JObject invalidAdapter = JObject.Parse(packageText);
                string cardPath = "HardwareConfig/MotionCards.json";
                JObject card = JObject.Parse((string)invalidAdapter["files"][cardPath]);
                card["motionCards"][0]["adapterType"] = "Virtual";
                SetPackageFile(invalidAdapter, cardPath, card.ToString()); File.WriteAllText(invalidPath, invalidAdapter.ToString());
                ExpectFailure(() => target.Preview(invalidPath), "不支持工程要求的运动卡");
                card["motionCards"][0]["adapterType"] = "ZMotion"; card["axisCount"] = 7;
                SetPackageFile(invalidAdapter, cardPath, card.ToString()); File.WriteAllText(invalidPath, invalidAdapter.ToString());
                ExpectFailure(() => target.Preview(invalidPath), "不支持工程要求的运动卡");
                JObject malicious = JObject.Parse(packageText);
                SetPackageFile(malicious, "../Runtime/RecoverySnapshot.json", "{}");
                File.WriteAllText(invalidPath, malicious.ToString());
                ExpectFailure(() => target.Preview(invalidPath), "不允许的路径");
                JObject duplicateCase = JObject.Parse(packageText);
                string existingAxis = (string)duplicateCase["files"]["HardwareConfig/AxisParameters.json"];
                SetPackageFile(duplicateCase, "HardwareConfig/axisparameters.json", existingAxis);
                File.WriteAllText(invalidPath, duplicateCase.ToString());
                ExpectFailure(() => target.Preview(invalidPath), "路径重复");
                File.WriteAllText(invalidPath, "{\"format\":\"A\",\"format\":\"B\"}");
                ExpectAnyFailure(() => target.Preview(invalidPath));

                string pending = Path.Combine(targetRoot, "Runtime/EquipmentProjectApplyPending.json");
                string manifest = File.ReadAllText(Path.Combine(backup, "BackupManifest.json"));
                File.WriteAllText(pending, "{损坏");
                ExpectAnyFailure(() => target.RecoverPendingApply(() => true));
                Require(target.HasPendingApply, "损坏的事务标记不能被当作已恢复。 ");
                JObject altered = JObject.Parse(manifest);
                JObject firstExisting = (JObject)((JArray)altered["Files"]).First(item => (bool)item["Existed"]);
                firstExisting["Sha256"] = "invalid";
                File.WriteAllText(pending, altered.ToString());
                ExpectFailure(() => target.RecoverPendingApply(() => true), "与备份清单不一致");
                Require(target.HasPendingApply && File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/AxisParameters.json")).Contains("SourceAxis"),
                    "错误备份导致恢复标记或运行配置被改变。 ");
                File.WriteAllText(pending, manifest);
                string backedUpFile = Path.Combine(backup, ((string)firstExisting["RelativePath"]).Replace('/', Path.DirectorySeparatorChar));
                byte[] originalBackup = File.ReadAllBytes(backedUpFile); File.WriteAllText(backedUpFile, "损坏");
                Require(target.ListBackups().Any(item => item.StartsWith(backup, StringComparison.OrdinalIgnoreCase) && item.Contains("损坏")),
                    "损坏的工程备份未被标记。 ");
                ExpectFailure(() => target.RecoverPendingApply(() => true), "备份缺失或内容变化");
                Require(target.HasPendingApply, "备份损坏时未保留中断标记。 ");
                File.WriteAllBytes(backedUpFile, originalBackup);
                ExpectFailure(() => target.RecoverPendingApply(() => false), "不能应用");
                string retainedPath = Path.Combine(targetRoot, "Parameters/DemoStations.json");
                File.WriteAllText(retainedPath, "{\"user\":\"回滚期间保留修改\"}");
                string collisionPath = Path.Combine(targetRoot, "HardwareConfig/Connection.json");
                int recoveryChecks = 0;
                ExpectFailure(() => target.RecoverPendingApply(() =>
                {
                    if (++recoveryChecks == 3) File.WriteAllText(collisionPath, "{\"external\":\"恢复期间修改\"}");
                    return true;
                }), "其他操作改变");
                Require(target.HasPendingApply && File.ReadAllText(collisionPath).Contains("恢复期间修改"), "恢复中覆盖了并发修改或丢失标记。 ");
                File.WriteAllBytes(collisionPath, originalConnection);
                target.RecoverPendingApply(() => true);
                Require(File.ReadAllText(retainedPath).Contains("回滚期间保留修改"), "恢复错误覆盖了非本次导入文件的修改。 ");
                Require(!target.HasPendingApply && File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/AxisParameters.json")).Contains("OldAxis"),
                    "中断恢复未恢复原配置。 ");
                Require(File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/Connection.json")).Contains("未引用的原文件"), "重启恢复时丢失原有同名硬件文件。 ");
                Require(File.ReadAllBytes(Path.Combine(targetRoot, "HardwareConfig/Connection.json")).SequenceEqual(originalConnection), "中断恢复未保留原始字节。 ");
                Require(File.ReadAllText(Path.Combine(targetRoot, "Runtime/RecoverySnapshot.json")).Contains("现场证据")
                    && File.ReadAllText(Path.Combine(targetRoot, "Drafts/TaskWorkspace.json")).Contains("任务草稿"), "中断恢复覆盖了运行数据。 ");
                CheckInterruptedProcess(root, package, targetRoot, originalConnection);
                if (args.Length == 1) CheckExistingSample(args[0], temporary);
                Console.WriteLine("设备工程包：跨设备导入、引用、保护记录、回滚、变更及损坏检查通过。");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }

        /// <summary>创建支持当前测试设备及控制器的工程服务。</summary>
        private static MachineProjectPackageService NewService(MachineConfigurationStore store, string machineId)
        { return new MachineProjectPackageService(store, machineId, new[] { "Demo" }, new[] { "ZMotion", "Virtual" }, null, CheckCard); }

        /// <summary>用测试 Host 声明的实际单卡能力验证工程要求。</summary>
        private static string CheckCard(MachineProjectCardSelection card)
        {
            return (card.Backend == "ZMotion" || card.Backend == "Virtual") && card.Vendor == "正运动"
                && card.CardType == "总线型" && card.AdapterType == "ZMotion" && new[] { 8, 16, 32, 64 }.Contains(card.AxisCount)
                && card.CardCount == 1 ? null : "测试 Host 不支持该控制卡组合。";
        }

        /// <summary>子进程在已写首个配置、事务标记存在时退出，模拟程序意外中断。</summary>
        private static int InterruptAfterFirstWrite(string root, string package)
        {
            var target = NewService(new MachineConfigurationStore(root), "Machine01");
            MachineProjectPreview preview = target.Preview(package);
            int calls = 0;
            target.Apply(preview, () => { if (++calls == 5) Environment.Exit(23); return true; });
            return 2;
        }

        /// <summary>启动独立进程中断应用，再由新服务实例检测标记并恢复原始配置。</summary>
        private static void CheckInterruptedProcess(string root, string package, string targetRoot, byte[] originalConnection)
        {
            var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                "--interrupt \"" + root + "\" \"" + package + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (Process child = Process.Start(info))
            {
                if (!child.WaitForExit(30000)) throw new TimeoutException("中断测试进程未结束。 ");
                Require(child.ExitCode == 23, "测试子进程没有在预期位置中断。 ");
            }
            var restarted = NewService(new MachineConfigurationStore(root), "Machine01");
            Require(restarted.HasPendingApply && File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/AxisParameters.json")).Contains("SourceAxis"),
                "中断后的现场没有保留标记和首个已写文件。 ");
            Require(!File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/Connection.json")).Contains("未引用的原文件"),
                "中断测试未覆盖到此前未引用的同名硬件文件。 ");
            ExpectFailure(() => restarted.Preview(package), "上次设备工程应用未完成");
            restarted.RecoverPendingApply(() => true);
            Require(!restarted.HasPendingApply && File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/AxisParameters.json")).Contains("OldAxis"),
                "重启后的工程回滚未完成。 ");
            Require(File.ReadAllText(Path.Combine(targetRoot, "HardwareConfig/Connection.json")).Contains("未引用的原文件"),
                "进程重启后未恢复此前未引用的同名硬件文件。 ");
            Require(File.ReadAllBytes(Path.Combine(targetRoot, "HardwareConfig/Connection.json")).SequenceEqual(originalConnection),
                "进程重启后未按原始字节恢复同名文件。 ");
        }

        /// <summary>文件在写前被外部更改时保留新内容和恢复证据，不自动覆盖。</summary>
        private static void CheckConcurrentChangeBeforeWrite(MachineProjectPackageService target, string targetRoot, string package)
        {
            MachineProjectPreview preview = target.Preview(package);
            string path = Path.Combine(targetRoot, "HardwareConfig/AxisParameters.json");
            byte[] original = File.ReadAllBytes(path); int calls = 0;
            ExpectFailure(() => target.Apply(preview, () => { if (++calls == 3) File.WriteAllText(path, "{\"external\":\"写前修改\"}"); return true; }), "备份恢复未完成");
            Require(target.HasPendingApply && File.ReadAllText(path).Contains("写前修改"), "写前并发修改被覆盖或没有保留标记。 ");
            ExpectFailure(() => target.RecoverPendingApply(() => true), "其他操作改变");
            File.WriteAllBytes(path, original);
            target.RecoverPendingApply(() => true);
        }

        /// <summary>包文件全部写完后被外部更改时不能显示成功或覆盖该更改。</summary>
        private static void CheckConcurrentChangeAfterWrite(MachineProjectPackageService target, string targetRoot, string package)
        {
            MachineProjectPreview preview = target.Preview(package);
            string path = Path.Combine(targetRoot, "HardwareConfig/AxisParameters.json");
            byte[] original = File.ReadAllBytes(path); int calls = 0;
            ExpectFailure(() => target.Apply(preview, () =>
            {
                if (++calls == preview.Files.Count + 3) File.WriteAllText(path, "{\"external\":\"提交前修改\"}");
                return true;
            }), "备份恢复未完成");
            Require(target.HasPendingApply && File.ReadAllText(path).Contains("提交前修改"), "提交前并发修改被覆盖或没有保留标记。 ");
            ExpectFailure(() => target.RecoverPendingApply(() => true), "其他操作改变");
            File.WriteAllBytes(path, original);
            target.RecoverPendingApply(() => true);
        }

        /// <summary>只读导出既有十三轴样例到临时包，再导入新临时设备目录验证实际格式。</summary>
        private static void CheckExistingSample(string sourceDirectory, string temporary)
        {
            var definitions = new[] { "Demo", "WeldingExample", "RotaryWelding", "RotaryMotionValidation" };
            var backends = new[] { "ZMotion", "Virtual" };
            var source = new MachineProjectPackageService(new MachineConfigurationStore(sourceDirectory), "Machine01", definitions, backends, null, CheckCard);
            string package = Path.Combine(temporary, "十三轴样例.mcpkg.json");
            source.Export(package);
            string destinationRoot = Path.Combine(temporary, "13axis-target");
            var destinationStore = new MachineConfigurationStore(destinationRoot);
            var destination = new MachineProjectPackageService(destinationStore, "Machine01", definitions, backends, null, CheckCard);
            MachineProjectPreview preview = destination.Preview(package);
            destination.Apply(preview, () => true);
            string axisPath = Path.Combine(destinationStore.GetMachineDirectory("Machine01"), "HardwareConfig/AxisParameters.json");
            var axes = (JArray)JObject.Parse(File.ReadAllText(axisPath))["axes"];
            Require(axes.Count(item => !string.IsNullOrWhiteSpace((string)item["name"])) >= 13, "十三轴样例映射数量不完整。 ");
            Console.WriteLine("十三轴现有配置：工程导出、预检和隔离目录导入通过。 ");
        }

        /// <summary>写入一台独立测试机的完整轴、IO 和任务配置。</summary>
        private static void CreateFixture(string root, string machineId, string axisName, string owner)
        {
            string connectionName = owner == "TaskEngine" ? "Connection.json" : "OldConnection.json";
            Write(root, "HardwareConfig/MotionCards.json", "{\"schemaVersion\":1,\"motionCardVendor\":\"正运动\",\"motionCardType\":\"总线型\",\"axisCount\":8,\"motionCards\":[{\"controllerId\":\"Card\",\"adapterType\":\"ZMotion\",\"connectionConfigurationFile\":\"HardwareConfig/" + connectionName + "\"}]}");
            Write(root, "HardwareConfig/" + connectionName, "{\"schemaVersion\":1}");
            Write(root, "HardwareConfig/AxisParameters.json", "{\"schemaVersion\":1,\"axes\":[{\"controllerId\":\"Card\",\"axisNumber\":0,\"name\":\"" + axisName + "\",\"engineeringUnit\":\"mm\",\"unitsPerEngineeringUnit\":1,\"minimumPosition\":-10,\"maximumPosition\":10,\"softLimitEnabled\":true,\"manualSpeed\":1}]}");
            Write(root, "HardwareConfig/IoMapping.json", "{\"schemaVersion\":1,\"digitalInputs\":[{\"controllerId\":\"Card\",\"name\":\"急停\",\"channel\":0,\"activeHigh\":true}],\"digitalOutputs\":[]}");
            Write(root, "HardwareConfig/SafetyInterlock.json", "{\"schemaVersion\":1,\"emergencyStopInputName\":\"急停\"}");
            Write(root, "Parameters/Equipment.json", "{\"schemaVersion\":1,\"definition\":\"Demo\",\"executionOwner\":\"" + owner + "\"}");
            Write(root, "Parameters/ControllerBackend.json", "{\"backend\":\"ZMotion\"}");
            if (owner != "TaskEngine") return;
            Write(root, "Parameters/TaskMachine.json", "{\"schemaVersion\":1,\"machineId\":\"" + machineId + "\",\"name\":\"单工位测试\",\"revision\":1,\"namedResources\":[],\"stations\":[{\"stationId\":\"S1\",\"name\":\"工位\",\"taskId\":\"T1\",\"enabled\":true,\"mode\":\"Cyclic\",\"cycleIntervalMs\":100}]}");
            Write(root, "Tasks/T1.json", "{\"schemaVersion\":1,\"taskId\":\"T1\",\"name\":\"测试任务\",\"revision\":1,\"root\":{\"kind\":\"Sequence\",\"id\":\"root\",\"name\":\"顺序\",\"timeoutMs\":30000,\"children\":[{\"kind\":\"Delay\",\"id\":\"wait\",\"name\":\"延时\",\"timeoutMs\":1000,\"milliseconds\":100}]}}");
        }

        /// <summary>创建测试专用文件及父目录。</summary>
        private static void Write(string root, string relative, string text)
        { string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, new UTF8Encoding(false)); }

        /// <summary>修改测试工程中的一份文件并更新其校验值，以验证语义校验。</summary>
        private static void SetPackageFile(JObject package, string path, string contents)
        {
            package["files"][path] = contents;
            using (var hash = SHA256.Create()) package["hashes"][path] = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(contents)));
        }

        /// <summary>断言条件不满足时报告具体检查内容。</summary>
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        /// <summary>断言操作失败且原因包含预期上下文。</summary>
        private static void ExpectFailure(Action action, string message)
        {
            try { action(); }
            catch (Exception error) { if (error.ToString().Contains(message)) return; throw; }
            throw new InvalidOperationException("操作意外成功：" + message);
        }

        /// <summary>断言格式损坏的输入不会被接受。</summary>
        private static void ExpectAnyFailure(Action action)
        {
            try { action(); }
            catch (Exception) { return; }
            throw new InvalidOperationException("损坏的输入意外通过。 ");
        }
    }
}
