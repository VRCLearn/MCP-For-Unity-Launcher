# MCP for Unity Launcher

[English](../../README.md) | [日本語](README-ja.md) | [한국어](README-ko.md) | [繁體中文](README-zh-TW.md) | **简体中文**

这是配合 [MCP for Unity](https://github.com/VRCLearn/unity-mcp) 使用的 Unity 编辑器扩展包，可通过 VPM 或 UPM 安装，让本地 MCP 服务随 Unity 项目自动启动，并在多个编辑器之间持续运行。

- **自动启动**：打开项目后，自动启动 MCP 服务并连接 Unity 编辑器。
- **多编辑器共用**：A 和 B 使用同一服务时，关闭 A，B 仍能继续使用。
- **异常恢复**：受管理的服务退出或停止响应后自动重启；守护进程退出后，由仍在运行的编辑器重新启动。

**每个需要这些功能的项目都必须安装 Launcher。** 只安装 MCP for Unity 的项目仍沿用原来的启动和关闭方式。

## 安装

需要 Unity 2021.3 或更高版本以及 [uv](https://docs.astral.sh/uv/getting-started/installation/)。Launcher 依赖 MCP for Unity 10.3.x，目标平台为 Windows、macOS 和 Linux 桌面版 Editor。

### VPM

1. 如果尚未安装 uv，先按官方说明安装。安装后重启 Unity，让编辑器能够找到 `uv` 和 `uvx`。
2. 打开 [VPM 安装页面](https://vrclearn.github.io/MCP-For-Unity-Launcher/)，点击 **Add to VCC/ALCOMD**，或手动添加仓库地址：

   ```text
   https://vrclearn.github.io/MCP-For-Unity-Launcher/index.json
   ```

   这一个仓库同时提供 **MCP for Unity** 和 **MCP for Unity Launcher**，无需分别添加两个仓库。
3. 在每个项目的包管理页面安装 **MCP for Unity Launcher**（`com.vrclearn.mcp-for-unity-launcher`）。VPM 会从同一个仓库安装它依赖的 MCP for Unity。
4. 打开项目，在 **Window → MCP for Unity** 中使用 **HTTP Local**，并完成一次 AI 客户端的 HTTP 连接配置。默认地址为 `http://127.0.0.1:8080/mcp`。

此后，Launcher 会自动启动本地服务并连接编辑器，不需要每次手动点击启动。首次运行可能需要等待 uv 下载 Python 和服务依赖；AI 客户端仍需配置自己的 MCP 连接。

### UPM（任何 Unity 项目）

通过 UPM 安装不需要 VRChat、VCC 或 ALCOMD。

1. 安装 uv 并重启 Unity，让编辑器能够找到 `uv` 和 `uvx`。
2. 按照 [MCP for Unity 安装说明](https://github.com/VRCLearn/unity-mcp)，在同一个项目中安装 **MCP for Unity 10.3.x**。UPM 不会解析本包的 `vpmDependencies`，因此请先单独安装依赖，再安装 Launcher。
3. 打开 **Window → Package Manager**，选择 **+ → Add package from git URL**，输入：

   ```text
   https://github.com/VRCLearn/MCP-For-Unity-Launcher.git?path=/Packages/com.vrclearn.mcp-for-unity-launcher#main
   ```

4. 在 **Window → MCP for Unity** 中使用 **HTTP Local**，并配置 AI 客户端的 HTTP 连接。默认地址为 `http://127.0.0.1:8080/mcp`。

每个参与的项目都需要安装 Launcher。此 Git 地址指向 `main` 的包子目录；同一项目请只使用一种安装方式。

如果 MCP for Unity 尚未安装或版本不兼容，Launcher 会打开提供 VPM 和 UPM 安装说明的窗口，等待兼容依赖，而不会因缺少依赖产生编译错误。可从 **Window → MCP for Unity Launcher** 重新打开说明；安装并重新编译完成后，Launcher 会自动启用。

## 同时使用多个 Unity 编辑器

在项目 A 和 B 中都安装这两个包，并使用相同的本地服务地址。打开两个项目后，关闭 A，共享服务仍供 B 使用。最后一个受管理的编辑器关闭后，Launcher 会等待 10 秒，再结束由它启动的服务。

打开 **Window → MCP for Unity Launcher**，可以查看服务状态、参与的编辑器和日志。窗口中的自动管理开关只影响当前项目。

两个 Launcher 窗口都可通过 **Language / 语言** 选择英语、日语、韩语、繁体中文或简体中文。首次使用跟随系统语言，不支持的语言使用英语。语言选择独立于 MCP for Unity 保存，并应用到其他项目。诊断日志和运行时错误消息保持英文。

## 使用范围与验证情况

Launcher 管理本地 HTTP 服务。远程 HTTP 服务沿用原来的管理方式；已有的外部服务可以被使用，但 Launcher 不会接管或结束其进程。

0.3.1 修复缓存检查结果变化时不必要地重启正常服务器的问题。更新后请关闭所有使用 Launcher 的 Unity Editor，等待约 15 秒，再重新打开工程以加载更新后的监督进程。

0.3.0 新增 macOS 原生进程身份检查，以及监督进程结束时的 Linux/macOS 子进程清理。CI 在三个系统上检查进程管理、C#/Python 身份信息兼容性及发布封装。Unity Editor 的验证环境是 Windows 和 Unity 2022.3.22f1；多个 Editor 的交互操作及 macOS/Linux 上的 Unity 行为尚未验证。详情见 [验证记录](../verification.md)。

## 下载与文档

- [版本下载](https://github.com/VRCLearn/MCP-For-Unity-Launcher/releases)：包含 VPM ZIP、UnityPackage 和包信息。推荐使用 VPM；导入 UnityPackage 时需要单独安装 MCP for Unity。同一项目只使用一种安装方式。
- [开发与发布](../development.md)
- [实现原理](../architecture.md)
- [反馈问题](https://github.com/VRCLearn/MCP-For-Unity-Launcher/issues)

## 许可证

[MIT](../../LICENSE)。MCP for Unity 是独立依赖，由其贡献者维护。
