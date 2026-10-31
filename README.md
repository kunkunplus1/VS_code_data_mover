# VS Code 数据迁移工具（VSCodeDataMover）

一个**单文件、带图形界面**的小工具：把 C 盘上的 VS Code 用户数据搬到其它盘（D/E…），
或者一键搬回来。底层使用 NTFS **目录联接（Junction）**，对 VS Code 完全透明。

- 单文件：`VSCodeDataMover.exe`（约 80 KB，依赖系统自带的 .NET Framework 4.8）
- 无需管理员权限
- 自动检测 VS Code 安装位置和数据目录
- 支持 VS Code Stable / Insiders / VSCodium（检测到哪个就处理哪个）
- 支持 C/C++ 扩展的 IntelliSense 缓存目录
- 迁移前复制并校验，失败自动回滚
- 可在同一台机器上反复“迁移 → 还原”几十次

---

## 一、系统要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 1903+ / Windows 11 |
| 运行时 | .NET Framework 4.8（Win10/11 默认自带，无需安装） |
| 权限 | 普通用户即可（目录联接不需要管理员） |
| 文件系统 | 源盘 C: 与目标盘都必须是 **NTFS**（Junction 不支持 FAT/exFAT） |

> 如果目标盘是 exFAT 的 U 盘，工具会在创建联接时报错并自动回滚，不会丢数据。

---

## 二、快速开始

1. 双击 `VSCodeDataMover.exe`。
   > 迁移的是 VS Code 的**数据**（设置、扩展、工作区状态、缓存），不是程序安装目录。
2. 工具自动检测本机 VS Code 的数据位置，列表里显示每一项的状态、源路径、大小。
3. 在“迁移目标”里选择一个文件夹，例如 `D:\VS Code Data`。
   - 工具会自动在其中创建 `Roaming\Code`、`User\.vscode`、`Local\Microsoft\vscode-cpptools` 等子目录。
4. 如果 VS Code 正在运行，点“关闭 VS Code”（或手动退出）。
5. 勾选“深度校验”（建议第一次迁移时勾选），点“一键迁移”。
6. 想搬回来时，点“一键还原”。

> 如果之前已经手动迁移过、现在打不开或报错：启动时工具会自动体检，发现问题会提示；
> 点 **“一键修复…”**，它会同时修程序指向、快捷方式、PATH 和断裂的数据联接，
> 并可从系统历史里找回被移走的数据。

### 迁移后的目录结构

```
D:\VS Code Data\
├── Roaming\Code\                          ← C:\Users\<你>\AppData\Roaming\Code
├── User\.vscode\                          ← C:\Users\<你>\.vscode
├── User\.vscode-shared\                   ← C:\Users\<你>\.vscode-shared
└── Local\Microsoft\vscode-cpptools\       ← C:\Users\<你>\AppData\Local\Microsoft\vscode-cpptools
```

C 盘原路径会变成指向这些目录的联接，VS Code 照旧按原路径读写。

---

## 三、安全设计

工具按“**先复制、再校验、最后替换**”的顺序工作，任何一步失败都会回滚：

1. **预检**
   - VS Code 必须完全退出（检测 `Code` / `Code - Insiders` / `VSCodium` 进程）
   - 目标不能是磁盘根目录、系统目录、源目录本身或源目录内部
   - 目标子目录必须不存在或为空
   - 检查目标盘剩余空间（数据量 × 1.05 + 100 MB）
   - 目标目录写入测试
2. **复制**：Robocopy `/E /COPY:DAT /DCOPY:DAT /MT:16`，保留时间戳
3. **校验**
   - 默认：文件数量、相对路径、每个文件大小 + 关键文件 SHA256
     （`settings.json`、`storage.json`、`extensions.json`、`argv.json`、`state.vscdb`）
   - 深度校验：逐文件 SHA256
4. **替换**：源目录改名为 `xxx.vscode-mover-backup-时间戳`，再在原路径创建 Junction
5. **验证联接**：读取联接目标，确认等于预期目标，并通过联接访问目录
6. **失败回滚**：联接创建失败 → 删除联接 → 改回原名 → 清理目标副本

其它保护：

