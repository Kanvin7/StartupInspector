# 第三方组件与参考项目

## 运行时依赖

### TaskScheduler

- 作者:dahall
- 来源:https://github.com/dahall/TaskScheduler
- 许可:MIT
- 用途:.NET 计划任务封装,用于枚举与修改带登录/开机触发器的任务。

### System.ServiceProcess.ServiceController

- 作者:Microsoft
- 来源:https://github.com/dotnet/runtime
- 许可:MIT
- 用途:枚举 Windows 服务及其启动类型。

## 设计参考

### StartupPilot

- 来源:https://github.com/Mxlted/StartupPilot
- 许可:MIT
- 参考内容:把自启来源划分为注册表 Run 键、启动文件夹、计划任务、服务四类;
  以及"注册表值名前加 ! / 启动文件夹移入 Disabled / 任务改 Enabled 标志 /
  服务改 StartType"这套各来源禁用约定。

### p0w3rsh3ll/AutoRuns

- 来源:https://github.com/p0w3rsh3ll/AutoRuns
- 许可:BSD-3-Clause
- 参考内容:Windows 自启位置(自动化位置)的覆盖清单。

