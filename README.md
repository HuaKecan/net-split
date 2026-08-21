# net-split

Windows 11 双网卡透明分流工具。`net-split` 使用独立 Mihomo 进程和 TUN：

- 中国域名/IP 固定从网卡1直连。
- 其他公网流量默认通过机场节点；也可显式切换为住宅 SOCKS5 最终出口，机场节点仅作为前置链路。
- “机场节点 / 住宅 SOCKS5”是独立的最终出口模式；切换普通节点不会再被住宅出口状态覆盖。
- F50 掉线时国内网络保持可用，国外流量阻断，不回退真实出口。
- GUI 使用普通用户权限，网络接管由 LocalSystem Windows 服务完成。

## 当前状态

仓库已包含可构建的 P1/P2/P3 基础实现，P0 真实双网卡验收仍是发布硬门禁：

- `.NET 8` Windows Service、WinForms 托盘、紧急恢复工具。
- 网卡 GUID/MAC 识别、实时接口流量、F50 候选提示。
- 仅 SYSTEM/Administrators 可访问的 ProgramData 数据目录、DPAPI 加密订阅来源、原子设置/缓存写入、脱敏日志。
- 当前安装用户专用的 Named Pipe 身份校验、有界消息、连接超时和并发接收。
- 事务 journal 阶段记录、设置/缓存旧代快照、LKG 与缓存代际绑定；启动恢复前会先校验旧代，初始化未完成时 IPC 只提供诊断读取。
- 升级安装使用受 ACL 保护的启动禁用标记；即使存在未完成事务，服务也只能恢复文件，不能在安装完成前启动 Mihomo/TUN。
- 诊断快照会区分服务初始化中、已就绪和需要恢复，并单独报告 Mihomo TUN 与 DNS 是否已确认启用。
- Clash/Mihomo YAML 节点与 provider 导入。
- 代理页可明确切换机场最终出口或住宅 SOCKS5 最终出口，并区分“最终节点”和“住宅前置节点”。
- Mihomo TUN、Fake-IP、分流 DNS、规则和 `interface-name` 配置生成。
- Mihomo 候选配置离线校验、LKG 原子回滚、Job Object 生命周期和 REST API 状态监控。
- 自定义域名、CIDR、进程直连/代理/阻断规则。

严格系统启动阶段 Kill Switch、IPv6 和通用 base64 节点订阅不在首版范围。

## 安全边界

首次点击“开启分流”前，应用会：

1. 验证两张网卡和订阅。
2. 在受保护目录生成候选配置，不覆盖当前活动配置。
3. 使用 `mihomo -t` 离线校验候选配置。
4. 校验成功后才原子切换并启动 TUN；服务只有同时确认 TUN 和 DNS 后才报告核心就绪，启动失败会恢复上次可用配置。

GUI 只能读取脱敏设置快照，无法取得控制器密钥、DPAPI 密文或修改 Mihomo 可执行路径。LocalSystem 仅从 Program Files 启动 Mihomo；发布脚本会生成并安装 SHA-256 清单。

托盘退出只退出界面，不停止服务。出现异常时运行管理员恢复工具：

```powershell
NetSplit.Recovery.exe
```

恢复工具会请求服务关闭 TUN、停止托管 Mihomo、删除活动/候选运行配置并刷新 DNS，不会删除订阅、LKG 或网卡映射。
如果 PID 文件指向的可执行文件不是锁定的 Mihomo，恢复工具会保留 PID 文件作为证据，不会终止该进程。

## 开发

```powershell
dotnet restore NetSplit.sln
dotnet build NetSplit.sln -c Release
dotnet test NetSplit.sln -c Release
```

生成自包含程序：

```powershell
.\scripts\publish.ps1 -MihomoPath "C:\path\to\official-locked-mihomo.exe"
```

`config/mihomo.lock.json` 锁定发布版本和官方资产/可执行文件 SHA-256；发布脚本拒绝其他二进制。
如果未安装 Clash Verge，请同时传入包含 `geoip.dat` 和 `geosite.dat` 的目录：

