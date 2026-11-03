using System;
using System.Collections.Generic;
using MotionControl.Framework.Contracts.Equipment;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    /// <summary>双夹位和缓存的会话内状态；所有位置分配都在同一把锁中完成。</summary>
    internal sealed class RotaryCarrierLine
    {
        /// <summary>单件产品只在明确交接完成时改变工艺阶段。</summary>
        internal sealed class Carrier
        {
            /// <summary>扫码设备返回的产品编号。</summary>
            internal string Id;
            /// <summary>当前阶段：Loaded、Welded 或 Pressed。</summary>
            internal string Stage;
            /// <summary>复检返回的 OK 或 NG；未检测时为空。</summary>
            internal string Result;
            /// <summary>产品装入的物理夹位，在转台切换后仍保持不变。</summary>
            internal string Nest;
        }

        /// <summary>保护夹位、缓存和共享机构占用。</summary>
        private readonly object _sync = new object();
        /// <summary>A、B 夹位各自持有的产品。</summary>
        private readonly Carrier[] _nests = new Carrier[2];
        /// <summary>缓存仅允许一件产品，堵塞时不能覆盖。</summary>
        private Carrier _buffer;
        /// <summary>本次控制器会话内已经读取过的产品编号，重复扫码不放行。</summary>
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>各工位最近确认进入的动作，复制到现场证据中。</summary>
        private readonly Dictionary<string, string> _stationActions = new Dictionary<string, string>();
        /// <summary>写入独立设备证据，失败时禁止其他工位继续。</summary>
        private readonly IEquipmentEvidenceStore _evidenceStore;
        /// <summary>独立创建每件产品的完成记录。</summary>
        private readonly IEquipmentRecordStore _recordStore;
        /// <summary>本设备实例的会话标识，与旧会话严格区分。</summary>
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        /// <summary>上一次会话已有生产证据，现场未人工核对前不得运行。</summary>
        private readonly bool _previousProduction;
        /// <summary>现场证据写入失败的原因；失败后不允许继续动作。</summary>
        private string _evidenceError;
        /// <summary>上料中已由扫码设备返回的产品编号。</summary>
        private string _loadingProduct;
        /// <summary>位于焊接侧的夹位索引；另一夹位位于压合侧。</summary>
        private int _weldNest;
        /// <summary>装载、焊接、压合、卸载、转台及缓存各自的占用状态。</summary>
        private bool _loading, _welding, _pressing, _unloading, _rotating, _inspecting;
        /// <summary>会话已经开始过生产，不允许回零覆盖现场。</summary>
        private bool _started;

        /// <summary>绑定当前会话证据存储；读取到旧生产证据时保持禁止，不覆盖旧文件。</summary>
        internal RotaryCarrierLine(IEquipmentEvidenceStore evidenceStore, IEquipmentRecordStore recordStore, RotaryEvidence previous)
        {
            _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
            _recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));
            _previousProduction = previous != null && (previous.ProductionStarted || previous.Loading || previous.Welding
                || previous.Pressing || previous.Unloading || previous.Rotating || previous.Inspecting
                || previous.NestA != null || previous.NestB != null || previous.Buffer != null || !string.IsNullOrEmpty(previous.LoadingProduct));
        }

        /// <summary>拒绝绕过旧会话现场核对或本会话写入错误。</summary>
        private void EnsureAvailable()
        {
            if (_previousProduction)
                throw new InvalidOperationException("存在前次双夹位生产证据，现场与载具状态需要人工核对；禁止自动准备和续跑。证据：Runtime/Evidence/RotaryWelding.json。");
            if (_evidenceError != null)
                throw new InvalidOperationException("现场证据写入失败，禁止继续执行设备动作：" + _evidenceError);
        }

        /// <summary>发出动作前检查证据写入和前次会话状态；跨线程读取时持有同一状态锁。</summary>
        internal void EnsureRunningAllowed() { lock (_sync) EnsureAvailable(); }

        /// <summary>在共享状态锁内保存完整快照；一旦写失败，即使同会话也不能再次运行。</summary>
        private void SaveEvidence()
        {
            try
            {
                var uncertain = new List<string>();
                if (_loading) uncertain.Add("取料或装载尚未确认结束，产品位置及夹具状态需核对");
                if (_welding) uncertain.Add("焊接尚未确认结束，加工结果及轴位置需核对");
                if (_pressing) uncertain.Add("压合尚未确认结束，加工结果及轴位置需核对");
                if (_unloading) uncertain.Add("夹位到缓存的交接尚未收尾，载具及下料轴位置需核对");
                if (_rotating) uncertain.Add("旋转台切换尚未确认结束，夹位对应关系及轴位置需核对");
                if (_inspecting) uncertain.Add("检测或出料尚未确认结束，检测结果和产品去向需核对");
                _evidenceStore.SaveEvidence("RotaryWelding", new RotaryEvidence {
                    SchemaVersion = 1, SessionId = _sessionId, UpdatedUtc = DateTime.UtcNow,
                    ProductionStarted = _started, WeldNest = _weldNest,
                    NestA = ToEvidence(_nests[0]), NestB = ToEvidence(_nests[1]), Buffer = ToEvidence(_buffer),
                    LoadingProduct = _loadingProduct, Loading = _loading, Welding = _welding,
                    Pressing = _pressing, Unloading = _unloading, Rotating = _rotating, Inspecting = _inspecting,
                    UncertainOperations = uncertain.ToArray(), StationActions = new Dictionary<string, string>(_stationActions) });
            }
            catch (Exception error)
            {
                _evidenceError = error.Message;
                throw new InvalidOperationException("保存双夹位现场证据失败，禁止继续动作。", error);
            }
        }

        /// <summary>复制载具字段，使证据不引用后续会被修改的内存对象。</summary>
        private static RotaryProductEvidence ToEvidence(Carrier carrier)
        {
            return carrier == null ? null : new RotaryProductEvidence { Id = carrier.Id, Stage = carrier.Stage, Result = carrier.Result, Nest = carrier.Nest };
        }

        /// <summary>记录工位已进入的动作；若存储失败则不允许后续动作。</summary>
        internal void RecordProgress(string station, string description)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_started) return;
                _stationActions[station] = description;
                SaveEvidence();
            }
        }

        /// <summary>只读核对未进入生产且两个夹位与缓存均为空。</summary>
        internal void EnsureCanPrepare()
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (_started || _nests[0] != null || _nests[1] != null || _buffer != null)
                    throw new InvalidOperationException("当前会话存在生产记录或载具，禁止回零覆盖现场。");
            }
        }

        /// <summary>开始装载时锁定焊接侧夹位与交接区；转台及焊接不能同时使用。</summary>
        internal int? TryStartLoad()
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (_rotating || _loading || _unloading || _welding || _nests[_weldNest] != null) return null;
                _started = true;
                _loading = true;
                SaveEvidence();
                return _weldNest;
            }
        }

        /// <summary>扫码完成后在任何装载动作之前记录来自设备的唯一编号。</summary>
        internal void RegisterScan(string id)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_loading || string.IsNullOrWhiteSpace(id) || !_seen.Add(id))
                    throw new InvalidOperationException("扫码编号为空、重复或当前没有上料操作。");
                _loadingProduct = id;
                SaveEvidence();
            }
        }

        /// <summary>确认装载结束后才登记扫码编号并释放交接区。</summary>
        internal void FinishLoad(int nest, string id)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_loading || nest != _weldNest || _nests[nest] != null || string.IsNullOrWhiteSpace(id) || _loadingProduct != id)
                    throw new InvalidOperationException("装载状态或扫码编号无效、重复，不能交接。");
                _nests[nest] = new Carrier { Id = id, Stage = "Loaded", Nest = nest == 0 ? "A" : "B" };
                _loading = false;
                _loadingProduct = null;
                SaveEvidence();
            }
        }

        /// <summary>焊接侧有待加工产品时保留该夹位，避免装载或旋转并发进入。</summary>
        internal int? TryStartWeld()
        {
            lock (_sync)
            {
                EnsureAvailable();
                Carrier item = _nests[_weldNest];
                if (_rotating || _loading || _welding || item == null || item.Stage != "Loaded") return null;
                _welding = true;
                SaveEvidence();
                return _weldNest;
            }
        }

        /// <summary>焊接反馈完成且轴已让位后标记焊接完成。</summary>
        internal void FinishWeld(int nest)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_welding || nest != _weldNest || _nests[nest] == null || _nests[nest].Stage != "Loaded")
                    throw new InvalidOperationException("焊接载具或工位占用状态不一致。");
                _nests[nest].Stage = "Welded";
                _welding = false;
                SaveEvidence();
            }
        }

        /// <summary>只允许压合已完成焊接的载具。</summary>
        internal int? TryStartPress()
        {
            lock (_sync)
            {
                EnsureAvailable();
                int nest = 1 - _weldNest;
                Carrier item = _nests[nest];
                if (_rotating || _unloading || _pressing || item == null || item.Stage != "Welded") return null;
                _pressing = true;
                SaveEvidence();
                return nest;
            }
        }

        /// <summary>压合反馈合格且轴已让位后标记压合完成。</summary>
        internal void FinishPress(int nest)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_pressing || nest != 1 - _weldNest || _nests[nest] == null || _nests[nest].Stage != "Welded")
                    throw new InvalidOperationException("压合载具或工位占用状态不一致。");
                _nests[nest].Stage = "Pressed";
                _pressing = false;
                SaveEvidence();
            }
        }

        /// <summary>只在缓存空闲时保留压合侧交接区，装载和旋转必须等待。</summary>
        internal int? TryStartUnload()
        {
            lock (_sync)
            {
                EnsureAvailable();
                int nest = 1 - _weldNest;
                Carrier item = _nests[nest];
                if (_rotating || _loading || _unloading || _pressing || _buffer != null || item == null || item.Stage != "Pressed") return null;
                _unloading = true;
                SaveEvidence();
                return nest;
            }
        }

        /// <summary>独立到位反馈确认后立即登记实际交接；卸料机构未退出时仍禁止其他工位使用缓存。</summary>
        internal string ReceiveUnload(int nest)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_unloading || nest != 1 - _weldNest || _buffer != null || _nests[nest] == null || _nests[nest].Stage != "Pressed")
                    throw new InvalidOperationException("下料夹位或缓存状态发生变化，禁止覆盖载具。");
                _buffer = _nests[nest];
                _nests[nest] = null;
                SaveEvidence();
                return _buffer.Id;
            }
        }

        /// <summary>卸料轴退出后才释放交接区域，确保缓存和旋转不能抢先运动。</summary>
        internal void FinishUnload(int nest, string id)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_unloading || nest != 1 - _weldNest || _nests[nest] != null || _buffer == null || _buffer.Id != id)
                    throw new InvalidOperationException("缓存交接记录或下料区占用状态不一致。");
                _unloading = false;
                SaveEvidence();
            }
        }

        /// <summary>焊接已完成且压合侧空闲时保留整个旋转区域；未完成压合不会被送回焊接侧。</summary>
        internal int? TryStartRotation()
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (_rotating || _loading || _unloading || _welding || _pressing || _nests[_weldNest] == null
                    || _nests[_weldNest].Stage != "Welded" || _nests[1 - _weldNest] != null) return null;
                _rotating = true;
                SaveEvidence();
                return 1 - _weldNest;
            }
        }

        /// <summary>旋转到目标并完成安全复核后交换两个夹位。</summary>
        internal void FinishRotation(int nextWeldNest)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_rotating || nextWeldNest != 1 - _weldNest) throw new InvalidOperationException("转台交换状态不一致。");
                _weldNest = nextWeldNest;
                _rotating = false;
                SaveEvidence();
            }
        }

        /// <summary>缓存有载具时保留检测区域，并显示产品编号。</summary>
        internal string TryStartInspection()
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (_inspecting || _unloading || _buffer == null) return null;
                _inspecting = true;
                SaveEvidence();
                return _buffer.Id;
            }
        }

        /// <summary>唯一检测结果锁存到当前缓存载具。</summary>
        internal void SetResult(string id, string result)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_inspecting || _buffer == null || _buffer.Id != id || (result != "OK" && result != "NG"))
                    throw new InvalidOperationException("检测结果与缓存载具不一致。");
                _buffer.Result = result;
                SaveEvidence();
            }
        }

        /// <summary>出料反馈完成、缓存返回后才释放载具和缓存。</summary>
        internal void FinishInspection(string id)
        {
            lock (_sync)
            {
                EnsureAvailable();
                if (!_inspecting || _buffer == null || _buffer.Id != id || _buffer.Result == null)
                    throw new InvalidOperationException("复检产品或结果未知，不能完成下料。");
                try
                {
                    _recordStore.CreateRecord("RotaryWelding-" + _sessionId + "-" + id, new RotaryCompletedProduct {
                        SessionId = _sessionId, Id = id, Nest = _buffer.Nest, Result = _buffer.Result,
                        Station = "复检分拣", Stage = "已完成",
                        Outlet = _buffer.Result == "OK" ? "合格出口" : "不良出口", CompletedUtc = DateTime.UtcNow });
                }
                catch (Exception error)
                {
                    _evidenceError = "逐件记录写入失败：" + error.Message;
                    throw new InvalidOperationException("逐件记录写入失败，禁止清空缓存记录或继续动作。", error);
                }
                _buffer = null;
                _inspecting = false;
                SaveEvidence();
            }
        }

        /// <summary>给工位状态提供当前夹位上的产品信息。</summary>
        internal string DescribeNest(int? nest)
        {
            lock (_sync)
            {
                if (!nest.HasValue) return null;
                Carrier item = _nests[nest.Value];
                return (nest.Value == 0 ? "A夹位" : "B夹位") + (item == null ? "" : " · " + item.Id + " · " + item.Stage);
            }
        }
    }
}
