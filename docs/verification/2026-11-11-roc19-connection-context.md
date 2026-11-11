# ROC-19 连接上下文来源隔离验证

## 修复前证据

生产基线：`be17f5c063604b06a9019dbced09a71b7ce2dea6`。测试检查点：[`0be383e8`](https://github.com/hefan1/MotionControlFramework/commit/0be383e801573815726679d2d0a7618a79b02251)。该检查点没有修改生产源码。

[Windows 回归 42936784179](https://github.com/hefan1/MotionControlFramework/actions/runs/42936784179)，job `130248705002`：Host 和检查程序构建成功，受保护布局检查与 Virtual 控制器 API 检查成功；15 个内部检查项目中 14 个通过，OperatorPages 因新增组失败。已读取完成任务的完整日志，三个预期产品断言全部实际出现：

| 离线场景 | 实际失败标记 | 证明的问题 |
| --- | --- | --- |
| 同一卡 / IO 重复通知 | `ROC19_重复连接重建上下文` | 已发布 Runtime、任务身份或报警历史被重建。 |
| setter 等在途装载结束期间失联 | `ROC19_旧上下文撤回失联` | 旧 setter 撤回后来锁存的失联状态。 |
| A 监控退出晚于 B 连接 | `ROC19_旧断连清空新连接` | 旧断连续体清空后来连接的绑定或历史。 |

失败均来自实际行为断言；没有编译失败、屏障超时或初始化配置错误。现有其他 OperatorPages 组继续执行，新增组的失败不会阻止整套后续检查。检查程序旧汇总文字仍写 69 组，新增组后应为 70 组，本轮随后纠正该计数。

## 后续基线检查

第二个仅测试检查点 [`8bbc1a3`](https://github.com/hefan1/MotionControlFramework/commit/8bbc1a303c04f3b50afa471309677f1ab9836235) 的 [Windows 回归 42938591417](https://github.com/hefan1/MotionControlFramework/actions/runs/42938591417) / job `130254669150` 同样编译、布局与 Virtual API 通过，内部项目 14/15。新增两条产品断言实际出现：`ROC19_旧连接完成覆盖新组合`、`ROC19_旧连接失败清空新组合`。首批三条仍按预期失败。

第二轮三组关窗检查均在进入连接服务断开屏障前超时，连正常关窗组也没有建立场景，因此**不能算关窗问题的 RED**。已定位：真实 `VerifyShutdownOutputsCoreAsync` 要求每张卡声明 `IOutputStatePreservingDisconnect`，原无轴 LoadCard 没有该能力，故退出在输出核验时就被拒绝。检查仅为零轴、零输出的场景包装原 LoadCard 并明确声明该适配语义；生产准入、原 LoadCard、停止期限均未改，没有预造恢复快照或延长等待。

第三个仅测试检查点 [`a1b1d647`](https://github.com/hefan1/MotionControlFramework/commit/a1b1d6470e1cf557eaef0d8ec10f0ba50404f7e7) 的 [Windows 回归 42941042649](https://github.com/hefan1/MotionControlFramework/actions/runs/42941042649) / job `130262656239` 已完成。Host 和全部检查构建、保护布局、Virtual API 通过；内部项目 14/15。正常真实 App 关窗组通过，先前五条仍为产品 RED，另四条实际产品断言失败：

| 离线场景 | 实际失败标记 |
| --- | --- |
| 完整离线同步后旧结果恢复页面 | `ROC19_旧连接晚于断连重挂运行时` |
| 心跳失联且不再发送同步通知 | `ROC19_无同步失联后重挂旧连接` |
| 关窗等待 provider 断卡期间安装新连接 | `ROC19_关窗断卡等待越界` |
| 关窗等待 App 同步期间安装新连接 | `ROC19_关窗同步等待越界` |

此轮没有配置前提超时；检查程序自身的关窗诊断超时清理已按独立审阅修正：保留原异常和对象，真实收尾前不复用；永久阻塞由独立检查进程总限报告未取得结果。

## 当前实现检查点

已取得九条原生产代码的真实产品 RED，正在实施来源与收尾隔离。初版修复采用等待后的来源与代次核对、同源通知幂等、捕获旧 Runtime/history 的收尾，以及实际关窗的等待后复核。尚未宣称修复后的构建、回归或集成通过。

独立静态评审还发现初版补丁在未发布 Runtime 停监控失败时丢失重试对象，并提前关闭其历史。新增回归通过真实 launcher 已安装、App 尚未发布的身份窗口注入取消回调失败，核对再次同步必须等待原监控且保留原历史；标记为 `ROC19_未发布监控失败丢失收尾`。该回归将随初版生产候选执行，不能归入旧生产基线的九条 RED。

初版候选 [`bb0e6dd2`](https://github.com/hefan1/MotionControlFramework/commit/bb0e6dd21672fa71782fa656b6e7e18d1a950b6e) / [run 42942126808](https://github.com/hefan1/MotionControlFramework/actions/runs/42942126808) 在 App 分文件缺少 `MotionControl.Framework.Wpf.Configuration` 命名空间时编译失败（`SafetyInterlockConfigurationDocument` / CS0246），行为检查未执行，不能计作新增产品 RED。仅补引用的 [`2fced14b`](https://github.com/hefan1/MotionControlFramework/commit/2fced14be73c80586698a90af4a09800419846f0) / [run 42942706244](https://github.com/hefan1/MotionControlFramework/actions/runs/42942706244)，job `130268080990` 已完成：Host 与全部检查编译、保护布局和 Virtual API 通过，内部项目 14/15。

此次原来的九条来源问题全部转为 GREEN，正常关窗也通过。唯一产品失败为 `ROC19_未发布监控失败丢失收尾`，日志观察值为“屏障内保留历史=False、重试等待原监控=False、放行后历史线程结束=True”。场景实际建立，监控仍未结束时历史已被关闭，且下一次同步没有等待原监控；这是评审 finding 的真实 RED，不是编译或 fixture 失败。

随后修正保留具体 Runtime、对应 history 与共享原始 StopTask：停止失败不移除；下一次同步在幂等返回前重试，正常关窗异步重试并再次核对当前来源，强制退出只等待原始 Runtime 停止任务而不等待 UI 续体。历史只在原监控确认结束后关闭，退出正常交接的两个许可分支都要求待收尾集合为空。

该候选 [`a0d83fe1`](https://github.com/hefan1/MotionControlFramework/commit/a0d83fe16375cbf0a84e42fb0e5610b97bf07edc) / [run 42943872813](https://github.com/hefan1/MotionControlFramework/actions/runs/42943872813)，job `130271849880` 已取得完整 GREEN：Host、15/15 内部检查项目、70/70 操作页面组、保护布局及 Virtual/多控制器 API 全部通过。监控收尾三个日志观察值全部为 True，原九条来源与正常关窗仍通过。

## 最后评审补充：真实窗口关闭事件

独立完整差异评审接受具体对象保留与退出证据门，但指出新 pending 关窗分支在重试同步完成时，可能尚在原 Closing 事件中再次调用 `Close()`。Microsoft [.NET Framework 4.8 Window.Closing 文档](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.closing?view=netframework-4.8) 明确规定此时会抛 `InvalidOperationException`。

原关窗组直接调用真实 App 处理函数，可以检查来源归属，但不会设置 WPF 的内部关闭状态，不能覆盖这一窗口事件边界。仅测试检查点 [`b0bb763`](https://github.com/hefan1/MotionControlFramework/commit/b0bb763346fd739231d121fff4f06ab4e989f3c8) 加入真实 `window.Close()` → `Closing`，保持生产源码为 a0d83fe。

[run 42945053437](https://github.com/hefan1/MotionControlFramework/actions/runs/42945053437) / job `130275734084` 已确认真实 RED：原始堆栈为 `Window.VerifyNotClosing` → `Window.InternalClose` → `App.MainWindow_Closing` 第 79 行，异常为 WPF 关闭重入的 `InvalidOperationException`；见证为“Closing 次数=1、首次取消=True、调度异常=1、待收尾对象=0”。原监控收尾组仍为 True/True/True。

本轮同时有一条既有 `MachineDevice.Checks` 清料反馈超时，发生于 `ClearanceLeaseChecks.cs:404`，早于 OperatorPages 执行；因此实际内部结果为 13/15，不能称整轮通过。此项目与其 Runtime 源码在本次 Host 修改中未变；单独保留原始堆栈排查，不将其归因于新窗口回归，也不通过盲目延长超时处理。

后续只读诊断找到既有检查对象确定存在的发布竞争：`MoveAbsoluteAsync` 先公布 `Moving=true`，再创建完成任务，观察线程可能提前调用 `Complete` 而丢失信号。该对象的原始源码在 be17f5c 与 b0bb763 相同。最小修正仅让本次目标和完成任务先准备好，再用 volatile 状态发布，并让返回、取消和完成使用捕获的本次任务；生产动作和预算没有调整。此竞争与实际超时相容，但原日志不能证明其为本次失败的唯一原因；由原有 43 组和最终全套检查验证该修正，保留原失败证据。

对已确认的 WPF 问题，最小修正是在遗留监控收尾后增加一次 `await Task.Yield()`，先让原 Closing 返回，然后执行原有完整来源复核，再请求关闭捕获的窗口。该提交时的最终全套候选与实际 master 仍需验证；最终集成证据和完成状态统一登记到 [ROC-19](https://linear.app/roc123/issue/ROC-19) 及关联 PR，本文保留逐个检查点的证据边界。

## 证据边界

上述为真实 App / TaskMonitorLauncher 入口的受控离线通知和生命周期故障注入，不是正常按钮操作已观察到的现场故障，也不是正式 Host 屏幕验收。测试复用已有无轴设施，不验证运动 API；运动软件调用另由既有 Virtual 回归记录。正式 Windows Host 的跨设备 A→B→A 验收仍归 ROC-13，本轮当前未执行。
