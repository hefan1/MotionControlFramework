using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Preparation;
using MotionControl.TaskEngine.Validation;
using MotionControl.TaskEngine.Wpf.Editing;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEditing.Checks
{
    internal static partial class Program
    {
        /// <summary>仅被准备配置引用的任务也有明确所有者和字段位置，界面与应用都阻止删除。</summary>
        private static void V18PreparationReferences()
        {
            using (var test = new Workspace())
            {
                TaskWorkspaceDocument draft = CreatePreparationWorkspace(test);
                var references = test.Store.FindTaskReferences(draft, "preparationonly");
                Assert(references.Count == 1 && references[0].Kind == TaskWorkspaceReferenceKind.Preparation,
                    "准备专用任务没有进入统一引用列表。");
                Assert(references[0].OwnerId == "IndependentPreparation" && references[0].FieldPath.EndsWith("stations[0].taskId"),
                    "准备引用没有保留所有者和配置字段。");
                Assert(!draft.Machine.Stations.Any(station => station.StationId == "IndependentPreparation"),
                    "测试不应通过伪造同名生产工位来满足准备配置。");
                Assert(!test.Store.Check(draft).Any(issue => issue.Severity == TaskIssueSeverity.Error),
                    "独立准备标识被错误要求对应生产工位。");

                string before = test.ActiveBytes();
                draft.Tasks.RemoveAll(item => item.Definition.TaskId == "PreparationOnly");
                Assert(test.Store.Check(draft).Any(issue => issue.Severity == TaskIssueSeverity.Error
                    && issue.TaskId == "PreparationOnly" && issue.Field.EndsWith("stations[0].taskId")), "缺准备任务没有在工作区检查时定位。");
                string warning;
                Throws(() => test.Store.Apply(draft, out warning));
                Assert(test.ActiveBytes() == before && !test.Store.HasPendingApply(Workspace.Id), "准备引用错误仍写入了运行文件。");

                var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                var grid = (DataGrid)page.FindName("TasksGrid");
                grid.SelectedItem = grid.Items.Cast<TaskLibraryRow>().Single(row => row.Definition.TaskId == "PreparationOnly");
                var remove = (Button)page.FindName("RemoveTaskButton");
                Assert(!remove.IsEnabled && remove.ToolTip.ToString().Contains("StationPreparation.json"),
                    "被准备引用的任务删除按钮未禁用或缺少定位原因。");
                var viewReferences = (List<string>)typeof(TaskConfigurationView).GetMethod("References", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(page, new object[] { "PreparationOnly" });
                Assert(viewReferences.Single().Contains("独立准备组") && viewReferences.Single().Contains("stations[0].taskId"),
                    "查看引用没有显示准备名称和真实字段。");
            }
        }

        /// <summary>外部只修改准备配置也改变来源指纹，并清除页面原检查结论，运行任务字节保持不变。</summary>
        private static void V18PreparationSourceChanges()
        {
            using (var test = new Workspace())
            {
                TaskWorkspaceDocument draft = CreatePreparationWorkspace(test);
                string signature = test.Store.ComputeSignature(Workspace.Id);
                string activeBefore = test.ActiveBytes();
                var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                typeof(TaskConfigurationView).GetMethod("Check_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(page, new object[] { page, new RoutedEventArgs() });
                FieldInfo checkResults = typeof(TaskConfigurationView).GetField("_lastCheckIssues", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(checkResults.GetValue(page) != null, "有效配置没有形成初始检查结果。");

                string path = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), TaskStationPreparationStore.RelativePath);
                JObject preparation = JObject.Parse(File.ReadAllText(path));
                preparation["stations"][0]["name"] = "外部修改后的准备组";
                File.WriteAllText(path, preparation.ToString());
                Assert(test.Store.ComputeSignature(Workspace.Id) != signature, "准备文件变化没有改变任务来源指纹。");
                typeof(TaskConfigurationView).GetMethod("RefreshPermission", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(page, new object[] { page, EventArgs.Empty });
                Assert(checkResults.GetValue(page) == null, "准备文件已变化却仍保留旧检查结果。");
                Assert(!((Button)page.FindName("ApplyButton")).IsEnabled, "外部修改准备文件后旧草稿仍能应用。");
                Assert(((TextBlock)page.FindName("StatusText")).Text.Contains("旧检查结论失效"), "没有解释准备变化使检查失效。");
                string warning;
                Throws(() => test.Store.Apply(draft, out warning));
                Assert(test.ActiveBytes() == activeBefore, "观察准备来源变化时改写了运行任务。");
            }
        }

        /// <summary>共享纯校验提前识别缺任务、依赖环、错误轴、错误输出和显式 null 列表。</summary>
        private static void V18PreparationValidation()
        {
            using (var test = new Workspace())
            {
                TaskWorkspaceDocument draft = CreatePreparationWorkspace(test);
                var catalog = test.Store.LoadCatalog(draft);
                var definitions = draft.Tasks.Select(item => item.Definition).ToList();
                var store = new TaskStationPreparationStore(test.Configuration);

                TaskStationPreparationConfiguration configuration = store.Load(Workspace.Id);
                configuration.Stations[0].TaskId = null;
                RequirePreparationIssue(TaskStationPreparationValidator.Validate(configuration, catalog, definitions), ".taskId");

                configuration = store.Load(Workspace.Id);
                configuration.Stations[0].Axes[0].Name = "MissingPreparationAxis";
                RequirePreparationIssue(TaskStationPreparationValidator.Validate(configuration, catalog, definitions), ".axes[0].name");

                configuration = store.Load(Workspace.Id);
                configuration.Stations[0].Outputs.Add(new TaskPreparationSignal { Name = "MissingPreparationOutput", Expected = false });
                RequirePreparationIssue(TaskStationPreparationValidator.Validate(configuration, catalog, definitions), ".outputs[0].name");

                configuration = store.Load(Workspace.Id);
                definitions.Add(NewPreparationCheckTask("PreparationSecond"));
                configuration.Stations.Add(new TaskStationPreparationDefinition
                {
                    Id = "SecondPreparation", Name = "第二准备组", TaskId = "PreparationSecond",
                    Dependencies = new List<string> { "IndependentPreparation" },
                    Inputs = new List<TaskPreparationSignal> { new TaskPreparationSignal { Name = catalog.Inputs.First(), Expected = false } }
                });
                configuration.Stations[0].Dependencies.Add("SecondPreparation");
                Assert(TaskStationPreparationValidator.Validate(configuration, catalog, definitions)
                    .Any(issue => issue.Severity == TaskIssueSeverity.Error && issue.Message.Contains("依赖存在循环")), "准备依赖环未被纯校验拒绝。");

                string path = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), TaskStationPreparationStore.RelativePath);
                JObject nullList = JObject.Parse(File.ReadAllText(path));
                nullList["stations"][0]["dependencies"] = JValue.CreateNull();
                configuration = TaskStationPreparationStore.Deserialize(nullList.ToString());
                Assert(TaskStationPreparationValidator.Validate(configuration, catalog, definitions)
                    .Any(issue => issue.Message.Contains("不能为 null")), "显式 null 准备列表被静默变成默认空列表。");
            }
        }

        /// <summary>创建一个与生产工位完全独立的准备任务及合法轴验收条件，只写本检查的临时目录。</summary>
        private static TaskWorkspaceDocument CreatePreparationWorkspace(Workspace test)
        {
            TaskWorkspaceDocument draft = test.Store.Load(Workspace.Id, false);
            draft.Tasks.Add(new TaskWorkspaceTask { FileName = "PreparationOnly.json", Definition = NewPreparationCheckTask("PreparationOnly") });
            string warning;
            draft = test.Store.Apply(draft, out warning);
            var axis = test.Store.LoadCatalog(draft).Axes.Values.First();
            var preparation = new TaskStationPreparationConfiguration
            {
                SchemaVersion = 1,
                Stations = new List<TaskStationPreparationDefinition>
                {
                    new TaskStationPreparationDefinition
                    {
                        Id = "IndependentPreparation", Name = "独立准备组", TaskId = "PreparationOnly",
                        Axes = new List<TaskPreparationAxis>
                        {
                            new TaskPreparationAxis { Name = axis.Name, Position = axis.MinimumPosition.Value, Tolerance = 0.01 }
                        }
                    }
                }
            };
            test.Configuration.SaveJson(Workspace.Id, TaskStationPreparationStore.RelativePath, preparation);
            return test.Store.Load(Workspace.Id, false);
        }

        /// <summary>建立不发送设备动作的独立延时任务，便于只验证引用和文件边界。</summary>
        private static TaskDefinition NewPreparationCheckTask(string id)
        {
            return new TaskDefinition
            {
                TaskId = id, Name = id, Revision = 1,
                Root = new SequenceNode { Id = "root", Name = "准备检查", TimeoutMs = 1000,
                    Children = new List<TaskNode> { new DelayNode { Id = "wait", Name = "延时", Milliseconds = 1, TimeoutMs = 500 } } }
            };
        }

        /// <summary>要求准备错误保留原文件及字段位置，不能只返回泛化的失败文案。</summary>
        private static void RequirePreparationIssue(IEnumerable<TaskValidationIssue> issues, string fieldSuffix)
        {
            Assert(issues.Any(issue => issue.Severity == TaskIssueSeverity.Error
                && issue.Field.StartsWith(TaskStationPreparationStore.RelativePath, StringComparison.Ordinal)
                && issue.Field.EndsWith(fieldSuffix, StringComparison.Ordinal)), "准备校验没有定位字段：" + fieldSuffix);
        }
    }
}
