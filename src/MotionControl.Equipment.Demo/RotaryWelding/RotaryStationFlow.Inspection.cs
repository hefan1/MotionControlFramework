using System;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    internal sealed partial class RotaryStationFlow
    {
        /// <summary>缓存定位、相机检测及按实际 OK/NG 结果送出。</summary>
        private async Task ExecuteInspectionAsync(CancellationToken token)
        {
            switch (_step)
            {
                case 0:
                    _product = _line.TryStartInspection();
                    if (_product == null) { await Task.Delay(50, token).ConfigureAwait(false); return; }
                    _result = null; Next(10, "缓存移至检测位置"); return;
                case 10:
                    await EnsureSafeAsync(token, "Camera_Z", "Reject_Z").ConfigureAwait(false);
                    await MoveAsync("Buffer_Y", 1, token).ConfigureAwait(false); Next(11, "缓存横移至检测位置"); return;
                case 11:
                    await EnsureSafeAsync(token, "Camera_Z", "Reject_Z").ConfigureAwait(false);
                    await MoveAsync("Buffer_X", 1, token).ConfigureAwait(false); Next(12, "相机轴对准检测点"); return;
                case 12:
                    await EnsureSafeAsync(token, "Camera_Z").ConfigureAwait(false);
                    await MoveAsync("Camera_X", 1, token).ConfigureAwait(false); Next(13, "相机下降至检测高度"); return;
                case 13:
                    await MoveAsync("Camera_Z", 1, token).ConfigureAwait(false); Next(14, "确认相机旧结果复位"); return;
                case 14:
                    if (await CameraClearAsync(token).ConfigureAwait(false)) Next(15, "触发检测"); return;
                case 15:
                    await WriteOutputAsync("相机触发", true, token).ConfigureAwait(false); Next(16, "等待相机触发反馈"); return;
                case 16:
                    if (await WaitInputAsync("相机触发反馈", true, token).ConfigureAwait(false)) Next(17, "撤销相机触发"); return;
                case 17:
                    await WriteOutputAsync("相机触发", false, token).ConfigureAwait(false); Next(18, "等待相机触发反馈复位"); return;
                case 18:
                    if (await WaitInputAsync("相机触发反馈", false, token).ConfigureAwait(false)) Next(19, "等待相机检测完成"); return;
                case 19:
                    if (await WaitInputAsync("检测完成", true, token).ConfigureAwait(false)) Next(20, "读取检测结果"); return;
                case 20:
                    bool done = await _io.ReadInputAsync("检测完成", token).ConfigureAwait(false);
                    bool ok = await _io.ReadInputAsync("检测OK", token).ConfigureAwait(false);
                    bool ng = await _io.ReadInputAsync("检测NG", token).ConfigureAwait(false);
                    if (ok && ng) throw new InvalidOperationException("相机 OK 与 NG 同时有效，禁止下料。");
                    if (!await WaitAsync(done && (ok ^ ng), "相机唯一有效检测结果", token).ConfigureAwait(false)) return;
                    bool okAgain = await _io.ReadInputAsync("检测OK", token).ConfigureAwait(false);
                    bool ngAgain = await _io.ReadInputAsync("检测NG", token).ConfigureAwait(false);
                    if (!await _io.ReadInputAsync("检测完成", token).ConfigureAwait(false) || ok != okAgain || ng != ngAgain)
                        throw new InvalidOperationException("相机结果采样不稳定，禁止下料。");
                    _result = ok ? "OK" : "NG"; _line.SetResult(_product, _result); Next(21, "复位相机检测结果"); return;
                case 21:
                    await WriteOutputAsync("相机结果复位", true, token).ConfigureAwait(false); Next(22, "等待相机结果清除"); return;
                case 22:
                    if (await CameraClearAsync(token).ConfigureAwait(false)) Next(23, "撤销相机结果复位"); return;
                case 23:
                    await WriteOutputAsync("相机结果复位", false, token).ConfigureAwait(false); Next(24, "相机抬升让位"); return;
                case 24:
                    await MoveAsync("Camera_Z", 0, token).ConfigureAwait(false); Next(25, "相机横轴返回"); return;
                case 25:
                    await EnsureSafeAsync(token, "Camera_Z").ConfigureAwait(false);
                    await MoveAsync("Camera_X", 0, token).ConfigureAwait(false); Next(26, "等待对应出料口允许"); return;
                case 26:
                    if (await WaitInputAsync(_result == "OK" ? "合格出口允许" : "不良出口允许", true, token).ConfigureAwait(false)) Next(27, "缓存移向出料位置"); return;
                case 27:
                    await EnsureSafeAsync(token, "Camera_Z", "Reject_Z").ConfigureAwait(false);
                    await MoveAsync("Buffer_X", _result == "NG" ? 2 : 1, token).ConfigureAwait(false); Next(28, "缓存送至出料口"); return;
                case 28:
                    await EnsureSafeAsync(token, "Camera_Z", "Reject_Z").ConfigureAwait(false);
                    await MoveAsync("Buffer_Y", 2, token).ConfigureAwait(false); Next(_result == "NG" ? 30 : 50, _result == "NG" ? "剔除轴下降" : "确认合格出料反馈复位"); return;
                case 30:
                    await MoveAsync("Reject_Z", 1, token).ConfigureAwait(false); Next(31, "确认剔除反馈复位"); return;
                case 31:
                    if (await WaitInputAsync("剔除完成", false, token).ConfigureAwait(false)) Next(32, "启动不良品剔除"); return;
                case 32:
                    await WriteOutputAsync("剔除启动", true, token).ConfigureAwait(false); Next(33, "等待剔除完成"); return;
                case 33:
                    if (await WaitInputAsync("剔除完成", true, token).ConfigureAwait(false)) Next(34, "关闭剔除输出"); return;
                case 34:
                    await WriteOutputAsync("剔除启动", false, token).ConfigureAwait(false); Next(35, "等待剔除反馈复位"); return;
                case 35:
                    if (await WaitInputAsync("剔除完成", false, token).ConfigureAwait(false)) Next(36, "剔除轴抬升"); return;
                case 36:
                    await MoveAsync("Reject_Z", 0, token).ConfigureAwait(false); Next(40, "缓存返回等待位置"); return;
                case 50:
                    if (await WaitInputAsync("合格出料完成", false, token).ConfigureAwait(false)) Next(51, "启动合格出料"); return;
                case 51:
                    await WriteOutputAsync("合格出料", true, token).ConfigureAwait(false); Next(52, "等待合格出料完成"); return;
                case 52:
                    if (await WaitInputAsync("合格出料完成", true, token).ConfigureAwait(false)) Next(53, "关闭合格出料"); return;
                case 53:
                    await WriteOutputAsync("合格出料", false, token).ConfigureAwait(false); Next(54, "等待合格出料反馈复位"); return;
                case 54:
                    if (await WaitInputAsync("合格出料完成", false, token).ConfigureAwait(false)) Next(40, "缓存返回等待位置"); return;
                case 80:
                    if (await _io.ReadOutputAsync("相机触发", token).ConfigureAwait(false))
                        throw new InvalidOperationException("相机触发仍有效，不能复位旧结果。");
                    await WriteOutputAsync("相机结果复位", true, token).ConfigureAwait(false); Next(81, "等待旧相机结果清除"); return;
                case 81:
                    if (await CameraClearAsync(token).ConfigureAwait(false)) Next(82, "撤销相机结果复位"); return;
                case 82:
                    await WriteOutputAsync("相机结果复位", false, token).ConfigureAwait(false); Next(14, "重新检测前检查相机状态"); return;
                case 40:
                    await EnsureSafeAsync(token, "Camera_Z", "Reject_Z").ConfigureAwait(false);
                    await MoveAsync("Buffer_Y", 0, token).ConfigureAwait(false); Next(41, "缓存横轴返回"); return;
                case 41:
                    await EnsureSafeAsync(token, "Camera_Z", "Reject_Z").ConfigureAwait(false);
                    await MoveAsync("Buffer_X", 0, token).ConfigureAwait(false); Next(42, "完成本件产品"); return;
                case 42:
                    _line.FinishInspection(_product); lock (_sync) _completed++;
                    _product = null; _result = null; Next(0, "等待缓存载具"); return;
                default: throw new InvalidOperationException("未知复检步骤：" + _step);
            }
        }
    }
}
