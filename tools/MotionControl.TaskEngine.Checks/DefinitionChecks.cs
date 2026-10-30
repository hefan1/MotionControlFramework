using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>检查任务树、动作参数和类型边界。</summary>
    internal static class DefinitionChecks
    {
        /// <summary>文档样例也经过实际读取和引用校验，防止示例随代码失效。</summary>
        internal static void SampleFile()
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", "TransferCycle.json");
            TaskDefinition definition = TaskDefinitionJson.Deserialize(System.IO.File.ReadAllText(path));
            CheckAssert.NoErrors(new TaskDefinitionValidator().Validate(definition, TaskFixtures.Catalog()));
        }

        /// <summary>八种现有变量类型的常量均能按原类型校验和 JSON 往返。</summary>
        internal static void ScalarTypes()
        {
            var values = new Dictionary<DeviceVariableType, string> {
                { DeviceVariableType.Boolean, "true" }, { DeviceVariableType.Integer, "-2147483648" },
                { DeviceVariableType.Number, "1.25" }, { DeviceVariableType.String, "中文 OK" },
                { DeviceVariableType.Byte, "255" }, { DeviceVariableType.Character, "中" },
                { DeviceVariableType.DateTime, "2026-10-22T10:30:00.0000000+08:00" },
                { DeviceVariableType.UInt32, "4294967295" }
            };
            foreach (var value in values)
            {
                var binding = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = value.Key, Constant = value.Value };
                var wait = new WaitConditionNode {
                    Id = "value", Name = "常量类型检查", Condition = new TaskCondition {
                        Kind = TaskConditionKind.Compare, Comparison = TaskComparison.Equal, Left = binding, Right = binding
                    }, TimeoutAlarmCodes = new List<string> { "OPERATOR_NOTICE" }
                };
                TaskDefinition restored = TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(TaskFixtures.Task(wait)));
                CheckAssert.NoErrors(new TaskDefinitionValidator().Validate(restored, TaskFixtures.Catalog()));
            }
        }

        /// <summary>全部已支持节点经过 JSON 往返后保持含义，仍可完成引用校验。</summary>
        internal static void AllNodesRoundTrip()
        {
            var coordinated = new CoordinatedMoveNode {
                Id = "line", Name = "直线协调定义", Profile = TaskFixtures.Profile(),
                Targets = new List<AxisTarget> {
                    new AxisTarget { AxisName = "X", Target = TaskFixtures.Number("1") },
                    new AxisTarget { AxisName = "Y", Target = TaskFixtures.Number("2") }
                }
            };
            var definition = TaskFixtures.Task(
                TaskFixtures.Move(),
                new ParallelGroupNode {
                    Id = "parallel", Name = "独立机构并行",
                    Branches = new List<TaskNode> {
                        new SequenceNode { Id = "a", Name = "分支甲", Children = new List<TaskNode> { TaskFixtures.Delay("delay-a") } },
                        new SequenceNode { Id = "b", Name = "分支乙", Children = new List<TaskNode> { TaskFixtures.Delay("delay-b") } }
                    }
                },
                new ConditionBranchNode {
                    Id = "if", Name = "判断就绪", Condition = TaskFixtures.Condition(),
                    WhenTrue = new SequenceNode { Id = "yes", Name = "就绪处理" },
                    WhenFalse = new SequenceNode { Id = "no", Name = "未就绪处理" },
                    WhenFalseAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT", "OPERATOR_NOTICE" }
                },
                coordinated,
                new AxisCommandNode { Id = "home", Name = "回零定义", AxisName = "X", Command = TaskAxisCommand.Home },
                new AxisCommandNode { Id = "stop", Name = "停止定义", AxisName = "Y", Command = TaskAxisCommand.Stop, StopMode = AxisStopMode.Controlled },
                new SetOutputNode { Id = "do", Name = "输出定义", OutputName = "Vacuum", Value = true },
                new WaitInputNode {
                    Id = "di", Name = "等待来料", InputName = "PartReady", Expected = true,
                    TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT", "OPERATOR_NOTICE" }
                },
                new WaitConditionNode {
                    Id = "wait", Name = "等待条件", Condition = TaskFixtures.Condition(),
                    TimeoutAlarmCodes = new List<string> { "OPERATOR_NOTICE" }
                },
                new SetVariableNode {
                    Id = "assign", Name = "记录结果", VariableName = "Vision.Result",
                    Value = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.String, Constant = "OK" }
                },
                new ResourceScopeNode {
                    Id = "scope", Name = "拍照区域", Resources = new List<string> { "Axis:X", "Zone:Camera" },
                    Body = new SequenceNode { Id = "body", Name = "区域内步骤" }, ExitCondition = TaskFixtures.Condition()
                },
                new CallTaskNode { Id = "call", Name = "调用子任务", TaskId = "Child" });
            TaskDefinition child = TaskFixtures.Task(TaskFixtures.Delay("child-delay"));
            child.TaskId = "Child";
            string json = TaskDefinitionJson.Serialize(definition);
            TaskDefinition restored = TaskDefinitionJson.Deserialize(json);
            CheckAssert.True(JToken.DeepEquals(JObject.Parse(json), JObject.Parse(TaskDefinitionJson.Serialize(restored))), "JSON 往返改变了定义。");
            var issues = new TaskDefinitionValidator().Validate(restored, TaskFixtures.Catalog(), new[] { child });
            CheckAssert.NoErrors(issues);
            CheckAssert.True(issues.Any(issue => issue.Message.Contains("插补")), "未提示协调运动能力待核实。");
        }

        /// <summary>将旧版单报警字段加载为单项列表，覆盖输入等待、条件等待和条件分支。</summary>
        internal static void LegacyAlarmMigration()
        {
            TaskDefinition original = TaskFixtures.Task(
                new WaitInputNode { Id = "di", Name = "等待来料", InputName = "PartReady", Expected = true },
                new WaitConditionNode { Id = "wait", Name = "等待条件", Condition = TaskFixtures.Condition() },
                new ConditionBranchNode {
                    Id = "if", Name = "判断就绪", Condition = TaskFixtures.Condition(),
                    WhenTrue = new SequenceNode { Id = "yes", Name = "就绪处理" },
                    WhenFalse = new SequenceNode { Id = "no", Name = "未就绪处理" }
                });
            JObject oldJson = JObject.Parse(TaskDefinitionJson.Serialize(original));
            JArray nodes = (JArray)oldJson["root"]["children"];
            nodes[0]["timeoutAlarmCode"] = "DEMO_SENSOR_TIMEOUT";
            ((JObject)nodes[0]).Remove("timeoutAlarmCodes");
            nodes[1]["timeoutAlarmCode"] = "OPERATOR_NOTICE";
            ((JObject)nodes[1]).Remove("timeoutAlarmCodes");
            nodes[2]["whenFalseAlarmCode"] = "DEMO_SENSOR_TIMEOUT";
            ((JObject)nodes[2]).Remove("whenFalseAlarmCodes");

            TaskDefinition migrated = TaskDefinitionJson.Deserialize(oldJson.ToString());
            var migratedNodes = ((SequenceNode)migrated.Root).Children;
            CheckAssert.True(((WaitInputNode)migratedNodes[0]).TimeoutAlarmCodes.SequenceEqual(new[] { "DEMO_SENSOR_TIMEOUT" }),
                "旧 WaitInput 单报警没有迁移到列表。");
            CheckAssert.True(((WaitConditionNode)migratedNodes[1]).TimeoutAlarmCodes.SequenceEqual(new[] { "OPERATOR_NOTICE" }),
                "旧 WaitCondition 单报警没有迁移到列表。");
            CheckAssert.True(((ConditionBranchNode)migratedNodes[2]).WhenFalseAlarmCodes.SequenceEqual(new[] { "DEMO_SENSOR_TIMEOUT" }),
                "旧条件分支单报警没有迁移到列表。");
        }

        /// <summary>等待超时必须关联一条或多条报警；正常条件分支允许不关联报警。</summary>
        internal static void TimeoutAlarmAssociationRequired()
        {
            var inputWait = new WaitInputNode { Id = "di", Name = "等待来料", InputName = "PartReady", Expected = true };
            CheckAssert.HasError(Validate(inputWait), "至少关联一条报警");
            inputWait.TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT", "OPERATOR_NOTICE" };
            CheckAssert.NoErrors(Validate(inputWait));

            var conditionWait = new WaitConditionNode { Id = "wait", Name = "等待条件", Condition = TaskFixtures.Condition() };
            CheckAssert.HasError(Validate(conditionWait), "至少关联一条报警");
            conditionWait.TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" };
            CheckAssert.NoErrors(Validate(conditionWait));

            var branch = new ConditionBranchNode {
                Id = "branch", Name = "正常条件分支", Condition = TaskFixtures.Condition(),
                WhenTrue = new SequenceNode { Id = "yes", Name = "满足" },
                WhenFalse = new SequenceNode { Id = "no", Name = "不满足" }
            };
            CheckAssert.NoErrors(Validate(branch));
        }

        /// <summary>同卡独立轴与示教引用合法，跨轴点位、未示教和相对距离误用必须拒绝。</summary>
        internal static void TeachPoints()
        {
            var move = TaskFixtures.Move();
            move.Target = new TaskValueBinding { Source = TaskValueSource.TeachPoint, AxisName = "X", PointId = "P001" };
            CheckAssert.NoErrors(Validate(move));
            move.Target.AxisName = "Y";
            CheckAssert.HasError(Validate(move), "不属于");
            move.Target.AxisName = "X";
            move.Target.PointId = "P002";
            CheckAssert.HasError(Validate(move), "尚未示教");
            move.Target.PointId = "P001";
            move.Mode = TaskMoveMode.Relative;
            CheckAssert.HasError(Validate(move), "相对距离");
        }

        /// <summary>检查目标、参数、时限、逻辑名称及数值有效性。</summary>
        internal static void MotionParameters()
        {
            var move = TaskFixtures.Move();
            move.Target.Constant = "101";
            CheckAssert.HasError(Validate(move), "超出配置行程");
            foreach (string value in new[] { "NaN", "Infinity", "不是数字" })
            {
                move.Target.Constant = value;
                CheckAssert.HasError(Validate(move), "常量类型或内容无效");
            }
            move.Target = TaskFixtures.Number("10");
            move.Profile.Speed = double.NaN;
            CheckAssert.HasError(Validate(move), "速度必须");
            move.Profile = TaskFixtures.Profile();
            move.AxisName = "Missing";
            CheckAssert.HasError(Validate(move), "逻辑轴不存在");
            move.AxisName = "X";
            move.TimeoutMs = 0;
            CheckAssert.HasError(Validate(move), "时限必须");
        }

        /// <summary>动态变量只校验定义类型，不伪装已检查当前采集值。</summary>
        internal static void Variables()
        {
            var move = TaskFixtures.Move();
            move.Target = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Vision.OffsetX" };
            var issues = Validate(move);
            CheckAssert.NoErrors(issues);
            CheckAssert.True(issues.Any(issue => issue.Severity == TaskIssueSeverity.Warning), "动态变量缺少运行提示。");
            move.Target.VariableName = "Vision.Result";
            CheckAssert.HasError(Validate(move), "轴目标必须是数值");
            move.Target.VariableName = "Unknown";
            CheckAssert.HasError(Validate(move), "设备变量不存在");
            var assignment = new SetVariableNode { Id = "assign", Name = "数量", VariableName = "Product.Count", Value = TaskFixtures.Number("1.5") };
            CheckAssert.HasError(Validate(assignment), "赋值类型不一致");
            assignment.Value = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.UInt32, Constant = "4294967295" };
            CheckAssert.NoErrors(Validate(assignment));
            assignment.Value.Constant = "4294967296";
            CheckAssert.HasError(Validate(assignment), "常量类型或内容无效");
            assignment.Value.Constant = "-1";
            CheckAssert.HasError(Validate(assignment), "常量类型或内容无效");
        }

        /// <summary>IO 方向和延时必须明确，不允许错误名称默认返回成功。</summary>
        internal static void IoAndDelay()
        {
            CheckAssert.HasError(Validate(new SetOutputNode { Id = "do", Name = "错误输出", OutputName = "PartReady" }), "找不到逻辑 DO");
            CheckAssert.HasError(Validate(new WaitInputNode { Id = "di", Name = "错误输入", InputName = "Vacuum" }), "找不到逻辑 DI");
            CheckAssert.HasError(Validate(new DelayNode { Id = "delay", Name = "错误时间", Milliseconds = 101, TimeoutMs = 100 }), "延时须");
            CheckAssert.HasError(Validate(new AxisCommandNode { Id = "stop", Name = "未指定停止方式", AxisName = "X", Command = TaskAxisCommand.Stop }), "停止方式");
        }

        /// <summary>协调定义拒绝重复轴、跨卡及混合角度单位。</summary>
        internal static void CoordinatedAxes()
        {
            var group = new CoordinatedMoveNode {
                Id = "line", Name = "协调", Profile = TaskFixtures.Profile(),
                Targets = new List<AxisTarget> {
                    new AxisTarget { AxisName = "X", Target = TaskFixtures.Number("1") },
                    new AxisTarget { AxisName = "Y", Target = TaskFixtures.Number("2") }
                }
            };
            CheckAssert.NoErrors(Validate(group));
            group.Targets[1].AxisName = "X";
            CheckAssert.HasError(Validate(group), "重复加入");
            group.Targets[1].AxisName = "Z";
            CheckAssert.HasError(Validate(group), "不同控制卡");
            group.Targets[1].AxisName = "R";
            CheckAssert.HasError(Validate(group), "统一 mm");
        }

        /// <summary>比较类型、组合条件和条件循环均需明确报错。</summary>
        internal static void Conditions()
        {
            var wait = new WaitConditionNode {
                Id = "wait", Name = "等待", Condition = TaskFixtures.Condition(),
                TimeoutAlarmCodes = new List<string> { "OPERATOR_NOTICE" }
            };
            CheckAssert.NoErrors(Validate(wait));
            wait.Condition.Comparison = TaskComparison.Greater;
            CheckAssert.HasError(Validate(wait), "大小比较只支持");
            wait.Condition = new TaskCondition { Kind = TaskConditionKind.All };
            CheckAssert.HasError(Validate(wait), "至少一个子条件");
            wait.Condition.Conditions.Add(wait.Condition);
            CheckAssert.HasError(Validate(wait), "循环引用");
            wait.Condition = new TaskCondition { Kind = TaskConditionKind.Not, Conditions = new List<TaskCondition> { TaskFixtures.Condition() } };
            CheckAssert.NoErrors(Validate(wait));
        }

        /// <summary>重复节点、内存循环、错误分支形状和资源引用不能通过检查。</summary>
        internal static void Structure()
        {
            var one = TaskFixtures.Delay("same");
            CheckAssert.HasError(new TaskDefinitionValidator().Validate(TaskFixtures.Task(one, TaskFixtures.Delay("same")), TaskFixtures.Catalog()), "节点 ID");
            CheckAssert.HasError(new TaskDefinitionValidator().Validate(TaskFixtures.Task(one, one), TaskFixtures.Catalog()), "同一节点对象");
            var loop = new SequenceNode { Id = "loop", Name = "循环" };
            loop.Children.Add(loop);
            CheckAssert.HasError(Validate(loop), "循环");
            CheckAssert.HasError(Validate(new ParallelGroupNode { Id = "p", Name = "并行", Branches = new List<TaskNode> { one, TaskFixtures.Delay("two") } }), "Sequence");
            CheckAssert.HasError(Validate(new ResourceScopeNode { Id = "r", Name = "区域", Resources = new List<string> { "Missing" }, Body = new SequenceNode { Id = "b", Name = "正文" } }), "未登记");
        }

        /// <summary>子任务缺失、直接递归和间接递归均有调用节点定位。</summary>
        internal static void TaskCalls()
        {
            var call = new CallTaskNode { Id = "call", Name = "调用", TaskId = "Missing" };
            CheckAssert.HasError(Validate(call), "子任务未加载");
            call.TaskId = "Transfer";
            CheckAssert.HasError(Validate(call), "递归");
            call.TaskId = "Child";
            TaskDefinition child = TaskFixtures.Task(new CallTaskNode { Id = "back", Name = "回调", TaskId = "Transfer" });
            child.TaskId = "Child";
            var issues = new TaskDefinitionValidator().Validate(TaskFixtures.Task(call), TaskFixtures.Catalog(), new[] { child });
            CheckAssert.HasError(issues, "Transfer -> Child -> Transfer");
            CheckAssert.True(issues.Any(issue => issue.NodeId == "back"), "递归错误没有指向调用节点。");
        }

        /// <summary>深树和同时填写多种来源不能静默接受。</summary>
        internal static void DefinitionBounds()
        {
            SequenceNode root = new SequenceNode { Id = "deep0", Name = "深层" };
            SequenceNode current = root;
            for (int index = 1; index < 36; index++)
            {
                var child = new SequenceNode { Id = "deep" + index, Name = "深层" };
                current.Children.Add(child);
                current = child;
            }
            CheckAssert.HasError(Validate(root), "超过 32 层");
            var move = TaskFixtures.Move();
            move.Target.VariableName = "Vision.OffsetX";
            CheckAssert.HasError(Validate(move), "不能同时填写");
        }

        /// <summary>为单个动作执行相同的完整校验入口。</summary>
        private static IReadOnlyList<TaskValidationIssue> Validate(TaskNode node)
        {
            return new TaskDefinitionValidator().Validate(TaskFixtures.Task(node), TaskFixtures.Catalog());
        }
    }
}
