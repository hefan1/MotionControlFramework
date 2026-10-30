using System;
using System.IO;

namespace MotionControl.Framework.Runtime.Configuration
{
    /// <summary>读写每台设备的报警目录，并在首次使用时安装明确传入的默认报警。</summary>
    public sealed class MachineAlarmCatalogStore
    {
        /// <summary>报警目录在设备配置目录中的固定位置。</summary>
        public const string RelativeFilePath = "Parameters/AlarmCatalog.json";

        /// <summary>复用设备隔离、原子写入和备份机制。</summary>
        private readonly MachineConfigurationStore _store;

        /// <summary>绑定现有设备配置目录。</summary>
        public MachineAlarmCatalogStore(MachineConfigurationStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>首次启动时只创建缺少的文件，不覆盖现场已有报警库。</summary>
        public MachineAlarmCatalogDocument LoadOrCreate(string machineId, MachineAlarmCatalogDocument defaults)
        {
            if (defaults == null) throw new ArgumentNullException(nameof(defaults));
            string path = Path.Combine(_store.GetMachineDirectory(machineId), RelativeFilePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                Save(machineId, defaults);
                return Copy(defaults);
            }
            return Load(machineId);
        }

        /// <summary>读取并检查现有报警库。</summary>
        public MachineAlarmCatalogDocument Load(string machineId)
        {
            MachineAlarmCatalogDocument document = _store.LoadJson<MachineAlarmCatalogDocument>(machineId, RelativeFilePath);
            document.Validate();
            return document;
        }

        /// <summary>校验后原子保存报警库，返回旧文件备份路径。</summary>
        public string Save(string machineId, MachineAlarmCatalogDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Validate();
            return _store.SaveJson(machineId, RelativeFilePath, document);
        }

        /// <summary>复制默认报警目录，避免调用方后续改动默认对象。</summary>
        private static MachineAlarmCatalogDocument Copy(MachineAlarmCatalogDocument document)
        {
            var copy = new MachineAlarmCatalogDocument { SchemaVersion = document.SchemaVersion };
            foreach (MachineAlarmCatalogEntry alarm in document.Alarms)
                copy.Alarms.Add(alarm.Copy());
            return copy;
        }
    }
}
