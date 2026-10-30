using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using MotionControl.Framework.Contracts.Alarms;

namespace MotionControl.Framework.Runtime.Configuration
{
    /// <summary>一台设备可维护的报警目录文件内容。</summary>
    public sealed class MachineAlarmCatalogDocument
    {
        /// <summary>当前报警目录 JSON 格式版本。</summary>
        public int SchemaVersion { get; set; } = 1;

        /// <summary>可供设备流程和任务步骤引用的报警定义。</summary>
        public List<MachineAlarmCatalogEntry> Alarms { get; set; } = new List<MachineAlarmCatalogEntry>();

        /// <summary>检查版本、报警代码、说明和类型；无效配置不会进入运行时。</summary>
        public void Validate()
        {
            if (SchemaVersion != 1)
                throw new InvalidOperationException("只支持 schemaVersion 为 1 的 AlarmCatalog.json。");
            if (Alarms == null)
                throw new InvalidOperationException("AlarmCatalog.json 的 alarms 不能为 null。");

            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MachineAlarmCatalogEntry alarm in Alarms)
            {
                if (alarm == null)
                    throw new InvalidOperationException("报警目录包含空记录。");
                alarm.Validate();
                if (!codes.Add(alarm.Code))
                    throw new InvalidOperationException("报警代码重复：" + alarm.Code + "。");
            }
        }
    }

    /// <summary>报警库中可编辑并由多个流程或任务节点引用的一条报警定义。</summary>
    public sealed class MachineAlarmCatalogEntry
    {
        /// <summary>稳定报警代码；被任务或设备流程引用后应保持不变。</summary>
        public string Code { get; set; }

        /// <summary>报警发生时显示给操作人员的简短说明。</summary>
        public string Message { get; set; }

        /// <summary>停机报警会暂停整机并等待复位；非停机报警只提示并随条件恢复清除。</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public MachineAlarmKind Kind { get; set; }

        /// <summary>操作人员处理此报警前应先检查的内容。</summary>
        public string CheckGuidance { get; set; }

        /// <summary>条件处理后的复位或恢复步骤。</summary>
        public string RecoveryGuidance { get; set; }

        /// <summary>供报警目录列表显示的中文报警类别。</summary>
        [JsonIgnore]
        public string KindDisplay
        {
            get { return Kind == MachineAlarmKind.StopMachine ? "停机报警" : "非停机报警"; }
        }

        /// <summary>验证代码格式、说明和报警类型，不接受空格误差或重复语义。</summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Code) || Code != Code.Trim()
                || Code.Length > 64 || Code.Any(character => !(char.IsLetterOrDigit(character)
                    || character == '_' || character == '-' || character == '.')))
                throw new InvalidOperationException("报警代码必须是 1 到 64 位字母、数字、下划线、短横线或点号，且不能带首尾空格。");
            if (string.IsNullOrWhiteSpace(Message) || Message != Message.Trim())
                throw new InvalidOperationException("报警 " + Code + " 的显示说明不能为空或带首尾空格。");
            if (Kind != MachineAlarmKind.StopMachine && Kind != MachineAlarmKind.NonStop)
                throw new InvalidOperationException("报警 " + Code + " 的类型无效。");
            if (CheckGuidance != null && CheckGuidance != CheckGuidance.Trim())
                throw new InvalidOperationException("报警 " + Code + " 的检查指导不能带首尾空格。");
            if (RecoveryGuidance != null && RecoveryGuidance != RecoveryGuidance.Trim())
                throw new InvalidOperationException("报警 " + Code + " 的恢复指导不能带首尾空格。");
        }

        /// <summary>将设备目录记录转换为不可变 Runtime 报警定义。</summary>
        public MachineAlarmDefinition ToDefinition()
        {
            Validate();
            return new MachineAlarmDefinition(Code, Message, Kind, CheckGuidance, RecoveryGuidance);
        }

        /// <summary>建立供页面编辑使用的独立副本，避免修改已加载的运行时定义。</summary>
        public MachineAlarmCatalogEntry Copy()
        {
            return new MachineAlarmCatalogEntry
            {
                Code = Code,
                Message = Message,
                Kind = Kind,
                CheckGuidance = CheckGuidance,
                RecoveryGuidance = RecoveryGuidance
            };
        }
    }
}
