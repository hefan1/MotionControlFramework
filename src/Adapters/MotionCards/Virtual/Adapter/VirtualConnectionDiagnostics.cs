using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace MotionControl.Adapter.MotionCard.Virtual
{
    /// <summary>记录软件控制器的连接故障，文件不可写时仍保留本次进程内的首个原因。</summary>
    internal sealed class VirtualConnectionDiagnostics
    {
        /// <summary>单个日志文件上限约 1 MiB，最多额外保留一份旧文件。</summary>
        private const long MaxLogBytes = 1024 * 1024;
        /// <summary>避免同进程多个控制器实例并发写入时互相打断记录。</summary>
        private static readonly object FileSync = new object();
        /// <summary>保护首次故障快照和最后一次日志错误。</summary>
        private readonly object _sync = new object();
        /// <summary>本实例首个连接故障，避免后续“未连接”掩盖根因。</summary>
        private string _firstFailure;
        /// <summary>最近一次日志写入失败的原因，不替代连接异常。</summary>
        private string _writeFailure;

        /// <summary>创建指定目录的维护记录；不在构造时写文件。</summary>
        public VirtualConnectionDiagnostics(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("连接诊断目录不能为空。", nameof(directory));
            FilePath = Path.Combine(directory, "software-controller-connection.log");
        }

        /// <summary>软件控制器连接异常的本机日志完整路径。</summary>
        public string FilePath { get; }
        /// <summary>最近连接周期内首次故障的时间、动作和原始异常；没有故障时为空。</summary>
        public string FirstFailure { get { lock (_sync) return _firstFailure; } }
        /// <summary>日志写入失败时的原因；不表示连接已恢复。</summary>
        public string WriteFailure { get { lock (_sync) return _writeFailure; } }

        /// <summary>成功取得新会话后清除内存快照，不清除历史日志。</summary>
        public void BeginConnection()
        {
            lock (_sync) { _firstFailure = null; _writeFailure = null; }
        }

        /// <summary>只记录本连接周期首个异常及本地时间；记录失败不得改变设备调用的返回结果。</summary>
        public void Record(string controllerId, int port, string operation, Exception error)
        {
            string text = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
                + " 控制卡=" + controllerId + " 端口=" + port.ToString(CultureInfo.InvariantCulture)
                + " 操作=" + operation + Environment.NewLine + error + Environment.NewLine;
            if (text.Length > 16000) text = text.Substring(0, 16000) + Environment.NewLine + "异常详情已截断。" + Environment.NewLine;
            lock (_sync)
            {
                if (_firstFailure != null) return;
                _firstFailure = text;
            }
            try
            {
                lock (FileSync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var encoding = new UTF8Encoding(false);
                    string record = text + Environment.NewLine;
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length + encoding.GetByteCount(record) > MaxLogBytes)
                    {
                        File.Copy(FilePath, FilePath + ".previous", true);
                        File.WriteAllText(FilePath, record, encoding);
                    }
                    else File.AppendAllText(FilePath, record, encoding);
                }
            }
            catch (Exception writeError)
            {
                lock (_sync) _writeFailure = writeError.ToString();
                Trace.TraceError("软件控制器连接诊断写入失败：" + writeError);
            }
        }
    }
}
