using System;
using System.IO;
using System.Text;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Host.Wpf.Recovery
{
    /// <summary>保存报警会话是否完成安全交接；未知记录不能由普通复位清除。</summary>
    public sealed class AlarmContinuityStore
    {
        /// <summary>保护本进程中的状态和文件切换。</summary>
        private readonly object _sync = new object();
        /// <summary>安全结束的记录路径；不存在也不能推断为安全。</summary>
        private readonly string _cleanPath;
        /// <summary>打开会话前刷新到磁盘的待交接记录路径。</summary>
        private readonly string _pendingPath;
        /// <summary>设备身份与文件版本，用于拒绝错误设备的记录。</summary>
        private readonly string _cleanContent;
        /// <summary>本会话独有的待交接内容，不允许替其他会话收尾。</summary>
        private readonly string _pendingContent;
        /// <summary>只有本实例成功建立的待交接记录才可以尝试收尾。</summary>
        private bool _ownsPending;
        /// <summary>防止重复打开同一实例。</summary>
        private bool _opened;
        /// <summary>受锁保护的跨连接报警核查原因。</summary>
        private string _blockReason;
        /// <summary>需要设备维护人员核查的原因；非空时不能普通复位或自动运行。</summary>
        public string BlockReason { get { lock (_sync) return _blockReason; } private set { _blockReason = value; } }

        /// <summary>绑定单机配置目录，不读取或覆盖其他设备的记录。</summary>
        public AlarmContinuityStore(MachineConfigurationStore configuration, string machineId)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            string directory = Path.Combine(configuration.GetMachineDirectory(machineId), "Runtime");
            _cleanPath = Path.Combine(directory, "AlarmContinuity.clean");
            _pendingPath = Path.Combine(directory, "AlarmContinuity.pending");
            _cleanContent = "MC-ALARM-CONTINUITY|1|" + machineId;
            _pendingContent = _cleanContent + "|" + Guid.NewGuid().ToString("N");
        }

        /// <summary>建立动作前的持久待交接记录；首次部署、旧记录或任何读写故障均保持未知。</summary>
        public void Open()
        {
            lock (_sync)
            {
                if (_opened) return;
                _opened = true;
                try
                {
                    if (Exists(_pendingPath))
                    {
                        BlockReason = "上次报警会话尚未确认安全交接，必须由设备项目核查旧报警来源及现场。";
                        return;
                    }
                    if (!Exists(_cleanPath))
                    {
                        BlockReason = "缺少可信的报警交接记录；首次部署或旧版迁移必须先按设备规程核查。";
                        return;
                    }
                    if (!string.Equals(File.ReadAllText(_cleanPath, Encoding.UTF8), _cleanContent, StringComparison.Ordinal))
                    {
                        BlockReason = "报警交接记录损坏或设备身份不匹配，不能推断旧报警已恢复。";
                        return;
                    }
                    WriteNew(_pendingPath, _pendingContent);
                    _ownsPending = true;
                    File.Delete(_cleanPath);
                }
                catch (Exception error)
                {
                    BlockReason = "报警交接记录无法可靠读写，不能确认旧报警状态：" + error.Message;
                }
            }
        }

        /// <summary>旧来源失联或报警未复位时保留本会话的未知门禁及原始待交接文件。</summary>
        public void MarkUnconfirmed(string reason)
        {
            lock (_sync)
            {
                if (BlockReason == null)
                    BlockReason = "跨连接的停机报警或收尾状态未确认：" + reason;
            }
        }

        /// <summary>仅调用方已核实旧来源和设备停止均安全收尾，且本会话没有未知情况时标记结束。</summary>
        public void CloseIfSettled(bool settled)
        {
            lock (_sync)
            {
                if (!settled || !_ownsPending || BlockReason != null) return;
                try
                {
                    if (!Exists(_pendingPath) || File.ReadAllText(_pendingPath, Encoding.UTF8) != _pendingContent || Exists(_cleanPath))
                        throw new InvalidOperationException("待交接记录已变化，不能替其他会话收尾。");
                    WriteNew(_cleanPath, _cleanContent);
                    File.Delete(_pendingPath);
                    _ownsPending = false;
                }
                catch (Exception error)
                {
                    BlockReason = "报警交接结束记录无法可靠保存：" + error.Message;
                }
            }
        }

        /// <summary>只将明确不存在当成无文件；权限和其他读取错误继续上报。</summary>
        private static bool Exists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>独占创建并刷新文件，避免覆盖旧证据或以未完成写入放行动作。</summary>
        private static void WriteNew(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
    }
}
