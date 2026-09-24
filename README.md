# GPT 剩余额度 · Windows 任务栏组件

适用于 Windows 11 x64，使用 .NET 10 / WPF。任务栏透明双行文字显示「GPT剩余额度」与每周剩余百分比；点击刷新，悬停查看更新时间。支持托盘、桌面浮窗与开机启动设置，不进入 Alt+Tab。

这是独立通用版源码。使用者须在自己的电脑上安装 Microsoft Store 官方 Codex，并登录自己的 ChatGPT/Codex 账号。组件通过官方 App Server 读取额度，组件自身不读取认证文件。

## 构建与测试

在 Windows PowerShell 中从仓库根目录执行：

```powershell
.\scripts\Setup-DotNet.ps1
.\.tools\dotnet\dotnet.exe restore .\CodexQuotaWidget.slnx
.\.tools\dotnet\dotnet.exe test .\CodexQuotaWidget.slnx --no-restore
.\.tools\dotnet\dotnet.exe restore .\src\CodexQuotaWidget.App\CodexQuotaWidget.App.csproj --runtime win-x64 -p:SelfContained=true
.\scripts\Build-Share.ps1
```

分享 ZIP 位于 `artifacts/share`，自带 .NET 运行时，不包含任何人的 Codex 程序副本、认证、设置或日志。接收方完整解压后运行 `Install.cmd`，安装时核验并复制其本机官方 Codex CLI。详见 [使用说明](docs/SHARE.md)。

常规 `Build.ps1` 用于本机发布，会复制本机官方 CLI；其产物不作为分享包。源代码仓库不保存 SDK、二进制、账号数据或本机协作记录。

## 设计与架构

- [界面标准](docs/DESIGN.md)
- [架构说明](docs/ARCHITECTURE.md)

组件没有额度重置入口。任务栏无安全空位时暂时隐藏，可通过托盘切换桌面显示。当前只支持 Windows 11 x64。
