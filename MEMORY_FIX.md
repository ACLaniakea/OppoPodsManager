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

## 后续复查：原生 SDP 查询与当前设备选择

原生查询中发现更直接的大内存风险：`WsaQuerySet` 漏掉 `lpVersion`，x64 布局错误地只有 112 字节，后续字段整体错位，正确大小应为 120 字节。尤其 `lpBlob` 的正确偏移是 112；旧结构在这里已经越过分配末尾，原生端可能把相邻内存当作 BLOB 指针与长度。

一次现场采样中，进程提交内存从约 184 MiB 跃升到约 3.94 GiB；托管 GC 提交量只有约 39 MiB，单块原生提交区域约 3.68 GiB。该原生块的头部包含大小为 112 的查询结构、SDP 服务 UUID 及接近整块大小的异常 BLOB 长度。这与错位结构进入原生查询复制路径高度吻合。它是对本次大分配的最强解释；不能仅凭这次内存内容断言历史 49.5 GiB 中每个分配都走相同路径。

同时修复：

- 显式调用 `WSALookupServiceBeginW` / `WSALookupServiceNextW`，匹配 UTF-16 字符串。
- `LUP_FLUSHCACHE` 更正为 `0x2000`；`0x1000` 是 `LUP_FLUSHPREVIOUS`。
- 缓冲区不足处理改为 `WSAEFAULT=10014`，正确识别结束枚举错误码。
- 结果缓冲区最多 1 MiB，限制增长次数和结果轮数；扩容先成功分配再释放旧块，避免分配失败后重复释放。
- 验证返回地址数组与 sockaddr 均位于结果缓冲区内，拒绝异常长度和指针。
- 新增原生结构大小、关键偏移、接口入口与缓冲区边界回归测试。

型号显示问题另有独立原因：软件把附近收到的 AirPods 广播先提升为当前会话，导致真正已连接的 OPPO Enco X3 没有成为当前设备。现在被动 BLE 广播仍可在列表中手动选择，但不再仅凭广播自动接入；自动连接优先处理操作系统已连接的候选。工厂按传输类型筛选，广播地址不写入长期品牌缓存。

Apple 会话现明确拥有并释放底层广播连接，避免退出后连接继续被 hub 事件订阅保留。新增测试覆盖广播先到、OPPO 后到、当前设备移除、保留用户手动选择，以及 1,000 次 Apple 会话连接/断开后的原始连接释放。

参考：[WSAQUERYSETW](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/ns-winsock2-wsaquerysetw)、[WSALookupServiceNextW](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-wsalookupservicenextw)。

## 回归测试

测试项目链接实际生产源码，Windows BLE API 使用隔离替身，不访问蓝牙。另一个集成项目引用实际应用程序集，注入控制层事件验证快照保留和取消，不启动应用或读取用户设置。

```powershell
dotnet run --project .\RegressionTests\DiscoveryRegression.csproj -c Release
dotnet run --project .\RegressionTests\ControlIntegration\ControlIntegration.csproj -c Debug -r win-x64
```

覆盖 15 项发现/缓存/异步 worker/协议向量测试与 11 项控制层/原生接口集成测试，包括：

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
