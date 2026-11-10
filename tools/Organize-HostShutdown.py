# -*- coding: utf-8 -*-
"""临时退出文件归位脚本；先验证结构失败，再机械移动已测试方法。"""
from pathlib import Path
import re
import sys
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def checks():
    """增加职责文件检查，原方法仍在启动文件时必须失败。"""
    path = ROOT / 'tools/Check-CommandBoundaries.py'
    text = path.read_text(encoding='utf-8-sig')
    old = "(ROOT / 'src/MotionControl.Host.Wpf/App.xaml.cs').read_text(encoding='utf-8-sig')"
    if text.count(old) != 2:
        raise RuntimeError('App职责检查来源数量变化')
    text = text.replace(old, 'app_lifecycle_source()')
    marker = '\n\nclass CommandBoundaryChecks(unittest.TestCase):'
    helper = '''

def app_lifecycle_source():
    """读取同一个 App 类型的组合与退出文件，不把拆分当作新的运行层次。"""
    text = (ROOT / 'src/MotionControl.Host.Wpf/App.xaml.cs').read_text(encoding='utf-8-sig')
    shutdown = ROOT / 'src/MotionControl.Host.Wpf/App.Shutdown.cs'
    if shutdown.exists():
        text += '\\n' + shutdown.read_text(encoding='utf-8-sig')
    return text
'''
    if text.count(marker) != 1:
        raise RuntimeError('检查类定位失败')
    text = text.replace(marker, helper + marker)
    new_test = '''
    def test_app_exit_has_one_named_source_file(self):
        """退出事件集中到一个可查找文件，启动入口不再混入完整关窗实现。"""
        path = ROOT / 'src/MotionControl.Host.Wpf/App.Shutdown.cs'
        self.assertTrue(path.exists(), 'EXIT_LIFECYCLE_NOT_SEPARATED')
        start = (ROOT / 'src/MotionControl.Host.Wpf/App.xaml.cs').read_text(encoding='utf-8-sig')
        shutdown = path.read_text(encoding='utf-8-sig')
        for name in ['MainWindow_Closing', 'AcquireShutdownProjectGateAsync', 'ReleaseShutdownProjectGate', 'OnExit']:
            pattern = r'\\b(?:void|Task)\\s+' + name + r'\\('
            self.assertNotRegex(start, pattern)
            self.assertRegex(shutdown, pattern)
'''
    marker = "\n\nif __name__ == '__main__':"
    if text.count(marker) != 1:
        raise RuntimeError('检查入口定位失败')
    text = text.replace(marker, '\n' + new_test + marker)
    path.write_text(text, encoding='utf-8')


def move():
    """只移动指定方法及退出字段，逐字保留原方法体。"""
    path = ROOT / 'src/MotionControl.Host.Wpf/App.xaml.cs'
    target = ROOT / 'src/MotionControl.Host.Wpf/App.Shutdown.cs'
    if target.exists():
        raise RuntimeError('退出文件已经存在，不覆盖其他会话修改')
    text = path.read_text(encoding='utf-8-sig')
    original = text
    blocks = []
    for name in ['MainWindow_Closing', 'AcquireShutdownProjectGateAsync', 'ReleaseShutdownProjectGate', 'OnExit']:
        pattern = r'^        (?:private|protected).*?\b' + name + r'\('
        found = list(re.finditer(pattern, text, re.M))
        if len(found) != 1:
            raise RuntimeError('方法定位不唯一：' + name)
        start = text.rfind('        /// <summary>', 0, found[0].start())
        end = text.find('\n        }', found[0].end())
        if start < 0 or end < 0 or text[start:found[0].start()].count('\n') != 1:
            raise RuntimeError('注释或方法边界变化：' + name)
        end += len('\n        }')
        block = text[start:end]
        blocks.append(block)
        text = text[:start] + text[end:]
    fields = []
    for name in ['_exitingApplication', '_shutdownDisconnectInProgress', '_shutdownProjectLease', '_taskEditsCloseConfirmed', '_shutdownRetainedBlockedEvidence']:
        matches = list(re.finditer(r'^        /// <summary>[^\n]*\n        private [^\n]*\b' + re.escape(name) + r';\n', text, re.M))
        if len(matches) != 1:
            raise RuntimeError('字段定位不唯一：' + name)
        fields.append(matches[0].group(0).rstrip('\n'))
        text = text[:matches[0].start()] + text[matches[0].end():]
    text = re.sub(r'\n{3,}', '\n\n', text)
    output = '''using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf
{
    /// <summary>Host 关窗与退出事件：负责界面确认、工程门和结果提示，设备步骤交给退出协调对象。</summary>
    public partial class App
    {
''' + '\n\n'.join(fields + blocks) + '\n    }\n}\n'
    # 每个完整方法块仍原样存在，仅更换物理文件。
    for block in blocks:
        if block not in original or block not in output:
            raise RuntimeError('方法体发生非预期变化')
    target.write_text(output, encoding='utf-8')
    path.write_text(text, encoding='utf-8')
    print('App启动文件行数：', len(original.splitlines()), '->', len(text.splitlines()))
    print('退出文件行数：', len(output.splitlines()))


if __name__ == '__main__':
    if sys.argv[1:] == ['--checks']:
        checks()
    elif sys.argv[1:] == ['--move']:
        move()
        subprocess.run([sys.executable, str(ROOT / 'tools/Check-CommandBoundaries.py')], check=True)
    else:
        raise SystemExit('仅允许 --checks 或 --move')
