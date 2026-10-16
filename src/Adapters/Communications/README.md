# Communications Adapters

这里放可复用的 Modbus、TCP 等连接和基础数据读写实现。

设备项目负责 PLC 点位含义、命令握手和具体设备报文；MES 上传协议由 `Adapters/Mes` 负责。基础通讯实现不引用设备项目，也不包含设备流程。
