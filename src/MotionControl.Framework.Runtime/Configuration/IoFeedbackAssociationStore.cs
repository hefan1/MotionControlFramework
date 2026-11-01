using System;
using System.Collections.Generic;
using System.IO;

namespace MotionControl.Framework.Runtime.Configuration
{
    /// <summary>独立读取和保存 IO 反馈显示关系，不修改物理 IO 映射。</summary>
    public sealed class IoFeedbackAssociationStore
    {
        /// <summary>每台设备目录下的关联配置路径。</summary>
        public const string RelativePath = "Parameters/IoFeedbackAssociations.json";

        /// <summary>复用设备配置存储的路径检查与安全保存。</summary>
        private readonly MachineConfigurationStore _store;

        /// <summary>当前物理设备编号。</summary>
        private readonly string _machineId;

        /// <summary>绑定配置存储与设备编号。</summary>
        public IoFeedbackAssociationStore(MachineConfigurationStore store, string machineId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (string.IsNullOrWhiteSpace(machineId)) throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            _machineId = machineId;
        }

        /// <summary>旧设备没有关系文件时返回空配置；损坏或不支持的配置明确报错。</summary>
        public IoFeedbackAssociationConfiguration Load()
        {
            string path = Path.Combine(_store.GetMachineDirectory(_machineId), RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return new IoFeedbackAssociationConfiguration();
            var configuration = _store.LoadJson<IoFeedbackAssociationConfiguration>(_machineId, RelativePath);
            Validate(configuration);
            return configuration;
        }

        /// <summary>校验格式后保存；具体 IO 是否仍存在由配置页面和监控页分别提示。</summary>
        public void Save(IoFeedbackAssociationConfiguration configuration)
        {
            Validate(configuration);
            _store.SaveJson(_machineId, RelativePath, configuration);
        }

        /// <summary>拒绝空名称和重复引用，防止同一输出出现不确定的多个关系。</summary>
        private static void Validate(IoFeedbackAssociationConfiguration configuration)
        {
            if (configuration == null || configuration.SchemaVersion != 1 || configuration.Associations == null)
                throw new InvalidOperationException("IO 反馈关联配置格式无效或版本不受支持。请检查配置文件。");
            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IoFeedbackAssociation item in configuration.Associations)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.OutputName) || item.InputNames == null)
                    throw new InvalidOperationException("IO 反馈关联包含空输出名称或无效输入列表。");
                if (!string.Equals(item.OutputName, item.OutputName.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("输出名称前后不能有空格：" + item.OutputName);
                if (!outputs.Add(item.OutputName.Trim()))
                    throw new InvalidOperationException("IO 反馈关联包含重复输出：" + item.OutputName);
                var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string input in item.InputNames)
                {
                    if (string.IsNullOrWhiteSpace(input) || !string.Equals(input, input.Trim(), StringComparison.Ordinal) || !inputs.Add(input))
                        throw new InvalidOperationException("输出“" + item.OutputName + "”包含空的或重复的反馈输入。");
                }
            }
        }
    }
}
