# P0 双网卡验收

P0 分为离线校验和主动网络测试。离线校验不会启动 TUN；主动测试会改变本机网络路径，开始前应关闭重要下载、远程桌面和未保存的在线工作。

## 1. 离线配置校验

普通 `dotnet test` 会把本项明确报告为 skipped。只有设置 `NETSPLIT_RUN_P0=1` 及对应环境变量后，才会调用本机 Mihomo 做外部校验；自动化单元测试通过不等于 P0 通过。

确认 Clash Verge 当前配置中存在可用订阅，然后运行：

```powershell
.\scripts\p0-validate.ps1 -DirectAdapterName "以太网" -ProxyAdapterName "以太网 3"
```

脚本会：

- 验证项目构建和自动化测试。
- 检查两张接口均有 IPv4 和默认网关。
- 使用当前 Clash Verge 订阅生成 net-split 配置。
- 使用已安装的 `verge-mihomo.exe -t` 校验配置。
- 不启动 Mihomo，不创建 TUN，不修改 route metric。
- 进入主动验收时，脚本只有在状态快照同时确认 `tunEnabled`、`dnsEnabled` 和 `dnsStatusKnown` 后才会继续。

## 2. 启用前快照

以管理员 PowerShell 记录：

```powershell
Get-NetAdapter | Format-Table Name, Status, ifIndex, InterfaceDescription
Get-NetRoute -AddressFamily IPv4 | Where-Object DestinationPrefix -eq "0.0.0.0/0"
Get-DnsClientServerAddress -AddressFamily IPv4
```

确认 `NetSplit.Recovery.exe` 可以运行。

分流已经开启时，可以先运行只读观察脚本，不改变当前网络状态：

```powershell
.\scripts\p0-observe.ps1 -SampleSeconds 10 -RequireBindingEvidence
```

脚本记录服务诊断、启动注册、物理网卡、TUN、IPv4 路由、DNS、Mihomo
进程身份及锁定 SHA-256、TCP/UDP 本地绑定和采样期间接口流量增量。采样期间同时访问国内
和境外站点。报告中的 `BindingEvidenceObserved` 只表示观察到了 Mihomo
绑定选定物理网卡的连接，不等同于完整 P0 通过。

## 3. TCP 出口

启用分流后分别访问国内和国外 IP 检测服务。不要用网卡总流量作为唯一结论，需同时观察连接：

```powershell
pktmon start --capture --pkt-size 0
Get-NetAdapterStatistics
Get-NetTCPConnection | Sort-Object OwningProcess
pktmon stop
```

验收：

- 国内测试连接由 Mihomo direct 出站，经网卡1发送。
- 国外测试连接的应用流量进入 TUN，机场服务器连接由 Mihomo 经网卡2发送。
- 网卡2上不应出现对应国内站点连接。
- 网卡1上不应出现机场服务器连接。

## 4. UDP 与 QUIC

在浏览器开发者工具确认目标请求使用 HTTP/3，或使用支持 QUIC 的测试客户端。连续执行至少 20 次：

- 国外 QUIC 流量只经网卡2。
- 国内 UDP/QUIC 流量只经网卡1。
- 任一协议出现跨出口即视为 P0 失败，不进入发布阶段。

## 5. DNS

- 国外测试域名应命中 `NETSPLIT-PROXY` 的 DoH。
- 国内域名应命中绑定网卡1的国内 DNS。
- 托盘概览中的“TUN + DNS 已接管”必须显示为已确认；仅显示“TUN 已接管”不能视为通过。
- `dnsleaktest.com` 不应观察到主宽带 DNS 查询其测试域名。
- 浏览器自带 Secure DNS 会改变测试路径，验收时分别测试开启和关闭状态。

可先运行可审计的命令行 DNS leak 测试：

```powershell
.\scripts\p0-dnsleak.ps1
```

脚本使用一次性测试 ID 触发 10 个唯一域名解析，保存服务端观察到的公网
出口、DNS 解析器、ASN 和国家信息，并在结束后关闭分流。该结果不能替代
浏览器开启/关闭 Secure DNS 两种状态下的最终页面复核。

## 6. 故障恢复

1. 断开 F50：国内访问应继续，国外访问应失败，托盘显示代理出口不可用。
2. 重连 F50：等待连续健康检查后，国外访问应自动恢复。
3. 断开网卡1：国内访问失败，国外代理仍可用。
4. 结束 Mihomo：服务应指数退避重启。
5. 执行紧急恢复：TUN 和托管进程应退出，Windows 原有网络恢复。
6. 如果运行目录中的 PID 文件指向的可执行文件不是锁定的 Mihomo，恢复工具会保留该 PID 文件作为证据，不会终止该进程。

F50 断线/恢复和 Mihomo 崩溃可使用管理员故障脚本重复测试：

```powershell
.\scripts\p0-failure.ps1 `
  -DirectAdapterName "以太网" `
  -ProxyAdapterName "以太网 3"
```

脚本会请求 UAC，禁用后重新启用网卡2，并强制终止一次托管 Mihomo。
独立 watchdog 会在主脚本异常时重新启用网卡2并关闭分流。网卡1断线和
`dnsleaktest.com` 仍需按本手册手工验收。

开发验收时可用以下命令查看、开启或关闭服务状态；`enable` 不会自动
创建 watchdog，仅应与 P0 脚本配合使用：

```powershell
.\scripts\p0-control.ps1 -Action status
.\scripts\p0-control.ps1 -Action settings
.\scripts\p0-control.ps1 -Action enable
.\scripts\p0-control.ps1 -Action disable
.\scripts\p0-control.ps1 `
  -Action add-direct-domain `
  -Domain "api.example.com"
```

## 7. 结论记录

记录 Mihomo 版本、节点协议、两张接口名称/GUID、TCP/UDP/QUIC 结果、DNS 结果和失败日志。只有所有强制项通过后，才将机器设置为开机自动启用。
