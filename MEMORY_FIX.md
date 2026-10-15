# 2.0.1：设备发现内存增长修复

修复设备广播频繁变化时，历史地址缓存、重复发现任务和待处理控制层快照不断累积的问题。保留现有耳机控制协议与操作系统配置。

## 变更

- 只把完整的 Apple proximity pairing 帧（厂商数据头 `07 19`、至少 27 字节）作为 BLE 耳机候选；其他 Apple 广播不再进入耳机发现流程。型号未知时仍可使用基础状态解码。
- BLE 缓存最多 256 个地址，按最后出现时间淘汰，2 分钟未见的条目过期；30 秒周期清理也会通知设备移除。停止或释放中枢时清空缓存，迟到回调不能重新填充。
- 电量广播仍实时传给现有连接。只有设备集合变化时才重建发现列表，每轮只复制一次缓存快照；空名称显示为 AirPods。
- 聚合发现、Windows DeviceWatcher 刷新和控制层设备计划均合并通知：最多一个正在运行的操作和一个最新待处理值。旧快照不再逐项排队；控制层获取信号量后再次检查 generation。
- WinRT 枚举使用取消与超时，共享未完成的枚举；DataReader 和 BluetoothDevice 确定性释放。
- 清理未使用的 Forget 方法、重复 watcher 事件处理器、BLE 连接中多余的委托字段与空异步状态机，以及指向已不存在目录的构建排除项。
- 将华为 MBB 编解码器中从未调用的生产自测方法移到回归项目，保留已知帧与 CRC 向量验证，同时消除该方法的空值警告。
- 程序版本为 2.0.1；关于页面读取程序集版本，因此显示 v2.0.1。

## 回归测试

测试项目链接实际生产源码，Windows BLE API 使用隔离替身，不访问蓝牙。另一个集成项目引用实际应用程序集，注入控制层事件验证快照保留和取消，不启动应用或读取用户设置。

```powershell
dotnet run --project .\RegressionTests\DiscoveryRegression.csproj -c Release
dotnet run --project .\RegressionTests\ControlIntegration\ControlIntegration.csproj -c Debug -r win-x64
```

覆盖 15 项发现/缓存/异步 worker/协议向量测试与 3 项控制层集成测试，包括：

- 10,000 个轮换地址不能超过缓存容量；无需新广播也能过期并通知移除。
- 1,000 次同地址电量广播只触发一次设备集合变化，同时保留全部状态通知。
- 第一轮工作被阻塞时，10,000 次通知合并为当前工作与最后一次更新。
- 控制层阻塞处理时，10,000 个事件快照经 GC 后只剩一个待处理快照。
- 退出取消、并发生产者、异常恢复、旧 generation 拒绝，以及手动发现的排序和去重。

在本次 Windows x64 隔离测试中，第 200 个地址的同线程分配量为 49,296 字节；旧版同类隔离测量为 720,072 字节。这是代码路径的回归指标，不等于真实机器的整进程内存或长时运行结果。

源码缺陷已复现并修复，但历史峰值中托管/原生对象的精确占比，以及所有系统重启的归因，仍需独立证据。回归测试不能替代真实耳机环境中的长期验证。

## Windows 编译

`global.json` 要求 .NET SDK 10.0.302（允许按 latestFeature 规则前滚），运行时不足以编译。

自包含、便于后续内存分析的 Windows x64 版本：

```powershell
dotnet publish .\OppoPodsManager.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:DebuggerSupport=true -p:StripSymbols=false -o .\artifacts\win-x64
```

此产物需要保留整个输出目录，目标机无需另外安装 .NET。NativeAOT 仍沿用项目默认 Release 配置，需要 Windows C++ 编译工具链：

```powershell
dotnet publish .\OppoPodsManager.csproj -c Release -r win-x64 -o .\artifacts\win-x64-aot
```

不要同时运行原版与修复版来比较耳机行为；后续测试应在相同连接条件下分别记录进程提交内存、GC 堆、设备缓存数量和任务数量，覆盖连接、断连、广播变化与恢复场景。此次构建不替换现有安装，也不修改启动项、电源、分页文件或驱动。
