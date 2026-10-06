# Agent Panel

面向命令行 AI Agent 的 Avalonia 桌面工作台，参考 tty7 的三栏布局：左侧管理会话，中间运行终端，右侧浏览文件、修改 diff 和 Git graph。

## 项目划分

| 项目 | 职责 |
| --- | --- |
| `src/AgentPanel` | Avalonia 桌面入口、三栏布局、会话操作 |
| `src/AgentPanel.Core` | 会话模型与存储契约，无 UI 和数据库依赖 |
| `src/AgentPanel.Storage` | SQLite 会话持久化，使用 Microsoft.Data.Sqlite |
| `src/AgentPanel.Workspace` | 文件浏览、文本预览、Git 状态 / diff / 提交图，无 UI 依赖 |
| `src/AgentPanel.Terminal` | Ghostty C ABI 绑定及 Avalonia 终端控件 |
| `native` | Linux PTY 与 libghostty-vt 桥接，固定桥接 ABI |
| `tests/AgentPanel.Smoke` | SQLite、真实 Git 仓库、真实 PTY 与 Avalonia 渲染的冒烟验证 |

## 运行

当前终端桥接支持 **Linux x86_64**。需要 .NET 10 SDK、Git、C 编译器、curl 和 tar。构建脚本会在 `.deps/` 下载并校验 Zig 0.16.0，固定 Ghostty 源码版本；不修改系统安装。

```bash
bash scripts/build-native.sh
dotnet build AgentPanel.slnx
dotnet run --project src/AgentPanel
```

网络需要代理时，通过 `HTTP_PROXY` / `HTTPS_PROXY` 配置。已有 Zig 0.16 可用 `ZIG=/path/to/zig` 指定，已有同版本 Ghostty 源码可用 `GHOSTTY_SOURCE=/path/to/ghostty` 指定。

VS Code 已配置构建、原生构建和测试任务；首次完成原生构建后，F5 启动桌面应用。

新建会话时填写名称、工作目录和启动命令，例如 `codex`、`claude` 或 `exec /bin/bash -i`。相应 CLI 需自行安装和登录。点击“启动 / 重启”运行命令；切换会话保留已启动的终端，关闭应用或删除会话会停止对应进程。程序启动时恢复配置，不自动执行保存的命令。

## 数据与 Git

会话名称、工作目录、启动命令和最近使用时间保存到 SQLite，默认位置为 `$XDG_DATA_HOME/agent-panel/sessions.db`；未设置时使用 .NET 的用户本地数据目录。启用 WAL，SQL 使用参数绑定。进程和终端画面只在本次应用运行期间保留。

右侧文件页支持目录导航和只读文本预览（上限 512 KiB）；diff 同时展示已暂存与工作区变更，新文件显示内容；Git graph 使用真实 `git log --graph --all` 展示最多 100 条提交。文件与 Git 使用按钮手动刷新，所有 Git 查询均为只读。

## Ghostty 接入

使用官方建议的外部嵌入接口 [libghostty-vt](https://github.com/ghostty-org/ghostty/blob/c3203ea4b169a18eb2ccfe92847e426d8afea858/include/ghostty/vt.h)，而非 macOS 专用的内部 surface API。固定上游提交 `c3203ea4b169a18eb2ccfe92847e426d8afea858`，因为该接口仍处于开发阶段。

libghostty-vt 负责转义序列、终端状态、颜色、滚动历史、重排和特殊按键编码；C 桥接创建 PTY、运行进程、处理终端查询回复；Avalonia 按单元格绘制文本和光标。支持基础输入、Ctrl 组合键、方向键、F1–F12、调整大小、滚轮历史和 Ctrl+Shift+V 粘贴（跟随 bracketed paste 模式）。

初始化版本尚未实现鼠标协议、文本选择/复制、图片协议绘制、完整 Kitty 普通字符键盘事件和 GPU 终端渲染。Git graph 目前是字符图；文件编辑、Git 写操作、后台守护进程和重启后的进程恢复也不在此版本内。Windows/macOS 的终端 PTY 适配需单独实现。

## 验证

```bash
dotnet run --project tests/AgentPanel.Smoke -- --ui
```

验证 SQLite 重开 / 更新 / 删除、中文会话名、Git 初始分支、含空格与中文的路径、已暂存及未暂存 diff、重命名、终端颜色 / resize / 退出，以及 Avalonia 无头渲染。测试只在临时目录创建数据库与 Git 仓库。UI 预览输出到 `/tmp/agent-panel-preview.png`。

仅验证托管部分时可传 `--skip-native --ui`。

Ghostty 及其依赖按上游许可证分发；重新分发原生库时请保留对应许可声明。项目许可证见 [LICENSE](LICENSE)。