- 每次操作前把状态写入 `%LOCALAPPDATA%\VSCodeDataMover\state.json`，进程被杀后可运行修复
- 中断恢复：源目录已改名但联接没建成时，下次打开会显示“中断待恢复”，`--recover` 可补建联接或回退
- 备份保留：每次迁移只保留最近 1 个工具备份，避免反复迁移撑爆 C 盘
- 还原时会重新校验，校验通过后才删除目标盘副本
- 目标盘不可用（例如 D 盘没挂载）时，可从 C 盘备份恢复，并明确提示“迁移后的改动可能丢失”
- 全程日志：`%LOCALAPPDATA%\VSCodeDataMover\logs\`

---

## 四、一键修复（手动移动过数据、现在打不开）

这是本工具的核心场景：用户自己移动/改名/建联接后，VS Code 打不开、快捷方式失效、报“找不到文件”。
现在不需要区分“修程序”还是“找回数据”——主界面只有一个 **“一键修复…”**。

### 启动自动体检

每次启动会在后台做一次快速体检；发现问题时弹窗：

> 启动体检发现 N 个问题（程序本体 / 快捷方式 / 注册表 / PATH / 数据）。是否现在打开“一键修复”？

没问题就不打扰。

### 修复窗口做什么

**上半部分：体检结果**

| 检查项 | 能自动修复的情况 |
|---|---|
| 程序本体 | 找到 Code.exe（注册表 / 快捷方式 / 常见目录 / 磁盘扫描）；找不到则明确提示重装 |
| 快捷方式 | 桌面、开始菜单、任务栏里指向已不存在 Code.exe 的快捷方式 → 改指到找到的 Code.exe |
| 注册表 | `HKCU\...\Uninstall` 的 InstallLocation / DisplayIcon / UninstallString 指向不存在的目录 → 改到找到的安装目录 |
| PATH | 用户 PATH 里失效的 `...\Microsoft VS Code\bin` → 换成有效目录 |
| 数据 | 断裂联接（有备份）、迁移中断、残留半副本、残留备份 → 按备份/状态修复 |
| 空配置 | 当前用户数据没有 settings.json / workspaceStorage，像被清空 → 提示可从历史找回 |

**下半部分：历史数据候选**

修复窗口会同时从这些系统痕迹里找“移动之前”的位置：

| 线索来源 | 具体内容 |
|---|---|
| PowerShell 历史 | `ConsoleHost_history.txt` 里的 `mklink`、`Move-Item`、`robocopy` 等命令 |
| Windows“最近使用” | `%APPDATA%\Microsoft\Windows\Recent` 里指向 Code / .vscode 的快捷方式 |
| VS Code 日志 | `%APPDATA%\Code\logs\**\*.log` 中出现过的绝对路径 |
| 注册表 | 资源管理器地址栏历史（TypedPaths）、运行对话框历史（RunMRU） |
| 本工具历史 | `state.json` 里记录过的上次迁移目标 |
| 备份目录扫描 | 名称像 `Code`、`.vscode`、`备份`、`副本`、`backup` 的目录，以及常见的 `Roaming\Code`、`User\.vscode` 结构 |

勾选某个候选，再点 **一键修复**，工具会：

1. 自动修快捷方式 / 注册表 / PATH；
2. 自动修能用备份处理的数据联接；
3. 对勾选了历史候选的数据项：先备份当前数据，再复制候选目录回原路径并校验；
4. 失败自动回滚；成功后可以继续点“一键迁移”。

> 说明：这些是“线索”，不是时光机。工具只能找到仍然存在、且内容像 VS Code 数据的目录；
> 如果旧数据已被删除、被覆盖或所在磁盘不在，工具无法凭空恢复。
> 修复只改“路径指向”和“数据”，不会凭空生成丢失的程序文件；程序本体确实被删时，请先重新安装 VS Code。

所有改动写入 `%LOCALAPPDATA%\VSCodeDataMover\logs\`。

---

## 五、命令行（便于自动化）

```
VSCodeDataMover.exe --list
VSCodeDataMover.exe --migrate "D:\VS Code Data" [--deep] [--quiet]
VSCodeDataMover.exe --restore [--deep] [--quiet]
VSCodeDataMover.exe --recover
VSCodeDataMover.exe --history
VSCodeDataMover.exe --restore-history "VS Code-userdata" "D:\备份\Code"
VSCodeDataMover.exe --doctor
VSCodeDataMover.exe --doctor --history
VSCodeDataMover.exe --fix
VSCodeDataMover.exe --version
```

模拟参数（开发/调试用）：

```
--sim-root <目录>   把用户目录模拟到指定目录（不会碰真实 C 盘数据）
```

退出码：`0` 成功，`1` 操作失败，`2` 参数错误，`3` 预检不通过。

---

## 六、目录结构

```
VS_code_qian_yi\
├── VSCodeDataMover.exe        ← 单文件程序（双击运行）
├── build.ps1                  ← 编译脚本（调用系统 csc.exe）
├── README.md                  ← 本文件
├── src\
│   ├── Core.cs                ← 路径/联接/检测/状态存储
│   ├── History.cs             ← 历史线索扫描（PowerShell/最近使用/日志/注册表/备份目录）
│   ├── Doctor.cs              ← 统一体检/修复：程序、快捷方式、注册表、PATH、数据、历史候选
│   ├── RepairForm.cs          ← 唯一的“一键修复”窗口（合并了历史恢复）
│   ├── Mover.cs               ← 迁移/还原/校验/回滚/数据修复
│   ├── Ui.cs                  ← WinForms 主界面
│   ├── Program.cs             ← 入口 + 命令行
│   └── app.manifest           ← DPI / 长路径声明
```

### 重新编译

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

使用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（C# 5）编译，
不依赖 Visual Studio 或 .NET SDK。

---

## 七、常见问题

**Q：迁移后 VS Code 打不开 / 设置丢失？**
A：先看列表状态。如果显示“目标丢失”，说明目标盘没挂载或路径被移动；
接回目标盘即可，或用“一键还原”从备份恢复。

**Q：会不会影响 VS Code 自动更新？**
A：不会。更新只替换程序目录，用户数据目录独立存放。

**Q：卸载 VS Code 会删掉 D 盘数据吗？**
A：官方卸载器不会删用户数据；但如果你手动“彻底清理”删除
`%APPDATA%\Code` 这类联接路径，可能连带删掉 D 盘数据。请先用工具的
“还原”功能搬回来，再卸载。

**Q：为什么迁移后 C 盘还有 `xxx.vscode-mover-backup-*`？**
A：那是迁移前的备份，确认使用正常后可以删除；下次迁移会自动只保留最近 1 个。

**Q：支持绿色版 / 便携版 VS Code 吗？**
A：检测到安装目录下有 `data` 文件夹（便携模式）时会在界面上提示。
便携模式本身已经把数据放在安装目录，不需要本工具。

**Q：会迁移哪些目录？**
A：`%APPDATA%\Code`、`%USERPROFILE%\.vscode`、`%USERPROFILE%\.vscode-shared`、
`%LOCALAPPDATA%\Microsoft\vscode-cpptools`；Insiders / VSCodium 存在时一并处理。
不会迁移 `~/.copilot`（Copilot CLI 自身数据）和 `%TEMP%` 临时缓存。

**Q：同一台机器反复迁移/还原会出问题吗？**
A：测试中连续 50 轮“迁移 → 改数据 → 还原”全部通过，且每轮都做全量 SHA256 比对。
工具每次都会重新备份、校验、清理，不会累积联接或备份。

---

## 八、实现要点

- **目录联接**：`mklink /J`（不需要管理员），读取联接目标用
  `DeviceIoControl(FSCTL_GET_REPARSE_POINT)` 解析 `IO_REPARSE_TAG_MOUNT_POINT`。
- **长路径**：清单声明 `longPathAware`，内部统一使用 `\\?\` 前缀，
  并声明 `TargetFramework 4.8` 以启用 .NET 的长路径处理。
- **状态文件**：`%LOCALAPPDATA%\VSCodeDataMover\state.json`，
  记录 Target / Backup / Phase，用于中断恢复。
- **备份命名**：`<原名>.vscode-mover-backup-yyyyMMdd-HHmmss`，只保留最近 1 个。
- **进程检测**：`Code`、`Code - Insiders`、`VSCodium`；
  “关闭 VS Code”发送 `CloseMainWindow` 并等待，不会强杀未保存的工作。
