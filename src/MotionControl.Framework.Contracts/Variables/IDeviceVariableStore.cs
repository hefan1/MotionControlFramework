using System;
using System.Collections.Generic;

namespace MotionControl.Framework.Contracts.Variables
{
    /// <summary>设备流程和监控页面共用的单机变量读写入口。</summary>
    public interface IDeviceVariableStore
    {
        /// <summary>变量值变化后通知监控页面刷新。</summary>
        event EventHandler VariablesChanged;

        /// <summary>返回变量定义副本，供 Host 保存设备配置。</summary>
        IReadOnlyList<DeviceVariableDefinition> GetDefinitions();

        /// <summary>返回当前变量值快照，供监控页面显示。</summary>
        IReadOnlyList<DeviceVariableSnapshot> GetSnapshot();

        /// <summary>读取布尔类型变量；变量不存在或类型不匹配时抛出可读异常。</summary>
        bool GetBoolean(string name);

        /// <summary>读取整数类型变量；变量不存在或类型不匹配时抛出可读异常。</summary>
        int GetInteger(string name);

        /// <summary>读取小数类型变量；变量不存在或类型不匹配时抛出可读异常。</summary>
        double GetNumber(string name);

        /// <summary>读取字符串类型变量；变量不存在或类型不匹配时抛出可读异常。</summary>
        string GetString(string name);

        /// <summary>由设备流程更新布尔类型变量。</summary>
        void SetBoolean(string name, bool value);

        /// <summary>由设备流程更新整数类型变量。</summary>
        void SetInteger(string name, int value);

        /// <summary>由设备流程更新小数类型变量。</summary>
        void SetNumber(string name, double value);

        /// <summary>由设备流程更新字符串类型变量。</summary>
        void SetString(string name, string value);

        /// <summary>按名称、类型和值替换设备变量定义；相同名称和类型会保留当前运行值。</summary>
        void ReplaceDefinitions(IEnumerable<DeviceVariableDefinition> definitions);

        /// <summary>按类型校验并写入现场人员输入的当前值。</summary>
        bool TrySetOperatorValue(string name, string valueText, out string errorMessage);
    }
}
