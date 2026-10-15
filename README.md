# KSA-Autopilot

Kitten Space Agency 自动起飞入轨 Mod，兼容指定版本的简体中文汉化。当前为 **0.1.0 实验版**。

支持目标轨道高度与发射方位角设置，自动控制垂直起飞、重力转弯、远地点滑行和圆轨道补燃，完成后关闭发动机。

## 兼容版本

- 游戏 **2026.10.13.5602** 原版。
- 同版本的指定简体中文补丁，兼容文件 SHA-256 见 [`compatibility.json`](自动驾驶Mod/compatibility.json)。不保证其他汉化或其他 Mod 组合可用。
- 面板使用简体中文，建议安装对应汉化字体。原版程序接入已通过后台测试，原版中文字体显示尚未验证。

## 安装与启动

下载本仓库 ZIP 并解压，或使用 Git 克隆。仓库已包含编译后的 Mod 和安装所需的 Mono.Cecil，无需先安装 .NET SDK。

推荐目录结构：

```text
任意目录/
├─ Kitten Space Agency/
│  ├─ KSA.exe
│  └─ KSA.dll
└─ KSA-Autopilot/
   ├─ 自动驾驶中文启动.cmd
   ├─ 卸载自动驾驶.cmd
   └─ 自动驾驶Mod/
```

准备游玩时，双击 **自动驾驶中文启动.cmd**，然后加载存档。启用火箭所需发动机，在面板中设置目标高度，点击“自动起飞入轨”。初始朝上偏差需小于 20°，起飞推重比至少 1.1，使用 1 倍模拟速度。

**F8** 显示或隐藏面板；隐藏时自动驾驶仍然运行。**Esc**、手动姿态控制键、原生关机键或面板中止按钮可停止自动驾驶。

游戏在其他目录时，可在 PowerShell 中运行：

```powershell
# 只安装，不打开游戏。
& './自动驾驶Mod/install.ps1' -Action Install -GamePath 'D:/Games/Kitten Space Agency'

# 需要游玩时再执行这一项。
& './自动驾驶Mod/install.ps1' -Action Launch -GamePath 'D:/Games/Kitten Space Agency'
```

也可设置 `KSA_GAME_PATH` 环境变量。安装器在 Mod 自己的 `runtime` 目录中生成接入程序集，保留原有汉化调用；源游戏文件不被覆盖。文件使用硬链接，无法建立硬链接时复制文件；Content 和语言目录使用目录联接。双击 **卸载自动驾驶.cmd** 可卸载登记的运行文件，保留源游戏、汉化和日志。

## 当前限制

- **多级火箭需要手动分级**；不会自动释放发射架连接、整流罩或选择下一级发动机。
- 火箭需要足够燃料、可重启发动机及发动机矢量控制或 RCS。没有最大动压优化，实际效果受游戏大气、结构和最小油门等限制影响。
- 当前不支持精确轨道倾角控制、交会、对接或着陆。
- 入轨完成容差为目标高度的 2%，最低 1.5 km，同时要求径向速度小于 15 m/s。
- **尚未进行游戏内实飞和界面验证。**后台模拟不包含游戏大气、部件物理及真实执行器响应，不能保证所有火箭均可成功入轨。

完整操作说明见 [`使用说明.md`](自动驾驶Mod/使用说明.md)。

## 从源码构建

安装 **.NET 10 SDK**，然后执行：

```powershell
& './自动驾驶Mod/build.ps1' -GamePath 'D:/Games/Kitten Space Agency'
```

也支持通过 `-CompilerPath` 指定现有 Roslyn `csc.exe`，直接引用游戏自带的 .NET 10 运行库编译。编译时需要本机游戏文件，仓库不分发游戏程序集或资源。

## 后台验证

以下命令均不会打开游戏窗口：

```powershell
& './自动驾驶Mod/install.ps1' -Action Install -GamePath 'D:/Games/Kitten Space Agency'
& './自动驾驶Mod/test.ps1' -GamePath 'D:/Games/Kitten Space Agency'
& './自动驾驶Mod/tests/install-tests.ps1' -GamePath 'D:/Games/Kitten Space Agency'
& './自动驾驶Mod/verify.ps1' -GamePath 'D:/Games/Kitten Space Agency'
```

无窗口测试使用专用测试入口和游戏自带运行库，检查轨道计算、异常中止、线程副本绑定、原生控制轴及滑行调度，并模拟地球 200 km、120 km 和月球 50 km 入轨。安装测试使用隔离副本。完整双配置安装测试可另传 `-OriginalGamePath` 指定同版本的原版备份目录。

本机此前完成 44 项后台检查、33 项安装检查，7 个接入点和 10 处原有汉化调用检查通过；发布整理后的验证结果另见 [VALIDATION.md](VALIDATION.md)。

## 许可

沿用仓库创建时选择的 [GNU GPLv3](LICENSE)。它允许商业使用和收费分发，分发时须履行相应源码及许可义务。第三方组件见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