```powershell
.\scripts\publish.ps1 `
    -MihomoPath "C:\sources\mihomo.exe" `
    -GeoDataDirectory "C:\sources\geodata"
```

`MihomoPath` 和 `GeoDataDirectory` 必须位于输出目录 `artifacts\win-x64` 之外，因为发布会替换整个输出目录。

管理员安装：

```powershell
.\scripts\install.ps1
```

安装器会先记录升级前的运行状态，再安全关闭分流并写入
`startup.force-disabled` 保护标记。服务启动并通过 RPC 确认 Mihomo、TUN
和 DNS 均关闭后，安装器才移除标记；如果升级前分流处于开启状态，安装器
会在移除标记后恢复接管并验证 Mihomo、TUN 和 DNS。恢复失败时会再次确认
关闭并返回明确错误。安装中断时标记会保留，后续启动仍保持关闭。

开机启动分为两条独立链路：`NetSplitService` 使用延迟自动启动，托盘通过当前用户的登录任务启动并保持普通权限。托盘任务会在登录后等待约 15 秒，再由隐藏的 `start-tray.ps1` 监护启动；监护器确认托盘稳定运行，短暂退出时会在当前任务内重试三次，全部失败后再交给任务计划的失败重试策略。用户从托盘菜单明确退出时不会被监护器重新拉起。托盘默认只显示通知区域图标，不会自动打开主窗口。首次安装保持关闭；升级会恢复升级前已经开启的分流状态，不会把原本关闭的状态自动打开。

只读校验候选配置（不会启动 TUN）：

```powershell
.\scripts\p0-control.ps1 -Action validate
```

检查 Windows 服务、登录任务和当前运行状态（只读，不改变 TUN）：

```powershell
.\scripts\startup-status.ps1
```

采集当前物理出口绑定、路由、DNS、接口流量和 Mihomo 连接证据（只读，
不会启停分流或修改网卡）：

```powershell
.\scripts\p0-observe.ps1 -SampleSeconds 10 -RequireBindingEvidence
```

采样期间同时访问一个国内站点和一个境外站点，可以提高捕获两类物理出口
连接的概率。报告会保存到 `artifacts\p0`；它用于定位和留证，不替代完整
P0 主动验收。

安装器也会把同一脚本复制到
`C:\Program Files\net-split\p0-observe.ps1`，并使用安装目录中的 Mihomo
SHA-256 清单核对核心身份。

诊断页中的“采集 P0 证据”按钮执行同一个只读脚本，完成后会在页面显示
报告摘要和两张物理网卡上观察到的 Mihomo TCP 连接数；环境未就绪或证据
不足时会保留报告并显示警告。采集有 45 秒超时保护，不会启停分流，也不会
修改网卡、路由或 DNS。

只修复服务/托盘的启动注册，不启动或停止服务：

```powershell
.\scripts\repair-startup.ps1
```

只有明确需要立即启动服务时才附加 `-StartService`；如果保存状态为开启，这可能会启动 Mihomo/TUN。

卸载：

```powershell
.\scripts\uninstall.ps1
```

## 首次使用

1. 安装并启动 `NetSplit Service`。
2. 打开托盘界面。
3. 选择网卡1和网卡2。
4. 点击“导入 Clash”或添加 HTTPS Clash/Mihomo YAML 订阅。
5. 检查只读的 Mihomo 和 GeoData 运行环境状态。
6. 点击“校验”；通过后再点击“开启分流”。
7. 按 [P0 验收手册](docs/P0-VALIDATION.md) 检查实际物理出口；未完成 P0 不得发布或设置开机自动启用。

## 目录

- `src/NetSplit.Core`：数据契约、网卡发现、订阅、配置生成和 IPC 客户端。
- `src/NetSplit.Service`：管理员服务、Mihomo 管理、状态机和 IPC 服务端。
- `src/NetSplit.Tray`：普通权限托盘和管理界面。
- `src/NetSplit.Recovery`：独立紧急恢复工具。
- `tests`：核心和服务状态机测试。
- `scripts`：构建、安装、卸载和 P0 校验脚本。
