# UI 交互设计讨论稿 V1

日期：2026-10-24。代码依据：`348f8c3`。

本轮交付设计文档与概念图片，未修改 C#、XAML、设备配置或运行逻辑。设计建议需要逐项讨论后再实施。

## 交付文件

- PDF：`output/pdf/运动控制上位机_UI交互设计讨论稿_V1_20261024.pdf`，共 24 页，带页面书签。
- `方案正文.md`：可搜索和批注的正文。
- `build_document.py`：PDF 排版与正文生成源文件，使用 ReportLab；方法含中文说明。
- `assets/`：四张最终 AI 工作区概念稿。现有外壳及已完成页面基本布局保持不动。
- `image-generation.json`：内置 `image_gen.imagegen` 工具使用的完整提示词、两次针对性修正与最终图片清单。
- `quality-check.json`：PDF 页数、书签、文本边界与目视检查记录。

## 讨论范围

建议新增设备总览、任务配置（工位安排与任务库）、列表任务编辑和当前报警工作区；步骤选择、资源等待与重启恢复作为对话框。已有任务运行页保留工位表、步骤树和分类详情布局。轴映射、轴手动、IO 映射、IO 手动、IO 监控、设备变量、安全联锁和传统流程状态页继续承担原职责。

明确区分传统 Runtime 与独立内存任务会话。目前任务内存运行值尚未接入 Host 的生产变量服务；TaskEngine 跨进程检查点尚未实现。草稿存储、权限、应用流程、恢复对话框与新页面都是设计建议。

## 图像与核对方式

四张 AI 图只绘制新增工作区，不作为运行截图。现有外壳和任务运行布局示意依据当前 XAML 绘制。Windows Computer Use 初始化及重置重试均返回 `failed to write kernel assets: 系统找不到指定的路径。 (os error 3)`，因此没有将软件界面屏幕核对计为完成，也未使用其他自动化绕过。

PDF 已逐页渲染并目视检查 24 页；检查了中文、图片比例、表格、对齐、页脚和分页，文本未超出页面边界。此记录属于文档质量检查，不是上位机功能、真实 IO、仿真器或实物设备验收。

## 参考代码范围

- 当前工程：`src/MotionControl.Host.Wpf`、`src/MotionControl.Framework.Wpf`、`src/MotionControl.TaskEngine`、`src/MotionControl.TaskEngine.Wpf`。
- UAMP：`C:/Users/23887/Desktop/Work/运动控制框架/别的厂商的运动控制框架/版本库_202400613-V1.0.1.7_修改真空检测_恢复版/UAMP`，重点为 `Common/MainForm.cs`、`Common/Pages/SettingPages/TaskSettingPage2.cs`、`Common/Forms/TaskSelectForm.cs`。
- POL：`C:/Users/23887/Desktop/Work/运动控制框架/别的厂商的运动控制框架/Leadshine.CNC.POL_V2.2.05-20210929/Leadshine.CNC.POL.Forms/MainForm.cs`。

文档第 24 页区分参考工程中实际观察到的做法和本框架的建议；没有照搬参考工程的反射页面创建或已注释的权限检查。

## 重新生成

在项目根目录执行以下命令。需要当前机器的微软雅黑字体，以及 Python 的 `reportlab`、`Pillow` 包。

```powershell
& 'C:/Users/23887/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' 'docs/design/ui-interaction-v1/build_document.py'
```

输出 PDF 和 `方案正文.md` 会一同更新。修改版面或内容后应重新逐页渲染核对。
