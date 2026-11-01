using System;

namespace MotionControl.Equipment.Demo.WeldingExample
{
    /// <summary>四个单件载具位组成的测试流水线；数据仅在当前运行时内有效。</summary>
    internal sealed class WeldingCarrierLine
    {
        /// <summary>保护载具位和测试编号的同一把锁。</summary>
        private readonly object _sync = new object();
        /// <summary>扫码、焊接、压合和复检四个工位当前占用的载具。</summary>
        private readonly string[] _carriers = new string[4];
        /// <summary>本次连接中生成的测试编号，不是实际扫码枪结果。</summary>
        private long _nextNumber;

        /// <summary>扫码工位为空时生成唯一测试产品及载具标识。</summary>
        internal string StartProduct()
        {
            lock (_sync)
            {
                if (_carriers[0] != null) return _carriers[0];
                _nextNumber++;
                _carriers[0] = "测试产品" + _nextNumber.ToString("D4") + "/载具" + _nextNumber.ToString("D4");
                return _carriers[0];
            }
        }

        /// <summary>读取工位当前载具标识；空位返回 null。</summary>
        internal string GetCarrier(int station)
        {
            lock (_sync) return _carriers[station];
        }

        /// <summary>仅当下游空闲时原子转移载具；最后工位完成后移出。</summary>
        internal bool TryTransfer(int station)
        {
            lock (_sync)
            {
                if (_carriers[station] == null) throw new InvalidOperationException("工位没有载具，不能交接。");
                if (station < _carriers.Length - 1)
                {
                    if (_carriers[station + 1] != null) return false;
                    _carriers[station + 1] = _carriers[station];
                }
                _carriers[station] = null;
                return true;
            }
        }
    }
}
