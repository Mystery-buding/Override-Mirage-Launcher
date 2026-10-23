# Override Mirage Launcher

## 简介

Override Mirage Launcher 是一款面向 Minecraft Java 版玩家的 Windows 启动器，用 C# / WPF / .NET 10 编写，**不引用任何第三方 NuGet 包**。它把版本安装、整合包管理、账户登录、资源下载与启动诊断整合在一起，界面简洁，日常启动和内容管理都很直接。

注意：Override Mirage Launcher 是独立开发的第三方启动器，并非 Minecraft 官方产品，也未经 Microsoft、Mojang Studios 或任何其他第三方平台赞助、认可或背书。

## 主要功能

### 游戏安装与启动

* 安装原版 Minecraft，以及 `Fabric`、`Quilt`、`Forge`、`NeoForge` 加载器。
* 支持官方源与 `BMCLAPI` 镜像，失败自动换源重试。
* 断点续装：已存在且大小匹配的文件直接跳过，可反复「校验 / 修复」补全缺失文件。
* 自动挑选合适的 Java，可在设置里手动指定；支持最大内存与游戏目录自定义。
* 启动时输出完整的目录一览与启动日志，便于定位问题。

### 整合包管理

* 自带版本隔离

### 账户与角色

* 支持 Microsoft 正版账户与离线账户两种登录方式。
* 如果您尚未拥有正版 Minecraft，请始终优先考虑购入正版 Minecraft。

### 资源中心

* 搜索并安装 Mod、资源包与光影包。
* 支持 `Modrinth` 与 `CurseForge`，可按游戏版本与加载器筛选。

### 外观与设置

* 页面切换带滑入淡入动画，左侧导航指示条同步滑动。
* 下载页为全屏覆盖式布局，分类卡片可展开，行按序错峰淡入。

### 系统要求

* Windows 10 / 11
* Minecraft Java Edition
* .NET 10 Desktop Runtime
