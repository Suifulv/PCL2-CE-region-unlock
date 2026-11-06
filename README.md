# PCL2 CE 区域限制修复工具（第三方）

> ⚠️ **非官方声明**：本仓库是一个独立的第三方工具，与 PCL / PCL2 / PCL-CE 的开发者、PCL 社区及成都瓜皮龙科技有限公司无任何关联，不代表其立场，也未获得其授权或认可。本仓库**不包含** PCL 的任何源代码、资源文件或密钥。
>
> 🤖 **AI 生成声明**：本仓库的代码与文档由 AI（DeepSeek Harness 会话）生成；生成后做过构建、对官方 2.15.1 x64 版本打补丁与启动验证，但未经过完整人工代码审计，请自行审查源码后再使用。
>
> 🧪 **版本范围**：仅测试了 **PCL2 CE 2.15.1 x64** 版本；其他版本（含后续版本）的 bundle 布局、目标方法或签名可能不同，工具会明确提示「未找到目标方法」而不会乱改。

一个用于 Windows 的小工具：对**你自己本机安装的** PCL2 CE 官方 exe 做本地字节修改，使区域限制判定恒放行，从而恢复「下载 / 安装 Minecraft」相关功能。

PCL2 CE 项目本身见：<https://github.com/PCL-Community/PCL-CE>

---

## 原理

PCL2 CE 官方 exe 是 .NET single-file bundle（`PCL2_CE_Release_x64.exe`）。工具做三步：

1. 解析 bundle footer，定位包内的 `Plain Craft Launcher 2.dll`；
2. 用 `System.Reflection.Metadata` 找到 `PCL.RestrictedFeaturePolicy` 的三个成员，把方法体改为短形式 IL：
   - `get_IsRestricted` → `ldc.i4.0; ret`（恒为 `false`）
   - `Check` → `ldc.i4.1; ret`（恒为 `true`）
   - `ThrowIfRestricted` → `ret`（空方法）
3. 只覆盖 DLL 区域后写回，**保持文件长度与 bundle 布局不变**。

因此官方 exe 内嵌的密钥、正版登录、CurseForge、联机、更新等功能全部保持原样。修改前自动备份为 `*.bak`，重复运行会检测到「已打过补丁」并跳过。

## 系统要求

- Windows x64（PCL2 CE 官方发行版为 x64）
- 目标版本：**仅验证 PCL2 CE 2.15.1 x64**；其他版本未测试
- .NET 10 运行时（本工具为**框架依赖**发布；想免运行时可用下方命令加 `--self-contained true`）

## 使用

1. 先关闭 PCL——exe 被占用时写入会失败。
2. 双击 `PCL2RegionUnlock.exe`：自动在常见位置查找 `PCL2_CE_Release_x64.exe` 并原地修复。
3. 也可以把 exe 拖到工具图标上，或用命令行：

```
PCL2RegionUnlock.exe [选项] [exe 路径...]

  --inspect     只列出 bundle 清单，不修改
  --no-backup   不创建 .bak 备份
  --no-pause    结束时不等待按键
  -h, --help    显示帮助
```

**每次官方更新后**：用新版 exe 覆盖旧文件，再双击工具即可。

## 构建

```powershell
dotnet publish src/PclRegionUnlock.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

产物在 `dist/PCL2RegionUnlock.exe`。

## 这个工具不做什么

- **不包含、不提取、不注入** PCL 的密钥——它只改 IL，官方 exe 自带的密钥字节原封不动。
- **不附带** PCL 官方 exe，也**不附带**打过补丁的 exe。
- 不联网、不上传任何数据。

> 请勿分发打过补丁的 PCL exe：修改后的 PCL 不再是「原样副本」，分发它不符合 PCL 的《分发有限许可》。本工具只应作用于你本机的安装。

## 许可证

- 本仓库工具代码：MIT，见 [LICENSE](LICENSE)。
- 本仓库不包含 PCL 代码；PCL 及其名称、二进制文件的版权归其权利人所有。
- 第三方依赖许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 免责声明

本工具仅供个人学习与研究使用。使用它修改软件可能违反该软件的使用条款或当地法律法规，也可能导致软件异常或数据丢失。请在修改前备份，并自行承担全部风险。如果你不同意上述内容，请勿使用本工具。
