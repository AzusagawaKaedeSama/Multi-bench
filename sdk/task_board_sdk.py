# 作者：Codex（GPT-6）· 2026-10-28
"""Small standard-library adapter client for workbench.task.v1."""
import json
import threading
import time
import urllib.parse
import urllib.request
import uuid

class TaskClient:
    def __init__(self, adapter, external_id, title, *, task_id=None, agent=None,
                 base_url='http://127.0.0.1:3900', deadline_at=None, capabilities=None):
        self.adapter, self.external_id, self.title = adapter, external_id, title
        self.base_url, self.task_id, self.deadline_at = base_url.rstrip('/'), task_id, deadline_at
        self.agent = agent or adapter
        self.capabilities = capabilities or ['observe', 'ack']
        self.sequence = 0
        self.state, self.detail, self.progress = 'registered', '', {}
        self.evidence, self.pending_ack = None, None
        self._lock, self._stop = threading.RLock(), threading.Event()
        self._thread, self.last_error = None, None

    def _request(self, route, value=None):
        raw = json.dumps(value, ensure_ascii=False).encode('utf8') if value is not None else None
        request = urllib.request.Request(self.base_url+route, data=raw,
                                         headers={'Origin': self.base_url, 'Content-Type': 'application/json'})
        with urllib.request.urlopen(request, timeout=5) as response:
            return json.load(response)

    def update(self, state=None, *, detail=None, progress=None, evidence=None, ack_command_id=None):
        with self._lock:
            self.state = state or self.state
            if detail is not None:
                self.detail = detail
            if progress is not None:
                self.progress = progress
            if evidence is not None:
                self.evidence = evidence
            if ack_command_id:
                self.pending_ack = ack_command_id
            self.sequence = max(self.sequence+1, time.time_ns()//1_000_000)
            value = {'schema':'workbench.task.v1', 'adapter':self.adapter, 'external_id':self.external_id,
                     'event_id':str(uuid.uuid4()), 'sequence':self.sequence, 'title':self.title,
                     'agent':self.agent, 'state':self.state, 'detail':self.detail, 'progress':self.progress,
                     'capabilities':self.capabilities}
            if self.task_id:
                value['task_id'] = self.task_id
            if self.deadline_at:
                value['deadline_at'] = self.deadline_at
            if self.evidence is not None:
                value['evidence'] = self.evidence
            if self.pending_ack:
                value['ack_command_id'] = self.pending_ack
            result = self._request('/api/task-board/ingest', value)
            self.task_id = result['job']['id']
            self.last_error = None
            self.pending_ack = None
            return result

    def start(self, heartbeat_seconds=30):
        self.update('running')
        if self._thread and self._thread.is_alive():
            return self
        self._stop.clear()
        def run():
            while not self._stop.wait(max(10, heartbeat_seconds)):
                try:
                    self.update()
                except Exception as error:
                    self.last_error = str(error)
        self._thread = threading.Thread(target=run, name='workbench-heartbeat', daemon=True)
        self._thread.start()
        return self

    def poll(self):
        query = urllib.parse.urlencode({'adapter':self.adapter, 'external_id':self.external_id})
        return self._request('/api/task-board/commands?'+query)['commands']

    def acknowledge(self, command, actual_state, detail):
        result = self.update(actual_state, detail=detail, ack_command_id=command['id'])
        if actual_state == 'cancelled':
            self.stop_heartbeat()
        return result

    def finish(self, evidence, detail='成果已提交，请验收。'):
        result = self.update('review', evidence=evidence, detail=detail)
        self.stop_heartbeat()
        return result

    def stop_heartbeat(self):
        self._stop.set()

