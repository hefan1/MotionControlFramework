using System;
using System.IO;
using MotionControl.TaskEngine.Configuration;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>检查原子保存、修订保护、设备隔离和无效文件处理。</summary>
    internal static class PersistenceChecks
    {
        /// <summary>保存和覆盖后可以读取，旧文件有备份且原版本内容仍在。</summary>
        internal static void SaveAndBackup()
        {
            using (var workspace = new TestWorkspace())
            {
                var store = new TaskDefinitionStore(workspace.Store);
                var definition = TaskFixtures.Task(TaskFixtures.Move());
                CheckAssert.True(store.Save("Test01", "Transfer.json", definition, TaskFixtures.Catalog()) == null, "首次保存不应有备份。");
                CheckAssert.True(store.LoadForEditing("Test01", "Transfer.json").TaskId == definition.TaskId, "任务读取失败。");
                CheckAssert.True(store.Load("Test01", "Transfer.json", TaskFixtures.Catalog()).TaskId == definition.TaskId, "带引用校验的读取失败。");
                definition.Revision++;
                definition.Name = "更新后的任务名称";
                string backup = store.Save("Test01", "Transfer.json", definition, TaskFixtures.Catalog());
                CheckAssert.True(File.Exists(backup), "覆盖后没有保留备份。");
                CheckAssert.True(TaskDefinitionJson.Deserialize(File.ReadAllText(backup)).Revision == 1, "备份不是旧修订。");
                CheckAssert.True(store.LoadForEditing("Test01", "Transfer.json").Revision == 2, "新版本没有生效。");
                store.Save("Test02", "Transfer.json", definition, TaskFixtures.Catalog());
                CheckAssert.True(File.Exists(Path.Combine(workspace.Root, "Test02", "Tasks", "Transfer.json")), "设备配置没有隔离。");
            }
        }

        /// <summary>无效定义、错误任务 ID 和未递增修订不会覆盖已有文件。</summary>
        internal static void FailedSaveKeepsFile()
        {
            using (var workspace = new TestWorkspace())
            {
                var store = new TaskDefinitionStore(workspace.Store);
                var move = TaskFixtures.Move();
                var definition = TaskFixtures.Task(move);
                store.Save("Test01", "Transfer.json", definition, TaskFixtures.Catalog());
                string path = Path.Combine(workspace.Root, "Test01", "Tasks", "Transfer.json");
                string original = File.ReadAllText(path);
                definition.Name = "改名但未递增";
                CheckAssert.Throws(() => store.Save("Test01", "Transfer.json", definition, TaskFixtures.Catalog()), "未递增修订仍覆盖成功。");
                definition.Revision++;
                move.Target.Constant = "999";
                CheckAssert.Throws(() => store.Save("Test01", "Transfer.json", definition, TaskFixtures.Catalog()), "越界任务仍保存。");
                move.Target.Constant = "10";
                definition.TaskId = "Other";
                CheckAssert.Throws(() => store.Save("Test01", "Transfer.json", definition, TaskFixtures.Catalog()), "其他任务覆盖成功。");
                CheckAssert.True(File.ReadAllText(path) == original, "失败保存改变了原文件。");
                CheckAssert.True(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "留下临时文件。");
            }
        }

        /// <summary>未来版本、损坏旧文件以及越目录文件名不会被静默覆盖。</summary>
        internal static void VersionAndPathProtection()
        {
            using (var workspace = new TestWorkspace())
            {
                var store = new TaskDefinitionStore(workspace.Store);
                var definition = TaskFixtures.Task(TaskFixtures.Move());
                store.Save("Test01", "Transfer.json", definition, TaskFixtures.Catalog());
                string path = Path.Combine(workspace.Root, "Test01", "Tasks", "Transfer.json");
                string future = File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
                File.WriteAllText(path, future);
                definition.Revision++;
                CheckAssert.Throws(() => store.Save("Test01", "Transfer.json", definition, TaskFixtures.Catalog()), "覆盖了未来版本。");
                CheckAssert.True(File.ReadAllText(path) == future, "未来文件被修改。");
                File.WriteAllText(path, "{bad json");
                CheckAssert.Throws(() => store.LoadForEditing("Test01", "Transfer.json"), "损坏文件被接受。");
                CheckAssert.Throws(() => store.Save("Test01", "Transfer.json", definition, TaskFixtures.Catalog()), "损坏文件被自动覆盖。");
                foreach (string fileName in new[] { "../Outside.json", "sub/file.json", "sub\\file.json", ".json", "bad.txt" })
                    CheckAssert.Throws(() => store.Save("Test01", fileName, definition, TaskFixtures.Catalog()), "非法路径被接受。");
                CheckAssert.Throws(() => store.Save("../Other", "Transfer.json", definition, TaskFixtures.Catalog()), "非法设备 ID 被接受。");
            }
        }
    }
}
