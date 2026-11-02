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
        /// <summary>本会话已经执行生产步骤；再次准备必须明确处理生产状态，不能清空载具绕过。</summary>
        private bool _productionStarted;

        /// <summary>记录生产流程已启动，正常暂停及报警复位不清除此记录。</summary>
        internal void MarkProductionStarted()
        {
            lock (_sync) _productionStarted = true;
        }

        /// <summary>回零前核对本会话没有执行生产且没有载具；不改变产品与工步。</summary>
        internal void EnsureCanPrepare()
        {
            lock (_sync)
            {
                foreach (string carrier in _carriers)
                    if (carrier != null)
                        throw new InvalidOperationException("设备中仍有在制品，请先处理载具；工位回零不会清空生产数据。");
                if (_productionStarted)
                    throw new InvalidOperationException("本次会话已经执行生产，请先确认工件及工步恢复方案；不能重新回零覆盖暂停现场。");
            }
        }

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
