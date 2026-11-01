# Multi-bench · 宿舍机工作台

一套单机自用的 Agent 工作台：把本机的审阅入口、任务工单、服务注册和桌面外壳合成一个窗口。它按个人创作流程定制——采集素材、生成、训练、剪辑、审阅、归档，每一步的结果都落在同一个入口里，多个 Agent（Hermes / DSH / Codex / Claude / OpenCode）在同一块工单板上交接。

公开出来是当实验田：这里的做法不一定适合别人，但每个模块都解决过一个真实问题。

## 它解决什么

| 问题 | 做法 |
|---|---|
| 审阅页散在各处，标注还要手动导出 | 3900 统一入口；审阅服务在伺服 HTML 时注入脚本，页面每次写入 localStorage 自动落盘，旧页面零改动 |
| 换 Agent 就丢上下文，任务要重新交代 | 统一工单协议：一个 `task_id` 跟随任务跨客户端，授权、断点、进度、干预指令都留在工单上 |
| 工作台只能从 Chrome 打开，占 200–400 MB | .NET 8 + WebView2 原生壳，约 130 MB 常驻，带托盘、标签分裂、拖放和右键菜单 |
| 手机上看不了、批注不了 | 走 Tailscale 放行：手机只做查看与标注，派活和改配置仍然限本机 |
| 不知道 Agent 现在在干什么 | 工单板实时订阅（SSE），执行心跳 30 秒，180 秒没心跳告警 |

## 架构

```
表层     panel/index.html · 原生壳 shell/（.NET 8 + WebView2）
         ↑ HTTP / SSE
服务层   hub/  hub.cjs（入口与转接）· workbench.cjs（本机服务 API）
              catalog-store.cjs（作品目录 SQLite）· review-pages.cjs（审阅页登记）
              task-board-api/store.cjs（工单板）· result-handoff.cjs（成果交付）
         ↑ 注入 / 读取
审阅层   review-service/  审阅服务.py（3955，自动保存）· 看门狗.py
         ↑ 工单事件
协作层   sdk/ + bridge/   workbench.task.v1 协议 · Python SDK · Hermes 看板桥
         ↑ 会话
Agent    Hermes（QQ 主入口）· DSH 执行器 · Codex · Claude · OpenCode
```

数据（数据库、交付物、素材、模型）不在本仓库。工作台是外壳，数据留在各自的项目目录。

## 目录

| 路径 | 内容 |
|---|---|
| `hub/` | 3900 服务端：主入口、作品目录、审阅页登记、工单板、成果交付、服务注册表 |
| `hub/services/live2d/` | Live2D 预览服务（8999） |
| `panel/` | 面板页面：`index.html`、工单板、筛选页、壳与页面的桥接脚本 |
| `shell/` | Windows 桌面壳（C#），带自检与离屏截图取证 |
| `review-service/` | 审阅服务与登记脚本，含 `review-hub` 技能 |
| `sdk/` | 工单协议 JSON、Python SDK、命令行入口、Hermes 适配器 |
| `bridge/task_channel/` | Hermes 侧工单通道（断点、发件箱、事件收集） |
| `skills/` | 与工作台配套的 Agent 技能 |
| `policies/` | 审阅入口、成果交付、工单协议等规则文档 |
| `scripts/` | 项目创建、收尾、总览等辅助脚本 |
| `docs/` | 架构、工作流、审阅、工单与命名规则 |

## 端口

| 端口 | 服务 |
|---|---|
| 3900 | 工作台主入口（本文件所在服务） |
| 3955 | 审阅服务（自动保存） |
| 3950 | 设计资产库 · 待筛选 |
| 3960 | 磁盘迁移筛选台 |
| 3965 | 作品集本地预览 |
| 3967 | 提示词工作台 |
| 3970 | MOSS 布局创作 |
| 3971 | ComfyUI 简易生图 |
| 3975 | 角色配音 |
| 8999 | Live2D 全角色预览 |

## 快速开始

依赖：Node.js 18+、Python 3.8+、Windows 10/11（桌面壳需要 WebView2 运行时，系统一般自带）。

```bash
# 主服务
node hub/hub.cjs --port 3900

# 审阅服务（另一终端）
python review-service/审阅服务.py 3955
```

浏览器打开 `http://127.0.0.1:3900/`。桌面壳的可执行文件需要 .NET 8 SDK 自行编译：

```powershell
dotnet publish shell/WorkbenchShell.csproj -c Release
```

## 公开版说明

- 服务端常量里保留了原作者机器的绝对路径（Python 解释器、数据目录）。自用时改 `hub/services.json`、`hub/workbench.cjs` 顶部的路径常量即可。
- 远程入口地址已替换为 `example.com` 占位。
- 数据目录、数据库、凭据、模型一律未包含；`.gitignore` 里也做了排除。

## 设计取舍

1. **审阅服务不改页面。** 注入一段脚本拦截 `localStorage` 写入并同步到服务器，旧页面不用改一行就获得自动保存和多端回填。
2. **工单协议独立于客户端。** 客户端只认 `adapter + external_id`，任务本身用 `task_id` 标识；换执行者时把 `task_id` 交过去，授权和断点跟着走。
3. **注意力优先。** 面板第一屏只列需要人决定的事项；正常运行的项不占空间，也不发通知。

## License

MIT
