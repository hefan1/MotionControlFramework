using System;
using System.Collections.Generic;
using System.IO;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Preparation;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json;

namespace MotionControl.TaskEngine.Editing
{
    public sealed partial class TaskWorkspaceStore
    {
        /// <summary>准备文件只在检查时读取，不写入任务草稿，也不会热更新正在运行的会话。</summary>
        private IReadOnlyList<TaskValidationIssue> CheckPreparation(TaskWorkspaceDocument draft, TaskResourceCatalog catalog,
            IEnumerable<TaskDefinition> definitions)
        {
            try
            {
                TaskStationPreparationConfiguration configuration = new TaskStationPreparationStore(_store).Load(draft.MachineId);
                if (configuration == null) return new List<TaskValidationIssue>().AsReadOnly();
                return TaskStationPreparationValidator.Validate(configuration, catalog, definitions);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException)
            {
                return new List<TaskValidationIssue>
                {
                    new TaskValidationIssue(TaskIssueSeverity.Error, null, null, TaskStationPreparationStore.RelativePath,
                        "准备配置读取或解析失败，不能确认引用完整性：" + error.Message)
                }.AsReadOnly();
            }
        }

        /// <summary>供页面发现外部准备文件变化；明确不存在时返回 null，读取失败必须由页面显示。</summary>
        public string ComputePreparationSignature(string machineId)
        {
            string path = Path.Combine(_store.GetMachineDirectory(machineId), TaskStationPreparationStore.RelativePath);
            try { return HashBytes(File.ReadAllBytes(path)); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }
}
