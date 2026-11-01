# 统一工单协议

协议对象：`workbench.task.v1`（`sdk/task-board-protocol.json`）。一个任务一个长期 `task_id`，客户端会话用 `adapter + external_id` 标识自己。

## 状态

```
已登记 → 排队中 → 执行中 → 待验收 → 已核销
                     ├ 等待资源
                     ├ 需要处理
                     └ 已暂停
停止请求生效 → 已取消
```

只有用户验收能核销。

## 上报

必填 `schema`、`adapter`、`external_id`、`event_id`、`sequence`、`state`。`event_id` 每次唯一、重试复用；同一执行编号 `sequence` 递增，旧序号不覆盖新状态。核心按事件编号去重。

已有工单传 `task_id`；同一执行编号保持原绑定。

## 接口

| 操作 | 接口 |
|---|---|
| 登记、上报、确认指令 | `POST /api/task-board/ingest` |
| 工单快照 | `GET /api/task-board` |
| 实时订阅 | `GET /api/task-board/events`（SSE） |
| 读取干预指令 | `GET /api/task-board/commands?adapter=...&external_id=...` |
| 工单详情 | `GET /api/task-board/jobs/<id>` |

## 接入

```python
import sys
sys.path.insert(0, 'sdk')
from task_board_sdk import TaskClient

task = TaskClient('myagent', native_task_id, '任务名', task_id=existing_or_none, endpoint='http://127.0.0.1:3900')
task.start()
task.update('running', progress={'current': 120, 'total': 300}, detail='已保存 120 张合格图')
for command in task.poll():
    actual_state, receipt = handle_native_command(command)  # 适配器真正保存断点/暂停/恢复
    task.acknowledge(command, actual_state, receipt)
task.finish([{'title': '成果', 'path': output_path, 'summary': '实际检查结果'}])
```

适配器自己实现 `handle_native_command`，在工具边界或安全检查点调用 `poll`。暂停要先保存断点、停掉本任务子进程，再确认 `paused`；恢复确认 `running`。

## 命令

```bash
node sdk/task-board-cli.cjs ingest      # 从标准输入读协议对象
node sdk/task-board-cli.cjs snapshot
node sdk/task-board-cli.cjs detail <工单编号>
```

`bridge/task_channel/` 是 Hermes 侧的桥：工单指令落到 Hermes 看板评论，执行方在工具边界接收并回写确认。

规则原文：`policies/统一工单适配协议.md`。
