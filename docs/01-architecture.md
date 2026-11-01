# 架构

## 五层

| 层 | 职责 | 关键文件 |
|---|---|---|
| 表层 | 渲染与桌面集成：标签、侧边栏、分裂窗口、托盘、右键菜单、主题 | `panel/index.html`、`shell/*.cs` |
| 服务层 | 一个 Node 进程聚合所有本机接口：服务清单、审阅页、作品目录、工单板、成果交付 | `hub/hub.cjs` 及各 `*.cjs` |
| 审阅层 | 伺服审阅页并在其中注入自动保存；登记、归档生命周期 | `review-service/审阅服务.py`、`hub/review-pages.cjs` |
| 协作层 | 跨客户端工单协议、Python SDK、命令行、Hermes 桥 | `sdk/`、`bridge/task_channel/`、`hub/task-board-*.cjs` |
| Agent 层 | Hermes（QQ 主入口）、DSH 执行器、Codex、Claude、OpenCode | 仓库外 |

## 一次请求怎么走

```
浏览器 / 桌面壳
   │  GET /api/workbench?fresh=1
   ▼
hub.cjs  ──► workbench.cjs ──► services.json 探活 + 审阅页目录扫描 + 作品目录分类映射
   │                                    │
   │                                    └─ 5 秒服务状态缓存、20 秒分类缓存
   ▼  JSON
index.html 渲染卡片；点击进入对应服务端口
```

审阅页的写入路径是反过来的：页面在浏览器里跑，脚本拦截 `localStorage.setItem`，POST 到 3955，服务器落到 `<页面目录>/审阅存档/`，下次打开先读回再填。

## 接口一览

| 接口 | 用途 |
|---|---|
| `GET /api/workbench` | 服务清单、审阅页、分类映射（`?fresh=1` 跳过缓存） |
| `GET/POST /api/review-pages`、`/api/review-status` | 审阅页读取与完成/归档/恢复 |
| `GET /api/services`、`/api/remote` | 服务在线状态、远程入口 |
| `GET /api/catalog`、`/api/deliveries`、`/api/catalog-summary` | 作品目录与成果版本 |
| `POST /api/results/reveal` | 把成果路径交给资源管理器 |
| `GET /api/projects/*`、`/api/training-queue` | 项目与训练队列只读视图 |
| `GET/POST /api/task-board*` | 工单快照、登记、上报、干预指令、SSE 订阅 |
| `GET /api/requests`、`GET/PUT /api/settings` | 本机请求队列与设置（仅本机） |

## 边界约定

- **写接口只限本机**：`workbench.cjs` 对写操作校验 Host、Origin 和 JSON 头；手机端只保留查看与标注。
- **一个状态一个写入者**：审阅状态由审阅账本写，作品目录由 `catalog-store` 写，工单由 `task-board-store` 写，互不越界。
- **缓存带语义**：`fresh=1` 只跳过服务探活与分类映射，审阅目录始终重扫，避免"点了刷新还是旧数据"。
- **失败不静默**：服务不在线显示为离线而不是消失；工单心跳超时会在看板告警。
