using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf
{
    /// <summary>
    /// 把 Host 当前已加载的设备配置、硬件映射和控制卡连接包装成设备定义可用的环境，
    /// 使设备定义不必引用配置存储、控制卡连接或 WPF 页面。
    /// </summary>
    internal sealed class HostEquipmentContext : IEquipmentContext
    {
        /// <summary>当前设备的 JSON 配置存储。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>当前设备标识，决定读取哪个设备配置目录。</summary>
        private readonly string _equipmentId;

        /// <summary>当前设备报警库，用于把报警代码解析为报警定义。</summary>
        private readonly MachineAlarmCatalogDocument _alarmCatalog;

        /// <summary>已映射的逻辑轴定义。</summary>
        private readonly IReadOnlyList<AxisHardwareDefinition> _axisDefinitions;

        /// <summary>已映射的逻辑 DI 定义。</summary>
        private readonly IReadOnlyList<DigitalIoHardwareDefinition> _inputDefinitions;

        /// <summary>已映射的逻辑 DO 定义。</summary>
        private readonly IReadOnlyList<DigitalIoHardwareDefinition> _outputDefinitions;

        /// <summary>已连接的控制卡组，用于按逻辑轴名称取轴。</summary>
        private readonly MotionCardGroup _motionCards;

        /// <summary>按逻辑名称读写 DI/DO 的入口。</summary>
        private readonly ILogicalIo _logicalIo;

        /// <summary>设备级报警服务。</summary>
        private readonly IMachineAlarmService _alarmService;

        /// <summary>创建一次连接周期内使用的设备环境；输入均来自当前已连接的卡和已加载的配置。</summary>
        internal HostEquipmentContext(
            MachineConfigurationStore configurationStore,
            string equipmentId,
            MachineAlarmCatalogDocument alarmCatalog,
            IReadOnlyList<AxisHardwareDefinition> axisDefinitions,
            IReadOnlyList<DigitalIoHardwareDefinition> inputDefinitions,
            IReadOnlyList<DigitalIoHardwareDefinition> outputDefinitions,
            MotionCardGroup motionCards,
            ILogicalIo logicalIo,
            IMachineAlarmService alarmService)
        {
            if (configurationStore == null) throw new ArgumentNullException(nameof(configurationStore));
            if (alarmCatalog == null) throw new ArgumentNullException(nameof(alarmCatalog));
            if (motionCards == null) throw new ArgumentNullException(nameof(motionCards));
            if (logicalIo == null) throw new ArgumentNullException(nameof(logicalIo));
            if (alarmService == null) throw new ArgumentNullException(nameof(alarmService));

            _configurationStore = configurationStore;
            _equipmentId = equipmentId;
            _alarmCatalog = alarmCatalog;
            _axisDefinitions = axisDefinitions ?? new AxisHardwareDefinition[0];
            _inputDefinitions = inputDefinitions ?? new DigitalIoHardwareDefinition[0];
            _outputDefinitions = outputDefinitions ?? new DigitalIoHardwareDefinition[0];
            _motionCards = motionCards;
            _logicalIo = logicalIo;
            _alarmService = alarmService;
        }

        /// <summary>当前设备标识。</summary>
        public string EquipmentId
        {
            get { return _equipmentId; }
        }

        /// <summary>已映射的逻辑轴硬件定义。</summary>
        public IReadOnlyList<AxisHardwareDefinition> AxisDefinitions
        {
            get { return _axisDefinitions; }
        }

        /// <summary>已映射的逻辑 DI 定义。</summary>
        public IReadOnlyList<DigitalIoHardwareDefinition> InputDefinitions
        {
            get { return _inputDefinitions; }
        }

        /// <summary>已映射的逻辑 DO 定义。</summary>
        public IReadOnlyList<DigitalIoHardwareDefinition> OutputDefinitions
        {
            get { return _outputDefinitions; }
        }

        /// <summary>按逻辑名称读写 DI/DO 的入口。</summary>
        public ILogicalIo LogicalIo
        {
            get { return _logicalIo; }
        }

        /// <summary>设备级报警服务。</summary>
        public IMachineAlarmService AlarmService
        {
            get { return _alarmService; }
        }

        /// <summary>判断设备配置目录下是否存在指定相对路径的文件。</summary>
        public bool FileExists(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("配置相对路径不能为空。", nameof(relativePath));
            }

            string fullPath = Path.Combine(
                _configurationStore.GetMachineDirectory(_equipmentId),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath);
        }

        /// <summary>从设备配置目录读取指定相对路径的 JSON 配置。</summary>
        public T LoadJson<T>(string relativePath) where T : class
        {
            return _configurationStore.LoadJson<T>(_equipmentId, relativePath);
        }

        /// <summary>按报警代码从设备报警库取出报警定义，代码缺失时报告可定位的错误。</summary>
        public MachineAlarmDefinition ResolveAlarm(string alarmCode)
        {
            MachineAlarmCatalogEntry entry = _alarmCatalog.Alarms.FirstOrDefault(
                alarm => string.Equals(alarm.Code, alarmCode, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new InvalidOperationException("设备报警库缺少工位已关联的报警代码：" + alarmCode + "。");
            }

            return entry.ToDefinition();
        }

        /// <summary>按逻辑轴名称取轴；名称未映射时由控制卡组报告错误。</summary>
        public IAxis GetAxis(string axisName)
        {
            return _motionCards.GetAxis(axisName);
        }
    }
}
