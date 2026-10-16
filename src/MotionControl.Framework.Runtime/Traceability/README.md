# Runtime Traceability

Runtime 接收设备项目组装的追溯记录，先通过 Storage 适配器保存记录和待上传状态，再异步交给 Host 选定的 MES 适配器。

网络故障或 MES 拒绝时保留记录和最近一次上传结果，应用重启后仍可继续处理。Runtime 不定义设备检测项，也不组装工厂协议报文。
