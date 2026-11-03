using System;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    internal sealed partial class RotaryStationFlow
    {
        /// <summary>取料、读取扫码器编号并在焊接侧夹位完成装载。</summary>
        private async Task ExecuteLoadAsync(CancellationToken token)
        {
            switch (_step)
            {
                case 0:
                    _nest = _line.TryStartLoad();
                    if (!_nest.HasValue) { await Task.Delay(50, token).ConfigureAwait(false); return; }
                    Next(10, "等待料仓到料"); return;
                case 10:
                    if (await WaitInputAsync("上料到位", true, token).ConfigureAwait(false)) Next(11, "确认取料夹具松开"); return;
                case 11:
                    if (await WaitClampAsync("取料", false, token).ConfigureAwait(false)) Next(12, "取料轴移向料仓"); return;
                case 12:
                    await EnsureSafeAsync(token, "Feeder_Z").ConfigureAwait(false);
                    await MoveAsync("Feeder_X", 1, token).ConfigureAwait(false); Next(13, "取料轴下降"); return;
                case 13:
                    await MoveAsync("Feeder_Z", 1, token).ConfigureAwait(false); Next(14, "夹紧物料"); return;
                case 14:
                    await WriteOutputAsync("取料夹紧", true, token).ConfigureAwait(false); Next(15, "等待取料夹紧到位"); return;
                case 15:
                    if (await WaitClampAsync("取料", true, token).ConfigureAwait(false)) Next(16, "取料轴抬升"); return;
                case 16:
                    await MoveAsync("Feeder_Z", 0, token).ConfigureAwait(false); Next(17, "取料轴移回上料位置"); return;
                case 17:
                    await EnsureSafeAsync(token, "Feeder_Z").ConfigureAwait(false);
                    await MoveAsync("Feeder_X", 0, token).ConfigureAwait(false); Next(18, "确认扫码反馈复位"); return;
                case 18:
                    if (await WaitInputAsync("扫码完成", false, token).ConfigureAwait(false)) Next(19, "触发扫码"); return;
                case 19:
                    await WriteOutputAsync("扫码触发", true, token).ConfigureAwait(false); Next(20, "等待读码器完成"); return;
                case 20:
                    if (await WaitInputAsync("扫码完成", true, token).ConfigureAwait(false)) Next(21, "检查扫码结果和编号"); return;
                case 21:
                    if (!await _io.ReadInputAsync("扫码成功", token).ConfigureAwait(false))
                    {
                        _scannerNeedsRetry = true;
                        throw new InvalidOperationException("扫码设备报告失败，禁止创建产品编号。");
                    }
                    int first = await ReadScannerCodeAsync(token).ConfigureAwait(false);
                    int second = await ReadScannerCodeAsync(token).ConfigureAwait(false);
                    if (first == 0 || first != second || !await _io.ReadInputAsync("扫码完成", token).ConfigureAwait(false)
                        || !await _io.ReadInputAsync("扫码成功", token).ConfigureAwait(false))
                    {
                        _scannerNeedsRetry = true;
                        throw new InvalidOperationException("扫码编号无效或采样期间发生变化，不能放行。");
                    }
                    _product = "SCAN-" + first.ToString("D5"); _line.RegisterScan(_product); Next(22, "复位扫码触发"); return;
                case 22:
                    await WriteOutputAsync("扫码触发", false, token).ConfigureAwait(false); Next(23, "等待扫码完成复位"); return;
                case 23:
                    if (await WaitInputAsync("扫码完成", false, token).ConfigureAwait(false)) Next(24, "检查夹位松开"); return;
                case 24:
                    if (await WaitClampAsync(_nest.Value == 0 ? "A" : "B", false, token).ConfigureAwait(false)) Next(25, "搬运轴对准夹位"); return;
                case 25:
                    await EnsureSafeAsync(token, "Feeder_Z", "Weld_Z").ConfigureAwait(false);
                    await MoveAsync("Transfer_Y", 1, token).ConfigureAwait(false); Next(26, "上料轴推入夹位"); return;
                case 26:
                    await EnsureSafeAsync(token, "Feeder_Z", "Weld_Z").ConfigureAwait(false);
                    await MoveAsync("Load_X", 1, token).ConfigureAwait(false); Next(27, "夹位夹紧物料"); return;
                case 27:
                    await WriteOutputAsync((_nest.Value == 0 ? "A" : "B") + "夹紧", true, token).ConfigureAwait(false);
                    Next(28, "等待夹位夹紧到位"); return;
                case 28:
                    if (await WaitClampAsync(_nest.Value == 0 ? "A" : "B", true, token).ConfigureAwait(false)) Next(29, "松开取料夹具"); return;
                case 29:
                    await WriteOutputAsync("取料夹紧", false, token).ConfigureAwait(false); Next(30, "等待取料夹具松开"); return;
                case 30:
                    if (await WaitClampAsync("取料", false, token).ConfigureAwait(false)) Next(31, "上料轴退出"); return;
                case 31:
                    await MoveAsync("Load_X", 0, token).ConfigureAwait(false); Next(32, "搬运轴退出"); return;
                case 32:
                    await EnsureSafeAsync(token, "Feeder_Z", "Weld_Z").ConfigureAwait(false);
                    await MoveAsync("Transfer_Y", 0, token).ConfigureAwait(false); Next(33, "确认交接并保留载具"); return;
                case 33:
                    _line.FinishLoad(_nest.Value, _product); lock (_sync) _completed++;
                    _nest = null; _product = null; Next(0, "等待空夹位"); return;
                case 80:
                    await WriteOutputAsync("扫码触发", false, token).ConfigureAwait(false); Next(81, "等待旧扫码反馈复位"); return;
                case 81:
                    if (await WaitInputAsync("扫码完成", false, token).ConfigureAwait(false)) Next(19, "重新触发扫码"); return;
                default: throw new InvalidOperationException("未知上料步骤：" + _step);
            }
        }
    }
}
