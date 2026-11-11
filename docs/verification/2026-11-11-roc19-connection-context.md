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

## 证据边界

上述为真实 App / TaskMonitorLauncher 入口的受控离线通知和生命周期故障注入，不是正常按钮操作已观察到的现场故障，也不是正式 Host 屏幕验收。测试复用已有无轴设施，不验证运动 API；运动软件调用另由既有 Virtual 回归记录。正式 Windows Host 的跨设备 A→B→A 验收仍归 ROC-13，本轮当前未执行。
