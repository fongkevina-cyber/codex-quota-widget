# GPT 剩余额度任务栏组件 · 分享版

适用：Windows 11 x64。安装包自带运行所需的 .NET，不需要管理员权限或另装 .NET。

## 安装

1. 接收方先从 Microsoft Store 安装官方 Codex，并在自己的 Windows 用户中登录要查看额度的 ChatGPT/Codex 账号。
2. 将 ZIP 完整解压到普通文件夹，不要直接在压缩包预览中运行文件。
3. 双击 `Install.cmd`。安装程序会检查当前用户的官方 Codex 安装及签名，在本机复制它的命令行程序到组件安装目录，然后启动组件。首次安装默认不设置开机启动；之后可从托盘菜单调整。

若 Windows 阻止未签名的本地脚本，可在解压目录打开 PowerShell，运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-Share.ps1
```

安装后可从开始菜单打开“Codex Quota Widget”；任务栏的“GPT剩余额度”显示每周剩余百分比。点击文字立即刷新，悬停可看更新时间和状态。任务栏没有安全位置时会暂时隐藏，托盘仍可切换桌面显示。

## 账号和隐私

分享 ZIP **不包含**任何人的 Codex 程序副本、登录凭据、设置或日志。组件通过接收方本机的官方 Codex 进程读取该 Windows 用户当前登录账号的额度；它自身不打开认证文件，也不会要求发送密码或密钥给分享者。

不同电脑会显示各自登录账号的数据。要换账号，请先在接收方的官方 Codex 中切换登录，再从托盘刷新组件。如果状态持续显示认证失效，可退出并重新打开组件。

## 更新与卸载

分享包仅支持当前版本的 Windows 11 x64 及 Microsoft Store 的官方 Codex。Codex 大版本更新后，如旧组件无法读取数据，重新运行较新的分享包安装程序。更新前从托盘退出正在运行的组件；安装程序不会强行关闭它。

从解压目录运行 `Uninstall.ps1`，或从组件安装目录运行同名脚本，可卸载组件。默认保留本机组件设置和日志；不会删除官方 Codex 或其登录状态。

安装程序只在 `%LOCALAPPDATA%\Programs\CodexQuotaWidget` 安装本组件，并在 `%LOCALAPPDATA%\CodexQuotaWidget` 保存本组件设置。它只读取 Microsoft Store 的官方 Codex 文件，不修改 WindowsApps、系统 PATH 或系统级注册表。
