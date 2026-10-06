# StartupInspector — Windows 开机自启检测

一个轻量的 Windows 开机自启项查看与管理工具。它会扫描系统上常见的自启位置,
在界面里集中展示,并允许你启用 / 停用 / 删除这些项。

![界面预览](docs/preview.png)

## 下载

到 Releases 页面下载 `StartupInspector.exe`:自包含单文件,双击即可运行,无需预先安装 .NET 运行时。
命令行版本是 `StartupInspectorCli.exe`。

> 可执行文件未做代码签名,首次运行时 Windows SmartScreen 可能提示"未知发布者",选择"更多信息 → 仍要运行"即可。

## 功能

- 一键扫描五类自启来源(见下),普通用户即可查看。
- 表格展示:程序图标、名称、来源(按类型着色)、状态、发布者、可执行文件、位置;
  失效条目(目标文件已不存在)会显示一个警告图标。
- 按名称 / 发布者 / 路径搜索,按来源和状态筛选。
- 启用 / 停用 / 删除选中项,支持多选批量操作。
- 右键菜单:打开文件所在位置、复制命令行。
- 导出为 CSV(带 BOM,Excel 直接打开)或 JSON。
- 需要修改 HKLM、服务或系统计划任务时,一键"以管理员身份重启"。

## 扫描的自启来源

| 来源 | 位置 |
| --- | --- |
| 注册表 Run / RunOnce(当前用户) | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run(Once)` |
| 注册表 Run / RunOnce(本机) | `HKLM\Software\Microsoft\Windows\CurrentVersion\Run(Once)` |
| 启动文件夹(当前用户) | `%AppData%\Microsoft\Windows\Start Menu\Programs\Startup` |
| 启动文件夹(所有用户) | `%ProgramData%\Microsoft\Windows\Start Menu\Programs\Startup` |
| 计划任务 | 带"登录时 / 开机时"触发器的任务 |
| 系统服务 | 启动类型为"自动 / 自动(延迟)"的服务 |

注册表会同时枚举 64 位与 32 位视图(即包含 `Wow6432Node`),因此同一程序
出现在不同视图时会显示为独立条目(可通过"位置"列区分)。

## 各来源的"停用"做法

停用一律采用 Windows 自己的机制,不改动启动项本体:

- 注册表:在 `Explorer\StartupApproved\Run`(32 位项为 `Run32`)写入禁用标记,
  与任务管理器"启动"选项卡的做法完全一致。
- 启动文件夹:在 `Explorer\StartupApproved\StartupFolder` 写入禁用标记,快捷方式文件保留原位。
- 计划任务:改为 `Enabled = false` 并重新注册。
- 服务:通过 `sc config ... start= disabled` 修改启动类型(需要管理员)。

> 停用只改变"是否随开机启动",不会删除启动项本身。如果某个程序每次运行都会重建自己的启动项,
> 停用后需要再次停用,或者直接在该程序自身的设置里关闭"开机启动"。

## 使用

### 图形界面

直接运行 `StartupInspector.exe`。首次启动会自动扫描一次;可用"重新扫描"刷新。

### 命令行(无界面)

```
StartupInspectorCli.exe scan                  # 打印扫描结果
StartupInspectorCli.exe csv  --out items.csv  # 导出 CSV
StartupInspectorCli.exe json --out items.json # 导出 JSON
```

## 目录结构

```
StartupInspector/
├─ StartupInspector.sln
└─ src/
   ├─ StartupInspector.Core/   # 扫描、启停、模型、导出(可复用)
   ├─ StartupInspector.App/    # WPF 图形界面
   └─ StartupInspector.Cli/    # 命令行
```

## 构建

需要 .NET 8 SDK:

```
dotnet build StartupInspector.sln -c Release
dotnet publish src/StartupInspector.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 依赖与许可

本项目自身代码以 MIT 许可发布。运行时依赖:

- `TaskScheduler`(dahall,NuGet,MIT)—— 计划任务读写。
- `System.ServiceProcess.ServiceController`(Microsoft,MIT)—— 服务枚举。

设计上参考了以下开源项目,在此致谢:

- StartupPilot(MIT)—— 五来源扫描的划分,以及"各来源禁用约定"的思路。
- p0w3rsh3ll/AutoRuns(BSD-3-Clause)—— 自启位置覆盖的清单参考。

详见 `THIRD-PARTY-NOTICES.md`。

## 许可证

本项目以 [MIT](LICENSE) 许可发布。

