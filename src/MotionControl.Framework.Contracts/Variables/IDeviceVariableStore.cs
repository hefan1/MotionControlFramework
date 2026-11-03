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

        /// <summary>恢复名称和类型匹配的已保存值；不匹配或格式无效的项会返回说明并跳过。</summary>
        IReadOnlyList<string> RestoreCurrentValues(IEnumerable<DeviceVariableSnapshot> snapshots);

        /// <summary>读取布尔类型变量；变量不存在或类型不匹配时抛出可读异常。</summary>
        bool GetBoolean(string name);

        /// <summary>读取整数类型变量；变量不存在或类型不匹配时抛出可读异常。</summary>
        int GetInteger(string name);

        /// <summary>读取小数类型变量；变量不存在或类型不匹配时抛出可读异常。</summary>
        double GetNumber(string name);

        /// <summary>读取字符串类型变量；变量不存在或类型不匹配时抛出可读异常。</summary>
        string GetString(string name);

        /// <summary>读取字节类型变量，范围为 0 到 255。</summary>
        byte GetByte(string name);

        /// <summary>读取单字符类型变量。</summary>
        char GetCharacter(string name);

        /// <summary>读取日期时间类型变量。</summary>
        DateTime GetDateTime(string name);

        /// <summary>读取 32 位无符号整数类型变量，范围为 0 到 4,294,967,295。</summary>
        uint GetUInt32(string name);

        /// <summary>由设备流程更新布尔类型变量。</summary>
        void SetBoolean(string name, bool value);

        /// <summary>在同一变量表锁内校验并写入两个不同的布尔变量，避免半组结果可见。</summary>
        void SetBooleanPair(string firstName, bool firstValue, string secondName, bool secondValue);

        /// <summary>在同一变量表锁内检查请求和完成值，仅在二者均有效且符合期望时更新完成值；未知值返回 false。</summary>
        bool TryTransitionBooleanPair(string requestName, bool expectedRequest, string completedName, bool expectedCompleted, bool newCompleted);

        /// <summary>由设备流程更新整数类型变量。</summary>
        void SetInteger(string name, int value);

        /// <summary>由设备流程更新小数类型变量。</summary>
        void SetNumber(string name, double value);

        /// <summary>由设备流程更新字符串类型变量。</summary>
        void SetString(string name, string value);

        /// <summary>由设备流程更新字节类型变量，范围为 0 到 255。</summary>
        void SetByte(string name, byte value);

        /// <summary>由设备流程更新单字符类型变量。</summary>
        void SetCharacter(string name, char value);

        /// <summary>由设备流程更新日期时间类型变量。</summary>
        void SetDateTime(string name, DateTime value);

        /// <summary>由设备流程更新 32 位无符号整数类型变量。</summary>
        void SetUInt32(string name, uint value);

        /// <summary>按名称、类型和值替换设备变量定义；相同名称和类型会保留当前运行值。</summary>
        void ReplaceDefinitions(IEnumerable<DeviceVariableDefinition> definitions);

        /// <summary>按类型校验并写入现场人员输入的当前值。</summary>
        bool TrySetOperatorValue(string name, string valueText, out string errorMessage);
    }
}
