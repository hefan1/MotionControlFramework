using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Adapter.MotionCard.Virtual
{
    /// <summary>通过本机 TCP 与独立软件控制器通讯的运动卡适配器。</summary>
    public sealed class VirtualMotionCard : IMotionCard, ILogicalIo, IPhysicalDigitalIoMonitor, IPhysicalDigitalIoBatchMonitor, IMotionCardIoCapabilityProvider
    {
        /// <summary>控制器本机监听端口。</summary>
        private readonly int _port;
        /// <summary>设备中已配置的轴实例。</summary>
        private readonly ReadOnlyCollection<IAxis> _axes;
        /// <summary>逻辑输入对应的物理映射。</summary>
        private readonly Dictionary<string, DigitalIoHardwareDefinition> _inputs;
        /// <summary>逻辑输出对应的物理映射。</summary>
        private readonly Dictionary<string, DigitalIoHardwareDefinition> _outputs;
        /// <summary>连接建立状态，通信失败时清除。</summary>
        private volatile bool _connected;
        /// <summary>控制器颁发的独占租约编号，所有控制命令均携带。</summary>
        private string _leaseId;
        /// <summary>控制当前心跳循环退出。</summary>
        private CancellationTokenSource _heartbeatCancellation;
        /// <summary>串行化连接与断开操作，防止同时取得多个租约。</summary>
        private readonly SemaphoreSlim _connectionGate = new SemaphoreSlim(1, 1);

        /// <summary>创建控制器适配器，轴和 IO 映射使用正式设备配置。</summary>
        public VirtualMotionCard(string controllerId, IEnumerable<AxisHardwareDefinition> axes,
            IEnumerable<DigitalIoHardwareDefinition> inputs, IEnumerable<DigitalIoHardwareDefinition> outputs, int port = 18520)
        {
            if (string.IsNullOrWhiteSpace(controllerId)) throw new ArgumentException("控制卡编号不能为空。", nameof(controllerId));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            ControllerId = controllerId;
            _port = port;
            _axes = (axes ?? throw new ArgumentNullException(nameof(axes))).Select(a => (IAxis)new VirtualAxis(this, a)).ToList().AsReadOnly();
            _inputs = (inputs ?? throw new ArgumentNullException(nameof(inputs))).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            _outputs = (outputs ?? throw new ArgumentNullException(nameof(outputs))).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>设备配置中的控制卡标识。</summary>
        public string ControllerId { get; private set; }
        /// <summary>最近一次成功建立的控制器连接状态。</summary>
        public bool IsConnected { get { return _connected; } }
        /// <summary>根据设备配置创建的逻辑轴列表。</summary>
        public IReadOnlyCollection<IAxis> Axes { get { return _axes; } }

        /// <summary>验证控制器可达后建立连接。</summary>
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_connected) return;
                if (_heartbeatCancellation != null) _heartbeatCancellation.Cancel();
                _leaseId = null;
                var identity = await SendAsync(new Dictionary<string, object> { ["command"] = "ping" }, cancellationToken, false).ConfigureAwait(false);
                if (!identity.TryGetValue("controllerType", out var type)
                    || !string.Equals(type as string, "MotionControl.VirtualController", StringComparison.Ordinal)
                    || !identity.TryGetValue("protocolVersion", out var version)
                    || !(version is int) || (int)version != 2)
                    throw new InvalidOperationException("本机服务不是受支持的软件控制器，或协议版本不兼容，连接已拒绝。");
                var lease = await SendAsync(new Dictionary<string, object> { ["command"] = "acquireLease" }, cancellationToken, false).ConfigureAwait(false);
                if (!lease.TryGetValue("leaseId", out var rawId) || string.IsNullOrWhiteSpace(rawId as string))
                    throw new InvalidOperationException("软件控制器未返回有效租约，连接已拒绝。");
                _leaseId = (string)rawId;
                _connected = true;
                _heartbeatCancellation = new CancellationTokenSource();
                CancellationToken heartbeatToken = _heartbeatCancellation.Token;
                string heartbeatLease = _leaseId;
                _ = Task.Run(() => MaintainLeaseAsync(heartbeatLease, heartbeatToken));
            }
            finally { _connectionGate.Release(); }
        }

        /// <summary>只有全部轴停止后才断开适配器连接。</summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_connected) return;
                foreach (IAxis axis in _axes)
                    if ((await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false)).Moving)
                        throw new InvalidOperationException("轴 " + axis.Name + " 正在运动，不能断开控制器。");
                await SendAsync(new Dictionary<string, object> { ["command"] = "releaseLease" }, cancellationToken).ConfigureAwait(false);
                _connected = false;
                if (_heartbeatCancellation != null) _heartbeatCancellation.Cancel();
                _leaseId = null;
            }
            finally { _connectionGate.Release(); }
        }

        /// <summary>放弃不确定状态的连接并停止续约，由控制器看门狗完成停车；此方法不代表停车已确认。</summary>
        public void AbandonConnection()
        {
            _connected = false;
            if (_heartbeatCancellation != null) _heartbeatCancellation.Cancel();
            _leaseId = null;
        }

        /// <summary>定期续约；通信失败后停止续约，由控制器看门狗独立停车。</summary>
        private async Task MaintainLeaseAsync(string leaseId, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    await SendAsync(new Dictionary<string, object> { ["command"] = "heartbeat", ["leaseId"] = leaseId }, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                if (string.Equals(_leaseId, leaseId, StringComparison.Ordinal)) _connected = false;
            }
        }

        /// <summary>向本机控制器发送一条请求并验证应答。</summary>
        internal async Task<Dictionary<string, object>> SendAsync(Dictionary<string, object> request, CancellationToken cancellationToken, bool requireConnected = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requireConnected && !_connected) throw new InvalidOperationException("软件控制器尚未连接。");
            string leaseId = _leaseId;
            if (requireConnected && !request.ContainsKey("leaseId") && !string.IsNullOrEmpty(leaseId))
                request["leaseId"] = leaseId;
            try
            {
                return await Task.Run(() =>
                {
                    using (var client = new TcpClient())
                    {
                        IAsyncResult connect = client.BeginConnect("127.0.0.1", _port, null, null);
                        using (connect.AsyncWaitHandle)
                        {
                            if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2))) throw new TimeoutException("连接软件控制器超时。");
                            client.EndConnect(connect);
                        }
                        client.ReceiveTimeout = 3000;
                        client.SendTimeout = 3000;
                        using (NetworkStream stream = client.GetStream())
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            var serializer = new JavaScriptSerializer();
                            writer.WriteLine(serializer.Serialize(request));
                            string line = reader.ReadLine();
                            if (line == null) throw new IOException("软件控制器已断开连接。");
                            var response = serializer.Deserialize<Dictionary<string, object>>(line);
                            if (!response.ContainsKey("ok") || !Convert.ToBoolean(response["ok"]))
                                throw new InvalidOperationException("软件控制器拒绝 " + request["command"] + "：" + (response.ContainsKey("error") ? response["error"] : "未知错误"));
                            return response;
                        }
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is TimeoutException)
            {
                if (requireConnected && string.Equals(_leaseId, leaseId, StringComparison.Ordinal)) _connected = false;
                throw new IOException("软件控制器通信失败，端口 " + _port + "。", ex);
            }
        }

        /// <summary>发送带物理通道号的输入输出命令。</summary>
        private async Task<bool> ReadChannelAsync(string command, int channel, CancellationToken token)
        {
            var result = await SendAsync(new Dictionary<string, object> { ["command"] = command, ["channel"] = channel }, token).ConfigureAwait(false);
            return Convert.ToBoolean(result["value"]);
        }
        /// <summary>读取物理输入电平。</summary>
        public Task<bool> ReadDigitalInputChannelAsync(int channel, CancellationToken cancellationToken) { return ReadChannelAsync("readInput", channel, cancellationToken); }
        /// <summary>读取物理输出电平。</summary>
        public Task<bool> ReadDigitalOutputChannelAsync(int channel, CancellationToken cancellationToken) { return ReadChannelAsync("readOutput", channel, cancellationToken); }
        /// <summary>设置物理输出电平。</summary>
        public async Task WriteDigitalOutputChannelAsync(int channel, bool physicalHigh, CancellationToken cancellationToken)
        {
            await SendAsync(new Dictionary<string, object> { ["command"] = "writeOutput", ["channel"] = channel, ["value"] = physicalHigh }, cancellationToken).ConfigureAwait(false);
        }
        /// <summary>批量读取物理通道，保持请求顺序与响应值对应。</summary>
        private async Task<IReadOnlyDictionary<int, bool>> ReadChannelsAsync(string command, IReadOnlyCollection<int> channels, CancellationToken token)
        {
            int[] numbers = channels.ToArray();
            var response = await SendAsync(new Dictionary<string, object> { ["command"] = command, ["channels"] = numbers }, token).ConfigureAwait(false);
            var values = (IList)response["values"];
            if (values.Count != numbers.Length) throw new IOException("软件控制器返回的 IO 数量不匹配。");
            var result = new Dictionary<int, bool>();
            for (int i = 0; i < numbers.Length; i++) result[numbers[i]] = Convert.ToBoolean(values[i]);
            return result;
        }
        /// <summary>批量读取物理输入。</summary>
        public Task<IReadOnlyDictionary<int, bool>> ReadDigitalInputChannelsAsync(IReadOnlyCollection<int> channels, CancellationToken cancellationToken) { return ReadChannelsAsync("readInputs", channels, cancellationToken); }
        /// <summary>批量读取物理输出。</summary>
        public Task<IReadOnlyDictionary<int, bool>> ReadDigitalOutputChannelsAsync(IReadOnlyCollection<int> channels, CancellationToken cancellationToken) { return ReadChannelsAsync("readOutputs", channels, cancellationToken); }
        /// <summary>按逻辑名称获取已配置 IO，缺少映射时明确报错。</summary>
        private static DigitalIoHardwareDefinition Find(Dictionary<string, DigitalIoHardwareDefinition> points, string name)
        {
            if (name == null || !points.TryGetValue(name, out var point)) throw new InvalidOperationException("未配置逻辑 IO：" + name);
            return point;
        }
        /// <summary>读取逻辑输入并按映射极性转换。</summary>
        public async Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken)
        {
            var point = Find(_inputs, inputName);
            return (await ReadDigitalInputChannelAsync(point.Channel, cancellationToken).ConfigureAwait(false)) == point.ActiveHigh;
        }
        /// <summary>读取逻辑输出并按映射极性转换。</summary>
        public async Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
        {
            var point = Find(_outputs, outputName);
            return (await ReadDigitalOutputChannelAsync(point.Channel, cancellationToken).ConfigureAwait(false)) == point.ActiveHigh;
        }
        /// <summary>将逻辑输出转换为物理电平并写入控制器。</summary>
        public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
        {
            var point = Find(_outputs, outputName);
            return WriteDigitalOutputChannelAsync(point.Channel, value == point.ActiveHigh, cancellationToken);
        }
        /// <summary>返回软件控制器当前支持的通道范围。</summary>
        public Task<MotionCardIoCapabilities> ReadIoCapabilitiesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_connected) throw new InvalidOperationException("软件控制器尚未连接。");
            return Task.FromResult(new MotionCardIoCapabilities {
                ControllerId = ControllerId,
                DigitalInputChannelRange = new MotionCardIoChannelRange { MappableChannelCount = 4096 },
                DigitalOutputChannelRange = new MotionCardIoChannelRange { MappableChannelCount = 4096 }
            });
        }
        /// <summary>重新获取软件控制器的固定 IO 范围。</summary>
        public Task<MotionCardIoCapabilities> RefreshIoCapabilitiesAsync(CancellationToken cancellationToken) { return ReadIoCapabilitiesAsync(cancellationToken); }
    }
}
