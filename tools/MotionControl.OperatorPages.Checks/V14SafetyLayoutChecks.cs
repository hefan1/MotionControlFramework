using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>安全配置内部离屏可达性预览，不创建窗口或发送设备动作。</summary>
    internal static class V14SafetyLayoutChecks
    {
        /// <summary>覆盖824×600和1200×800中英，逐输入滚动到可见并核对固定保存取消。</summary>
        internal static void Run()
        {
            string output = Path.GetFullPath(Path.Combine(".artifacts", "ui-safety-layout"));
            Directory.CreateDirectory(output);
            foreach (string language in new[] { "zh-CN", "en-US" })
                foreach (int width in new[] { 824, 1200 })
                {
                    int height = width == 824 ? 600 : 800;
                    var view = new SafetyInterlockConfigurationView(new MachineConfigurationStore(Path.Combine(output, "unused-config")), "Machine01");
                    view.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MotionControl.Host.Wpf;component/Localization/Strings." + language + ".xaml", UriKind.Relative) });
                    view.RefreshLocalizedText();
                    Layout(view, width, height);
                    var scroll = (ScrollViewer)view.FindName("SafetyContentScroll");
                    Check(view, "SaveSafetyButton", width, height); Check(view, "CancelSafetyButton", width, height);
                    foreach (string prefix in new[] { "Start", "Pause", "Reset", "EmergencyStop" })
                    {
                        var label = (FrameworkElement)view.FindName(prefix + "InputLabel");
                        var input = (FrameworkElement)view.FindName(prefix + "InputBox");
                        Rect labelBounds = label.TransformToAncestor(view).TransformBounds(new Rect(label.RenderSize));
                        Rect inputBounds = input.TransformToAncestor(view).TransformBounds(new Rect(input.RenderSize));
                        if (labelBounds.Right > inputBounds.Left + 0.5) throw new InvalidOperationException("安全页标签遮挡输入：" + prefix);
                    }
                    Render(view, output, language + "-" + width + "-top", width, height);
                    foreach (string name in new[] { "StartInputBox", "PauseInputBox", "ResetInputBox", "EmergencyStopInputBox" })
                    {
                        var input = (FrameworkElement)view.FindName(name);
                        double target = input.TransformToAncestor((Visual)scroll.Content).Transform(new Point()).Y;
                        scroll.ScrollToVerticalOffset(target);
                        Layout(view, width, height);
                        Check(view, name, width, height);
                        Rect inputBounds = input.TransformToAncestor(scroll).TransformBounds(new Rect(input.RenderSize));
                        if (inputBounds.Top < -1 || inputBounds.Bottom > scroll.ActualHeight + 1) throw new InvalidOperationException("滚动后输入仍不可达：" + name);
                        Check(view, "SaveSafetyButton", width, height); Check(view, "CancelSafetyButton", width, height);
                    }
                    Render(view, output, language + "-" + width + "-bottom", width, height);
                }
            File.WriteAllText(Path.Combine(output, "README.txt"), "Internal offscreen layout only; not Sandbox acceptance. Entry: V14SafetyLayoutChecks.Run(). No window, mouse or device actions.");
        }
        /// <summary>排空延迟布局，不激活任何窗口。</summary>
        private static void Layout(FrameworkElement view, int width, int height)
        {
            view.Width = width; view.Height = height;
            for (int i = 0; i < 3; i++) { view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout(); view.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { })); }
        }
        /// <summary>核对实际控件边界而非仅检查进程或返回码。</summary>
        private static void Check(FrameworkElement view, string name, int width, int height)
        {
            var item = (FrameworkElement)view.FindName(name);
            Rect bounds = item.TransformToAncestor(view).TransformBounds(new Rect(item.RenderSize));
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Left < -1 || bounds.Top < -1 || bounds.Right > width + 1 || bounds.Bottom > height + 1)
                throw new InvalidOperationException("安全页控件被裁切：" + name + " " + bounds);
        }
        /// <summary>输出页面位图，记录顶部与滚到底部时的真实内容布局。</summary>
        private static void Render(FrameworkElement view, string output, string name, int width, int height)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, "safety-" + name + ".png"))) encoder.Save(stream);
        }
    }
}
