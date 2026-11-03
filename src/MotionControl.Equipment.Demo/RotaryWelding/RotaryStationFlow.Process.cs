using System;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    internal sealed partial class RotaryStationFlow
    {
        /// <summary>焊接侧夹位锁定后执行单轴焊接，轴退出后才允许旋转。</summary>
        private async Task ExecuteWeldAsync(CancellationToken token)
        {
            switch (_step)
            {
                case 0:
                    _nest = _line.TryStartWeld();
                    if (!_nest.HasValue) { await Task.Delay(50, token).ConfigureAwait(false); return; }
                    Next(10, "确认夹位夹紧"); return;
                case 10:
                    if (await WaitClampAsync(_nest.Value == 0 ? "A" : "B", true, token).ConfigureAwait(false)) Next(11, "确认焊接完成反馈复位"); return;
                case 11:
                    if (await WaitInputAsync("焊接完成", false, token).ConfigureAwait(false)) Next(20, "确认焊接结果复位"); return;
                case 20:
                    if (await WaitInputAsync("焊接合格", false, token).ConfigureAwait(false)) Next(12, "焊接轴下降"); return;
                case 12:
                    if (!await WaitClampAsync(_nest.Value == 0 ? "A" : "B", true, token).ConfigureAwait(false)) return;
                    await MoveAsync("Weld_Z", 1, token).ConfigureAwait(false); Next(13, "再次确认焊接反馈复位"); return;
                case 13:
                    if (await WaitInputAsync("焊接完成", false, token).ConfigureAwait(false)) Next(23, "再次确认焊接结果复位"); return;
                case 23:
                    if (await WaitInputAsync("焊接合格", false, token).ConfigureAwait(false)) Next(14, "启动焊接"); return;
                case 14:
                    await EnsureAtAsync("Weld_Z", 1, token).ConfigureAwait(false);
                    if (_pendingOutput == "焊接启动")
                    {
                        if (!await WaitClampAsync(_nest.Value == 0 ? "A" : "B", true, token).ConfigureAwait(false)) return;
                        await WriteOutputAsync("焊接启动", true, token).ConfigureAwait(false); Next(15, "等待焊接完成"); return;
                    }
                    if (!await WaitProcessStartAsync(_nest.Value == 0 ? "A" : "B", "焊接完成", "焊接合格", "焊接启动", token).ConfigureAwait(false)) return;
                    await WriteOutputAsync("焊接启动", true, token).ConfigureAwait(false); Next(15, "等待焊接完成"); return;
                case 15:
                    await EnsureAtAsync("Weld_Z", 1, token).ConfigureAwait(false);
                    if (await WaitProcessCompleteAsync(_nest.Value == 0 ? "A" : "B", "焊接完成", token).ConfigureAwait(false)) Next(21, "等待焊接合格结果"); return;
                case 21:
                    await EnsureAtAsync("Weld_Z", 1, token).ConfigureAwait(false);
                    if (await WaitQualifiedAsync("焊接完成", "焊接合格", "焊接启动", _nest.Value == 0 ? "A" : "B", token).ConfigureAwait(false)) Next(16, "关闭焊接启动"); return;
                case 16:
                    await WriteOutputAsync("焊接启动", false, token).ConfigureAwait(false); Next(17, "等待焊接完成复位"); return;
                case 17:
                    if (await WaitInputAsync("焊接完成", false, token).ConfigureAwait(false)) Next(22, "等待焊接结果复位"); return;
                case 22:
                    if (await WaitInputAsync("焊接合格", false, token).ConfigureAwait(false)) Next(18, "焊接轴抬升让位"); return;
                case 18:
                    await MoveAsync("Weld_Z", 0, token).ConfigureAwait(false); Next(19, "确认焊接完成"); return;
                case 19:
                    await EnsureSafeAsync(token, "Weld_Z").ConfigureAwait(false);
                    _line.FinishWeld(_nest.Value); lock (_sync) _completed++;
                    _nest = null; Next(0, "等待待焊载具"); return;
                default: throw new InvalidOperationException("未知焊接步骤：" + _step);
            }
        }

        /// <summary>压合侧只处理已焊接载具，压合合格反馈与完成反馈独立读取。</summary>
        private async Task ExecutePressAsync(CancellationToken token)
        {
            switch (_step)
            {
                case 0:
                    _nest = _line.TryStartPress();
                    if (!_nest.HasValue) { await Task.Delay(50, token).ConfigureAwait(false); return; }
                    Next(10, "确认夹位夹紧"); return;
                case 10:
                    if (await WaitClampAsync(_nest.Value == 0 ? "A" : "B", true, token).ConfigureAwait(false)) Next(11, "确认压合反馈复位"); return;
                case 11:
                    if (await WaitInputAsync("压装完成", false, token).ConfigureAwait(false)) Next(21, "确认压合结果复位"); return;
                case 21:
                    if (await WaitInputAsync("压装合格", false, token).ConfigureAwait(false)) Next(12, "压合轴下降"); return;
                case 12:
                    if (!await WaitClampAsync(_nest.Value == 0 ? "A" : "B", true, token).ConfigureAwait(false)) return;
                    await MoveAsync("Press_Z", 1, token).ConfigureAwait(false); Next(13, "再次确认压合反馈复位"); return;
                case 13:
                    if (await WaitInputAsync("压装完成", false, token).ConfigureAwait(false)) Next(23, "再次确认压合结果复位"); return;
                case 23:
                    if (await WaitInputAsync("压装合格", false, token).ConfigureAwait(false)) Next(14, "启动压合"); return;
                case 14:
                    await EnsureAtAsync("Press_Z", 1, token).ConfigureAwait(false);
                    if (_pendingOutput == "压装启动")
                    {
                        if (!await WaitClampAsync(_nest.Value == 0 ? "A" : "B", true, token).ConfigureAwait(false)) return;
                        await WriteOutputAsync("压装启动", true, token).ConfigureAwait(false); Next(15, "等待压合完成"); return;
                    }
                    if (!await WaitProcessStartAsync(_nest.Value == 0 ? "A" : "B", "压装完成", "压装合格", "压装启动", token).ConfigureAwait(false)) return;
                    await WriteOutputAsync("压装启动", true, token).ConfigureAwait(false); Next(15, "等待压合完成"); return;
                case 15:
                    await EnsureAtAsync("Press_Z", 1, token).ConfigureAwait(false);
                    if (await WaitProcessCompleteAsync(_nest.Value == 0 ? "A" : "B", "压装完成", token).ConfigureAwait(false)) Next(16, "确认压合合格"); return;
                case 16:
                    await EnsureAtAsync("Press_Z", 1, token).ConfigureAwait(false);
                    if (await WaitQualifiedAsync("压装完成", "压装合格", "压装启动", _nest.Value == 0 ? "A" : "B", token).ConfigureAwait(false)) Next(17, "关闭压合启动"); return;
                case 17:
                    await WriteOutputAsync("压装启动", false, token).ConfigureAwait(false); Next(18, "等待压合完成复位"); return;
                case 18:
                    if (await WaitInputAsync("压装完成", false, token).ConfigureAwait(false)) Next(22, "等待压合结果复位"); return;
                case 22:
                    if (await WaitInputAsync("压装合格", false, token).ConfigureAwait(false)) Next(19, "压合轴抬升让位"); return;
                case 19:
                    await MoveAsync("Press_Z", 0, token).ConfigureAwait(false); Next(20, "确认压合完成"); return;
                case 20:
                    await EnsureSafeAsync(token, "Press_Z").ConfigureAwait(false);
                    _line.FinishPress(_nest.Value); lock (_sync) _completed++;
                    _nest = null; Next(0, "等待已焊载具"); return;
                default: throw new InvalidOperationException("未知压合步骤：" + _step);
            }
        }

        /// <summary>先安全交出压合载具，再在各轴让位后交换两个夹位。</summary>
        private async Task ExecuteTransferAsync(CancellationToken token)
        {
            switch (_step)
            {
                case 0:
                    _nest = _line.TryStartUnload();
                    if (_nest.HasValue) { Next(10, "等待缓存允许接料"); return; }
                    _nest = _line.TryStartRotation();
                    if (_nest.HasValue) { Next(30, "确认旋转区域安全条件"); return; }
                    await Task.Delay(50, token).ConfigureAwait(false); return;
                case 10:
                    if (await WaitInputAsync("缓存接收允许", true, token).ConfigureAwait(false)) Next(11, "确认压合轴让位"); return;
                case 11:
                    await EnsureSafeAsync(token, "Press_Z").ConfigureAwait(false); Next(12, "松开压合侧夹位"); return;
                case 12:
                    await WriteOutputAsync((_nest.Value == 0 ? "A" : "B") + "夹紧", false, token).ConfigureAwait(false);
                    Next(13, "等待夹位松开"); return;
                case 13:
                    if (await WaitClampAsync(_nest.Value == 0 ? "A" : "B", false, token).ConfigureAwait(false)) Next(14, "下料轴送出载具"); return;
                case 14:
                    await EnsureSafeAsync(token, "Press_Z").ConfigureAwait(false);
                    await MoveAsync("Unload_X", 1, token).ConfigureAwait(false); Next(17, "确认缓存到位反馈复位"); return;
                case 17:
                    if (await WaitInputAsync("缓存载具到位", false, token).ConfigureAwait(false)) Next(18, "发起缓存交接"); return;
                case 18:
                    await WriteOutputAsync("缓存交接", true, token).ConfigureAwait(false); Next(19, "等待缓存载具到位"); return;
                case 19:
                    if (await WaitInputAsync("缓存载具到位", true, token).ConfigureAwait(false))
                    { _product = _line.ReceiveUnload(_nest.Value); Next(20, "关闭缓存交接，载具已在缓存"); }
                    return;
                case 20:
                    await WriteOutputAsync("缓存交接", false, token).ConfigureAwait(false); Next(21, "等待缓存反馈复位"); return;
                case 21:
                    if (await WaitInputAsync("缓存载具到位", false, token).ConfigureAwait(false)) Next(15, "下料轴返回让位"); return;
                case 15:
                    await MoveAsync("Unload_X", 0, token).ConfigureAwait(false); Next(16, "确认载具进入缓存"); return;
                case 16:
                    await EnsureSafeAsync(token, "Unload_X").ConfigureAwait(false);
                    _line.FinishUnload(_nest.Value, _product); lock (_sync) _completed++;
                    _nest = null; _product = null; Next(0, "等待旋转或下料"); return;
                case 30:
                    await EnsureSafeAsync(token, "Feeder_Z", "Feeder_X", "Load_X", "Transfer_Y", "Weld_Z", "Press_Z", "Unload_X").ConfigureAwait(false);
                    Next(31, "确认待焊夹位松开"); return;
                case 31:
                    if (await WaitClampAsync(_nest.Value == 0 ? "A" : "B", false, token).ConfigureAwait(false)) Next(32, "确认已焊夹位锁紧"); return;
                case 32:
                    if (await WaitClampAsync(_nest.Value == 0 ? "B" : "A", true, token).ConfigureAwait(false)) Next(33, "旋转台切换夹位"); return;
                case 33:
                    await EnsureSafeAsync(token, "Feeder_Z", "Feeder_X", "Load_X", "Transfer_Y", "Weld_Z", "Press_Z", "Unload_X").ConfigureAwait(false);
                    if (!await WaitRotationClampsAsync(_nest.Value, token).ConfigureAwait(false)) return;
                    await MoveAsync("Conveyor_R", _nest.Value == 0 ? -90 : 90, token).ConfigureAwait(false); Next(34, "确认夹位切换完成"); return;
                case 34:
                    _line.FinishRotation(_nest.Value); _nest = null; Next(0, "等待旋转或下料"); return;
                default: throw new InvalidOperationException("未知旋转或下料步骤：" + _step);
            }
        }
    }
}
