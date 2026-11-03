using System;
using System.Collections.Generic;
using System.Reflection;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Clearance;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Host.Wpf;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证 Host 只从当前设备读取固定清料配置，不创建或启动动作。</summary>
    internal static class HostClearanceCompositionChecks
    {
        /// <summary>核对无提供器、快照、重复以及设备提供器读取失败。</summary>
        internal static void Run()
        {
            MethodInfo create = typeof(App).GetMethod("CreateClearanceProfiles", BindingFlags.Static | BindingFlags.NonPublic);
            if (create == null) throw new InvalidOperationException("找不到清料配置装配入口。");
            var empty = (IReadOnlyList<ControlledClearanceProfile>)create.Invoke(null, new object[] { new PlainEquipment() });
            if (empty.Count != 0) throw new InvalidOperationException("未声明清料配置的设备错误继承了计划。");

            var device = new DeclaredEquipment();
            ControlledClearanceProfile profile = Profile();
            device.Profiles.Add(profile);
            var snapshot = (IReadOnlyList<ControlledClearanceProfile>)create.Invoke(null, new object[] { device });
            device.Profiles.Clear();
            if (device.Calls != 1 || snapshot.Count != 1 || !ReferenceEquals(snapshot[0], profile))
                throw new InvalidOperationException("清料配置没有从同一设备复制快照。");
            device.Profiles.Add(profile); device.Profiles.Add(profile);
            Reject(create, device);
            device.Profiles.Clear(); device.Profiles.Add(null);
            Reject(create, device);
            device.Profiles.Clear(); device.Fail = true;
            Reject(create, device);
        }

        /// <summary>设备返回无效配置时必须向组合层报告，而不是沿用旧配置。</summary>
        private static void Reject(MethodInfo method, IEquipmentDefinition device)
        {
            try { method.Invoke(null, new object[] { device }); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { return; }
            throw new InvalidOperationException("无效清料配置未被拒绝。");
        }

        /// <summary>构造自洽的最小设备声明，仅用于检查快照。</summary>
        private static ControlledClearanceProfile Profile()
        {
            var inputs = new[] { new ClearanceInputCondition("许可", true) };
            var axes = new[] { new ClearanceAxisCondition("X", 0, 1) };
            var plan = new ControlledClearancePlan(new ControlledClearanceAction[] {
                new ClearanceAbsoluteMove("退回", "X", 0.5, 0.1, new AxisMotionProfile(1, 2, 2),
                    inputs, axes, TimeSpan.FromSeconds(2), inputs) });
            var recovery = new ControlledClearanceRecovery(inputs, axes, new ClearanceRecoveryOutput[0]);
            return new ControlledClearanceProfile("fixture", plan, recovery, new[] { new ClearanceAxisLimit("X", 0, 1, 1, 2, 2) });
        }

        /// <summary>无清料能力的设备。</summary>
        private class PlainEquipment : IEquipmentDefinition
        {
            /// <summary>设备名称。</summary>
            public string EquipmentName { get { return "test"; } }
            /// <summary>测试不创建报警。</summary>
            public IReadOnlyList<MachineAlarmDefinition> CreateDefaultAlarms() { return new MachineAlarmDefinition[0]; }
            /// <summary>测试不创建工位或控制设备。</summary>
            public IReadOnlyList<IStationFlow> CreateStationFlows(IEquipmentContext context) { throw new NotSupportedException(); }
        }

        /// <summary>模拟当前设备的可选清料声明。</summary>
        private sealed class DeclaredEquipment : PlainEquipment, IEquipmentClearanceProvider
        {
            /// <summary>可变来源用于验证 Host 复制。</summary>
            internal readonly List<ControlledClearanceProfile> Profiles = new List<ControlledClearanceProfile>();
            /// <summary>记录提供器是否被正确调用。</summary>
            internal int Calls;
            /// <summary>模拟设备定义读取失败。</summary>
            internal bool Fail;
            /// <summary>提供固定计划或报告设备配置错误。</summary>
            public IEnumerable<ControlledClearanceProfile> CreateClearanceProfiles()
            { Calls++; if (Fail) throw new InvalidOperationException("设备清料定义不可用。"); return Profiles; }
        }
    }
}
