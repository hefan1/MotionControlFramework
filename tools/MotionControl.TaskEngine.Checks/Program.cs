using System;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>定义及纯软件执行检查入口；不连接运动卡，不调用任何设备指令。</summary>
    internal static class Program
    {
        /// <summary>执行定义与内存运行检查，或只读查看指定设备配置能提供的目录数量。</summary>
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--production-demo") return MachineChecks.RunProductionDemo();
            if (args.Length > 0)
            {
                if (args.Length != 3 || args[0] != "--catalog")
                {
                    Console.Error.WriteLine("用法：不带参数运行离线检查；--production-demo 运行三工位内存演示；--catalog 配置根目录 MachineId 只读检查现场配置。");
                    return 2;
                }
                try
                {
                    var catalog = new TaskResourceCatalogLoader(new MachineConfigurationStore(args[1])).Load(args[2]);
                    Console.WriteLine("只读目录：轴=" + catalog.Axes.Count + "，DI=" + catalog.Inputs.Count
                        + "，DO=" + catalog.Outputs.Count + "，变量=" + catalog.Variables.Count + "，示教点=" + catalog.TeachPoints.Count);
                    foreach (string notice in catalog.Notices) Console.WriteLine("提示：" + notice);
                    return 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            }
            int failures = 0;
            Run("说明文档样例定义", DefinitionChecks.SampleFile, ref failures);
            Run("八种变量常量类型", DefinitionChecks.ScalarTypes, ref failures);
            Run("全部节点 JSON 往返和引用", DefinitionChecks.AllNodesRoundTrip, ref failures);
            Run("旧版单报警字段迁移到多报警列表", DefinitionChecks.LegacyAlarmMigration, ref failures);
            Run("示教点归属及相对距离", DefinitionChecks.TeachPoints, ref failures);
            Run("运动参数、范围和时限", DefinitionChecks.MotionParameters, ref failures);
            Run("变量类型及 UInt32 边界", DefinitionChecks.Variables, ref failures);
            Run("IO 方向、停止方式和延时", DefinitionChecks.IoAndDelay, ref failures);
            Run("协调运动的轴组边界", DefinitionChecks.CoordinatedAxes, ref failures);
            Run("条件类型和条件循环", DefinitionChecks.Conditions, ref failures);
            Run("节点结构、重复和资源引用", DefinitionChecks.Structure, ref failures);
            Run("子任务缺失及递归", DefinitionChecks.TaskCalls, ref failures);
            Run("树深度和数据来源互斥", DefinitionChecks.DefinitionBounds, ref failures);
            Run("未知节点、字段和枚举", JsonChecks.UnknownDefinitions, ref failures);
            Run("重复字段和损坏 JSON", JsonChecks.MalformedFiles, ref failures);
            Run("DO 目标状态必须显式填写", JsonChecks.RequiredOutputValue, ref failures);
            Run("任务保存、备份和设备隔离", PersistenceChecks.SaveAndBackup, ref failures);
            Run("保存失败保留原文件", PersistenceChecks.FailedSaveKeepsFile, ref failures);
            Run("版本和路径保护", PersistenceChecks.VersionAndPathProtection, ref failures);
            Run("现有配置及物理示教键兼容", CatalogChecks.ExistingConfiguration, ref failures);
            Run("可选文件缺失与损坏区分", CatalogChecks.MissingAndBrokenFiles, ref failures);
            Run("目录重复和范围检查", CatalogChecks.InvalidCatalog, ref failures);
            Run("无 WPF、Host 或厂商依赖", JsonChecks.AssemblyBoundaries, ref failures);
            Console.WriteLine("阶段 1 离线检查完成：共 23 组，失败 " + failures + " 组。未连接设备。");
            failures += ExecutionChecks.RunAll();
            failures += MachineChecks.RunAll();
            Run("执行后仍无 WPF、Host 或厂商依赖", JsonChecks.AssemblyBoundaries, ref failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>独立运行一组检查并保留完整异常，其他检查仍继续。</summary>
        private static void Run(string name, Action check, ref int failures)
        {
            try { check(); Console.WriteLine("[通过] " + name); }
            catch (Exception error) { failures++; Console.Error.WriteLine("[失败] " + name + "\n" + error); }
        }
    }
}
