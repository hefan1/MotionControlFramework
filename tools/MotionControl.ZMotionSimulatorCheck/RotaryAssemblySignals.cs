using System;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>保存转盘多工位仿真中的工位交接、产品结果和共享轴占用状态。</summary>
    internal sealed class RotaryAssemblySignals
    {
        /// <summary>本次仿真经过三轮工位加工，覆盖良品和不良品分流。</summary>
        public const int CycleCount = 3;

        /// <summary>保护不同工位线程共同读写的流程状态。</summary>
        private readonly object _sync = new object();
        /// <summary>每轮视觉检测结果：第 1、3 轮通过，第 2 轮不通过。</summary>
        private readonly bool[] _plannedInspectionResults = { true, false, true };
        /// <summary>视觉工位实际执行后写入的结果，用于下一轮出料分流。</summary>
        private readonly bool[] _inspectionResults = new bool[CycleCount];
        /// <summary>各工位每轮完成且已退回安全等待位的标记。</summary>
        private readonly bool[] _loadReady = new bool[CycleCount];
        private readonly bool[] _assemblyReady = new bool[CycleCount];
        private readonly bool[] _inspectionReady = new bool[CycleCount];
        private readonly bool[] _unloadReady = new bool[CycleCount];
        private readonly bool[] _feederReady = new bool[CycleCount];
        /// <summary>记录每轮出料走良品还是不良品路径。</summary>
        private readonly bool[] _unloadPassed = new bool[CycleCount];
        /// <summary>当前正在执行的转盘停歇周期。</summary>
        private int _currentCycle;
        /// <summary>当前轮的供料到位与压装取料握手信号。</summary>
        private bool _componentReady;
        /// <summary>当前轮压装工位已经取走供料组件。</summary>
        private bool _componentConsumed;
        /// <summary>所有工位最后一轮完成后置位。</summary>
        private bool _finalCycleCompleted;
        /// <summary>实际执行的转盘分度次数。</summary>
        private int _indexMoveCount;
        /// <summary>当前获得共享转运轴的工位数量。</summary>
        private int _sharedAxisOwners;
        /// <summary>记录当前持有共享转运轴的工位，便于诊断排队者。</summary>
        private string _sharedAxisOwnerStation;
        /// <summary>仿真过程中观察到的最大共享转运轴占用数。</summary>
        private int _maximumSharedAxisOwners;
        /// <summary>通过良品路径完成的出料数量。</summary>
        private int _goodUnloadCount;
        /// <summary>通过不良品路径完成的出料数量。</summary>
        private int _rejectUnloadCount;

        /// <summary>读取当前工位周期。</summary>
        public int CurrentCycle
        {
            get { lock (_sync) return _currentCycle; }
        }

        /// <summary>读取已经完成的转盘分度次数。</summary>
        public int IndexMoveCount
        {
            get { lock (_sync) return _indexMoveCount; }
        }

        /// <summary>读取是否已完成最后一轮全部工位动作。</summary>
        public bool FinalCycleCompleted
        {
            get { lock (_sync) return _finalCycleCompleted; }
        }

        /// <summary>读取当前实际持有共享转运轴的工位数量。</summary>
        public int SharedAxisOwners
        {
            get { lock (_sync) return _sharedAxisOwners; }
        }

        /// <summary>读取共享转运轴当前持有工位的名称。</summary>
        public string SharedAxisOwnerStation
        {
            get { lock (_sync) return _sharedAxisOwnerStation; }
        }

        /// <summary>读取共享转运轴最大同时占用数，用于确认没有并发驱动同一根轴。</summary>
        public int MaximumSharedAxisOwners
        {
            get { lock (_sync) return _maximumSharedAxisOwners; }
        }

        /// <summary>读取已完成的良品出料轮数。</summary>
        public int GoodUnloadCount
        {
            get { lock (_sync) return _goodUnloadCount; }
        }

        /// <summary>读取已完成的不良品出料轮数。</summary>
        public int RejectUnloadCount
        {
            get { lock (_sync) return _rejectUnloadCount; }
        }

        /// <summary>检查当前周期的五个工位是否都已完成并返回安全等待位置。</summary>
        public bool AreAllStationsReady(int cycle)
        {
            lock (_sync)
            {
                return AreAllStationsReadyUnsafe(cycle);
            }
        }

        /// <summary>记录上料工位已完成本轮动作并退回等待位置。</summary>
        public void MarkLoadReady(int cycle)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                _loadReady[cycle] = true;
            }
        }

        /// <summary>记录压装工位已完成本轮动作并退回等待位置。</summary>
        public void MarkAssemblyReady(int cycle)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                _assemblyReady[cycle] = true;
            }
        }

        /// <summary>记录视觉工位已完成本轮检测并退回等待位置。</summary>
        public void MarkInspectionReady(int cycle, bool passed)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                _inspectionResults[cycle] = passed;
                _inspectionReady[cycle] = true;
            }
        }

        /// <summary>记录供料工位已完成本轮供料并退回等待位置。</summary>
        public void MarkFeederReady(int cycle)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                _feederReady[cycle] = true;
            }
        }

        /// <summary>读取本轮视觉结果；结果由仿真工件数据预设，不使用真实视觉或 IO。</summary>
        public bool GetInspectionResult(int cycle)
        {
            EnsureCycleRange(cycle);
            return _plannedInspectionResults[cycle];
        }

        /// <summary>读取本轮出料应采用的结果；第 0 轮使用启动前已在出料位的良品。</summary>
        public bool GetUnloadPassResult(int cycle)
        {
            lock (_sync)
            {
                EnsureCycleRange(cycle);
                if (cycle == 0)
                {
                    return true;
                }

                if (!_inspectionReady[cycle - 1])
                {
                    throw new InvalidOperationException("上一轮视觉检测尚未完成，不能判断当前工件的出料路径。");
                }

                return _inspectionResults[cycle - 1];
            }
        }

        /// <summary>读取指定轮次的出料分流结果，供仿真结束核对。</summary>
        public bool WasUnloadPassed(int cycle)
        {
            lock (_sync)
            {
                EnsureCycleRange(cycle);
                return _unloadReady[cycle] && _unloadPassed[cycle];
            }
        }

        /// <summary>记录良品或不良品已经走完对应出料路径。</summary>
        public void MarkUnloadReady(int cycle, bool passed)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                if (_unloadReady[cycle])
                {
                    return;
                }

                _unloadPassed[cycle] = passed;
                _unloadReady[cycle] = true;
                if (passed)
                {
                    _goodUnloadCount++;
                }
                else
                {
                    _rejectUnloadCount++;
                }
            }
        }

        /// <summary>记录送料轴已经把组件送到压装工位。</summary>
        public void MarkComponentReady(int cycle)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                _componentReady = true;
            }
        }

        /// <summary>查询送料组件是否已到压装工位。</summary>
        public bool IsComponentReady(int cycle)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                return _componentReady;
            }
        }

        /// <summary>记录压装工位已经取走当前轮的供料组件。</summary>
        public void MarkComponentConsumed(int cycle)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                if (!_componentReady)
                {
                    throw new InvalidOperationException("压装工位尚未收到送料到位信号，不能确认取料。");
                }

                _componentConsumed = true;
            }
        }

        /// <summary>查询压装工位是否已经取走当前轮的供料组件。</summary>
        public bool IsComponentConsumed(int cycle)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                return _componentConsumed;
            }
        }

        /// <summary>确认某个工位已获得共享转运轴的运行资源。</summary>
        public void EnterSharedAxis(string stationName)
        {
            lock (_sync)
            {
                _sharedAxisOwners++;
                _sharedAxisOwnerStation = stationName;
                if (_sharedAxisOwners > _maximumSharedAxisOwners)
                {
                    _maximumSharedAxisOwners = _sharedAxisOwners;
                }

                if (_sharedAxisOwners > 1)
                {
                    _sharedAxisOwners--;
                    throw new InvalidOperationException("共享转运轴被两个工位同时占用。");
                }
            }
        }

        /// <summary>确认工位完成动作后归还共享转运轴资源。</summary>
        public void LeaveSharedAxis(string stationName)
        {
            lock (_sync)
            {
                if (_sharedAxisOwners != 1
                    || !string.Equals(_sharedAxisOwnerStation, stationName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("共享转运轴的释放次数与占用记录不匹配。");
                }

                _sharedAxisOwners = 0;
                _sharedAxisOwnerStation = null;
            }
        }

        /// <summary>等待转盘完成分度并进入下一轮；暂停时由取消令牌中断等待。</summary>
        public async Task WaitForNextCycleAsync(int cycle, CancellationToken cancellationToken)
        {
            while (CurrentCycle == cycle)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>所有工位到位后推进周期，并为下一轮清除供料握手状态。</summary>
        public void AdvanceAfterIndex(int cycle)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                if (!AreAllStationsReadyUnsafe(cycle))
                {
                    throw new InvalidOperationException("部分工位未完成或未退回等待位置，禁止转盘分度。");
                }

                if (_currentCycle >= CycleCount - 1)
                {
                    throw new InvalidOperationException("最后一轮完成后不应再次分度。");
                }

                _indexMoveCount++;
                _currentCycle++;
                _componentReady = false;
                _componentConsumed = false;
            }
        }

        /// <summary>确认最后一轮全部工位完成后结束本次验证周期。</summary>
        public void MarkFinalCycleCompleted(int cycle)
        {
            lock (_sync)
            {
                EnsureCurrentCycle(cycle);
                if (cycle != CycleCount - 1 || !AreAllStationsReadyUnsafe(cycle))
                {
                    throw new InvalidOperationException("最后一轮工位尚未全部完成，不能结束验证。");
                }

                _finalCycleCompleted = true;
            }
        }

        /// <summary>检查周期编号是否有效并且五个工位均已完成。</summary>
        private bool AreAllStationsReadyUnsafe(int cycle)
        {
            return cycle >= 0 && cycle < CycleCount
                && _loadReady[cycle]
                && _assemblyReady[cycle]
                && _inspectionReady[cycle]
                && _unloadReady[cycle]
                && _feederReady[cycle];
        }

        /// <summary>检查工位写入状态时仍处于同一轮次。</summary>
        private void EnsureCurrentCycle(int cycle)
        {
            EnsureCycleRange(cycle);
            if (_currentCycle != cycle)
            {
                throw new InvalidOperationException("工位周期已变化，当前周期=" + _currentCycle + "，提交周期=" + cycle + "。");
            }
        }

        /// <summary>检查轮次索引位于 0 到最后一轮之间。</summary>
        private static void EnsureCycleRange(int cycle)
        {
            if (cycle < 0 || cycle >= CycleCount)
            {
                throw new ArgumentOutOfRangeException(nameof(cycle), "转盘工位轮次编号无效。");
            }
        }
    }
}
