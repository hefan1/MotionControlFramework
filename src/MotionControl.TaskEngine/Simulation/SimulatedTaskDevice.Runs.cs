using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Device;

namespace MotionControl.TaskEngine.Simulation
{
    /// <summary>运行入口所有权：整机控制期间禁止另一控制器或独立入口绕过统一暂停。</summary>
    public sealed partial class SimulatedTaskDevice
    {
        /// <summary>当前持有整机控制权的具体对象，锁内读写。</summary>
        private object _machineOwner;
        /// <summary>尚未完全收尾的任务运行编号。</summary>
        private readonly HashSet<string> _activeRuns = new HashSet<string>();

        /// <summary>没有其他控制器或独立运行时才取得本设备控制权。</summary>
        internal void ClaimMachine(object owner)
        {
            lock (_sync)
            {
                if (_machineOwner != null || _activeRuns.Count != 0)
                    throw new InvalidOperationException("内存设备已被其他整机控制器或独立任务使用。");
                _machineOwner = owner;
            }
        }

        /// <summary>关闭全部运行后归还控制权，不清除未确认的区域资源。</summary>
        internal void ReleaseMachine(object owner)
        {
            lock (_sync)
            {
                if (_machineOwner != owner || _activeRuns.Count != 0)
                    throw new InvalidOperationException("设备所有权不匹配或任务尚未收尾。");
                _machineOwner = null;
            }
        }

        /// <summary>登记即将启动的任务；独立入口只允许在没有整机所有者时使用。</summary>
        internal void RegisterRun(string runId, object owner)
        {
            lock (_sync)
            {
                if (_machineOwner != owner) throw new InvalidOperationException("设备受整机控制，不能混用独立任务入口。");
                _activeRuns.Add(runId);
            }
        }

        /// <summary>动作全部退出后移除登记。</summary>
        internal void UnregisterRun(string runId)
        {
            lock (_sync) _activeRuns.Remove(runId);
        }

        /// <summary>设备端口声明：取得内存设备控制权，语义与其他设备实现一致。</summary>
        void ITaskDevice.ClaimMachine(object owner)
        {
            ClaimMachine(owner);
        }

        /// <summary>设备端口声明：归还内存设备控制权。</summary>
        void ITaskDevice.ReleaseMachine(object owner)
        {
            ReleaseMachine(owner);
        }

        /// <summary>设备端口声明：登记即将开始的运行。</summary>
        void ITaskDevice.RegisterRun(string runId, object owner)
        {
            RegisterRun(runId, owner);
        }

        /// <summary>设备端口声明：移除已收尾的运行登记。</summary>
        void ITaskDevice.UnregisterRun(string runId)
        {
            UnregisterRun(runId);
        }
    }
}
