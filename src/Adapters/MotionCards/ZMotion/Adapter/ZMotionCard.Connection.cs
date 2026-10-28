using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.VendorApi.ZMotion.Native;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>正运动适配器中负责连接与断开的实现部分。</summary>
    public sealed partial class ZMotionCard
    {
        /// <summary>异步连接本机仿真器并读取控制器身份。</summary>
        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() => Connect(cancellationToken), cancellationToken);
        }

        /// <summary>异步断开仿真器连接并清除当前控制器信息。</summary>
        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() => Disconnect(cancellationToken), cancellationToken);
        }

        /// <summary>通过 ZAux_OpenEth 连接配置地址，并拒绝非 ZDevelop 仿真控制器。</summary>
        private void Connect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_nativeLock)
            {
                if (_handle != IntPtr.Zero)
                {
                    return;
                }

                IntPtr openedHandle = IntPtr.Zero;
                try
                {
                    CheckResult(
                        zmcaux.ZAux_OpenEth(_configuration.Address, out openedHandle),
                        "连接 ZDevelop 控制器",
                        null);
                    if (openedHandle == IntPtr.Zero)
                    {
                        throw new InvalidOperationException(
                            "正运动控制卡 '" + ControllerId + "' 连接成功码返回，但没有取得有效句柄。");
                    }

                    var type = new StringBuilder(256);
                    var version = new StringBuilder(256);
                    var controller = new StringBuilder(256);
                    CheckResult(
                        zmcaux.ZAux_GetControllerInfo(openedHandle, type, version, controller),
                        "读取控制器身份",
                        null);

                    var actualType = type.ToString().Trim();
                    if (!string.Equals(actualType, SimulationControllerType, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "拒绝连接正运动控制器 '" + ControllerId + "'：期望控制器 '"
                            + SimulationControllerType + "'，实际身份为 '" + actualType + "'。"
                            + "当前适配器仅支持本机 VPLC5xx-Simu，不支持实体控制卡。");
                    }

                    ActualControllerType = actualType;
                    ActualControllerVersion = version.ToString().Trim();
                    ActualControllerId = controller.ToString().Trim();
                    _handle = openedHandle;
                    openedHandle = IntPtr.Zero;
                    _axisParametersApplied = false;
                    foreach (var axis in _axesByNumber.Values)
                    {
                        axis.ClearHomedState();
                    }
                }
                catch (DllNotFoundException error)
                {
                    CloseFailedConnection(openedHandle, error);
                    throw new InvalidOperationException(
                        "正运动原生库或其依赖项未找到。请按厂商 SDK 部署匹配当前进程位数的运行库。",
                        error);
                }
                catch (BadImageFormatException error)
                {
                    CloseFailedConnection(openedHandle, error);
                    throw new InvalidOperationException(
                        "正运动原生库位数与上位机进程不匹配，请核对 SDK DLL 与 Host 的 x86/x64 配置。",
                        error);
                }
                catch (Exception error)
                {
                    CloseFailedConnection(openedHandle, error);
                    throw;
                }
            }
        }

        /// <summary>确认所有已映射物理轴停稳后释放句柄，并清除控制器身份和轴回零状态。</summary>
        private void Disconnect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_nativeLock)
            {
                if (_handle == IntPtr.Zero)
                {
                    return;
                }

                var handle = _handle;
                foreach (ZMotionAxis axis in _axesByNumber.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int idle = 1;
                    CheckResult(
                        zmcaux.ZAux_Direct_GetIfIdle(handle, axis.AxisNumber, ref idle),
                        "断开前确认轴已停止",
                        axis.Name);
                    if (idle == 0)
                    {
                        throw new InvalidOperationException(
                            "正运动逻辑轴 '" + axis.Name + "' 仍在运动，必须先停止并确认轴停稳后再断开控制卡。");
                    }
                }

                CheckResult(zmcaux.ZAux_Close(handle), "断开控制器", null);
                _handle = IntPtr.Zero;
                _axisParametersApplied = false;
                ActualControllerType = null;
                ActualControllerVersion = null;
                ActualControllerId = null;
                foreach (var axis in _axesByNumber.Values)
                {
                    axis.ClearHomedState();
                }
            }
        }

        /// <summary>取得当前连接句柄；未连接时抛出带控制卡标识的异常。</summary>
        private IntPtr GetConnectedHandle()
        {
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("正运动控制卡 '" + ControllerId + "' 尚未连接。");
            }

            return _handle;
        }

        /// <summary>连接或身份校验失败时回收已打开句柄，并保留清理异常。</summary>
        private void CloseFailedConnection(IntPtr handle, Exception originalError)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var closeResult = zmcaux.ZAux_Close(handle);
                if (closeResult != 0)
                {
                    throw new ZMotionApiException(ControllerId, "回收失败的连接句柄", null, closeResult);
                }
            }
            catch (Exception closeError)
            {
                throw new AggregateException(
                    "正运动连接或身份核对失败，且回收连接句柄时也发生错误。",
                    originalError,
                    closeError);
            }
        }
    }
}
