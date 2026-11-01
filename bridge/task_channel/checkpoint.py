# Author: Codex, 2026-10-19
"""Task-local progress and explicit artifact acceptance, without Hermes imports.

Only a fenced ``task-channel`` JSON object in a card body declares acceptance.
Natural-language requirements remain with the worker/reviewer. Existing cards
without a contract keep working, while their declared artifact paths are checked.
"""
from __future__ import annotations

from contextlib import contextmanager
import fnmatch
import json
import os
from pathlib import Path
import re
import tempfile
import time
from typing import Any
import zipfile

AUTHOR = "Codex, 2026-10-19"
MAX_STATE_BYTES = 512 * 1024
MAX_FILE_BYTES = 32 * 1024 * 1024
MAX_MATCHES = 512
MAX_SCAN = 4096
_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\Z")
_CONTRACT = re.compile(r"^```task-channel\s*\n(.*?)^```\s*$", re.M | re.S)


class CheckpointError(ValueError):
    """Invalid or stale checkpoint/acceptance data."""


def _strings(value: Any, name: str) -> list[str]:
    if not isinstance(value, (list, tuple)) or len(value) > MAX_MATCHES:
        raise CheckpointError(f"{name} must be a list of at most {MAX_MATCHES} strings")
    if any(not isinstance(x, str) or not x.strip() or len(x) > 8192 for x in value):
        raise CheckpointError(f"{name} contains an invalid string")
    return list(dict.fromkeys(x.strip() for x in value))


def _versions(value: Any) -> dict[str, str]:
    if not isinstance(value, dict) or len(value) > 128:
        raise CheckpointError("input_versions must be an object with at most 128 entries")
    if any(not isinstance(k, str) or not isinstance(v, str) or len(k) + len(v) > 8192
           for k, v in value.items()):
        raise CheckpointError("input_versions keys and values must be strings")
    return dict(value)


def parse_contract(body: str) -> dict[str, Any] | None:
    """Read one explicit JSON contract. An invalid explicit contract fails closed."""
    matches = _CONTRACT.findall(body or "")
    if not matches:
        if re.search(r"^```task-channel\b", body or "", re.M):
            raise CheckpointError("task-channel contract must have a closing code fence")
        return None
    if len(matches) != 1 or len(matches[0]) > MAX_STATE_BYTES:
        raise CheckpointError("card must contain exactly one task-channel contract")
    try:
        value = json.loads(matches[0])
    except json.JSONDecodeError as exc:
        raise CheckpointError(f"task-channel JSON is invalid: {exc.msg}") from exc
    if not isinstance(value, dict) or value.get("version", 1) != 1:
        raise CheckpointError("task-channel contract version must be 1")
    acceptance = value.get("acceptance", [])
    validate_contract(acceptance)
    delivery = value.get("delivery")
    if delivery is not None and (not isinstance(delivery, str) or delivery not in {"files", "text"}):
        raise CheckpointError("delivery must be files or text")
    if delivery == "files" and not acceptance:
        raise CheckpointError("file delivery requires explicit acceptance checks")
    if "input_versions" in value:
        _versions(value["input_versions"])
    return value


def validate_contract(acceptance: Any) -> list[dict[str, Any]]:
    if not isinstance(acceptance, list) or len(acceptance) > 128:
        raise CheckpointError("acceptance must be a list of at most 128 checks")
    for item in acceptance:
        if not isinstance(item, dict) or ("path" in item) == ("glob" in item):
            raise CheckpointError("each acceptance check needs exactly one path or glob")
        target = item.get("path", item.get("glob"))
        if not isinstance(target, str) or not target.strip() or len(target) > 8192:
            raise CheckpointError("acceptance path/glob must be a nonempty string")
        if item.get("kind", "file") not in {"file", "text", "json", "zip", "image"}:
            raise CheckpointError("kind must be file, text, json, zip, or image")
        for key in ("count", "min_count", "max_count", "min_bytes"):
            if key in item and (type(item[key]) is not int or item[key] < 0):
                raise CheckpointError(f"{key} must be a nonnegative integer")
        if item.get("max_count", MAX_MATCHES) < item.get("min_count", 1):
            raise CheckpointError("max_count must be at least min_count")
    return acceptance


def _local(path: Path) -> Path:
    if str(path).startswith(("\\\\", "//")):
        raise CheckpointError("network paths require a separate bounded verifier")
    return path.resolve()


def _inside(path: Path, roots: tuple[Path, ...]) -> bool:
    return any(path == root or root in path.parents for root in roots)


class CheckpointStore:
    """Atomic per-card JSON under its workspace, safe across worker restarts.

    ``run_id`` is an ownership fence. A new worker calls ``initialize(run_id=)``;
    an older worker then cannot overwrite that new worker's progress.
    """

    def __init__(self, workspace: str | Path, task_id: str, *, board: str = "default",
                 allowed_roots: list[str | Path] | None = None):
        if not isinstance(task_id, str) or not _ID.fullmatch(task_id):
            raise CheckpointError("invalid task_id")
        if not isinstance(board, str) or not _ID.fullmatch(board):
            raise CheckpointError("invalid board")
        self.workspace = _local(Path(workspace))
        self.task_id = task_id
        self.board = board
        self.allowed_roots = tuple(dict.fromkeys(
            [self.workspace] + [_local(Path(p)) for p in (allowed_roots or [])]))
        self.directory = self.workspace / ".task-channel"
        self.path = self.directory / f"{board}.{task_id}.checkpoint.json"

    def _prepare(self) -> None:
        if not self.workspace.is_dir():
            raise CheckpointError("task workspace must exist")
        if not _inside(_local(self.directory), (self.workspace,)):
            raise CheckpointError("checkpoint directory escapes task workspace")
        self.directory.mkdir(exist_ok=True)
        for p in (self.path, self.path.with_suffix(".lock")):
            if p.is_symlink() or not _inside(_local(p), (self.workspace,)):
                raise CheckpointError("checkpoint path escapes task workspace")

    @contextmanager
    def _locked(self):
        self._prepare()
        with self.path.with_suffix(".lock").open("a+b") as handle:
            handle.seek(0, os.SEEK_END)
            if not handle.tell():
                handle.write(b"\0")
                handle.flush()
            deadline = time.monotonic() + 2
            while True:
                try:
                    handle.seek(0)
                    if os.name == "nt":
                        import msvcrt
                        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                    else:
                        import fcntl
                        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    break
                except OSError as exc:
                    if time.monotonic() >= deadline:
                        raise CheckpointError("checkpoint is busy; retry the update") from exc
                    time.sleep(0.02)
            try:
                yield
            finally:
                handle.seek(0)
                if os.name == "nt":
                    msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    fcntl.flock(handle, fcntl.LOCK_UN)

    def load(self) -> dict[str, Any] | None:
        self._prepare()
        if not self.path.exists():
            return None
        if self.path.stat().st_size > MAX_STATE_BYTES:
            raise CheckpointError("checkpoint exceeds size limit")
        try:
            state = json.loads(self.path.read_text(encoding="utf-8"))
        except (ValueError, UnicodeError) as exc:
            raise CheckpointError("checkpoint is damaged; recover its backup before continuing") from exc
        if not isinstance(state, dict) or state.get("task_id") != self.task_id or state.get("board") != self.board:
            raise CheckpointError("checkpoint identity does not match the card")
        if state.get("version") != 1:
            raise CheckpointError("unsupported checkpoint version")
        _versions(state.get("input_versions"))
        _strings(state.get("completed_steps"), "completed_steps")
        _strings(state.get("artifacts"), "artifacts")
        validate_contract(state.get("acceptance"))
        if not isinstance(state.get("next_step"), str) or not isinstance(state.get("run_id"), str):
            raise CheckpointError("checkpoint progress fields are invalid")
        return state

    def _write(self, state: dict[str, Any]) -> dict[str, Any]:
        state.update(author=AUTHOR, updated_at=time.time())
        encoded = json.dumps(state, ensure_ascii=False, indent=2).encode("utf-8")
        if len(encoded) > MAX_STATE_BYTES:
            raise CheckpointError("checkpoint exceeds size limit")
        with tempfile.NamedTemporaryFile(dir=self.directory, prefix=".checkpoint-", delete=False) as tmp:
            tmp_path = Path(tmp.name)
            try:
                tmp.write(encoded)
                tmp.flush()
                os.fsync(tmp.fileno())
            except BaseException:
                tmp_path.unlink(missing_ok=True)
                raise
        try:
            os.replace(tmp_path, self.path)
        finally:
            tmp_path.unlink(missing_ok=True)
        return state

    def initialize(self, *, input_versions: dict[str, str] | None = None,
                   acceptance: list[dict[str, Any]] | None = None, run_id: str = "") -> dict[str, Any]:
        """Initialize or adopt a preserved checkpoint; changed inputs require review."""
        if input_versions is not None:
            _versions(input_versions)
        if acceptance is not None:
            validate_contract(acceptance)
        if not isinstance(run_id, str) or len(run_id) > 512:
            raise CheckpointError("run_id must be a string of at most 512 characters")
        with self._locked():
            state = self.load()
            if state is None:
                state = dict(version=1, task_id=self.task_id, board=self.board,
                             input_versions=input_versions or {}, completed_steps=[], artifacts=[],
                             acceptance=acceptance or [], next_step="", status="running", run_id=run_id)
            else:
                if input_versions is not None and state["input_versions"] != input_versions:
                    raise CheckpointError("input versions changed; review completed steps before updating the checkpoint")
                if acceptance is not None:
                    state["acceptance"] = acceptance
                state.update(run_id=run_id or state.get("run_id", ""), status="running")
            return self._write(state)

    def update(self, *, completed_steps: list[str] | None = None,
               artifacts: list[str] | None = None, next_step: str | None = None,
               status: str = "running", run_id: str = "") -> dict[str, Any]:
        """Append proven steps/artifacts; interruption preserves them for the next run."""
        if status not in {"running", "interrupted", "ready_for_review", "completed"}:
            raise CheckpointError("invalid checkpoint status")
        if next_step is not None and (not isinstance(next_step, str) or len(next_step) > 16384):
            raise CheckpointError("next_step must be a string of at most 16384 characters")
        with self._locked():
            state = self.load()
            if state is None:
                raise CheckpointError("initialize the checkpoint first")
            if state.get("run_id") and run_id != state["run_id"]:
                raise CheckpointError("stale worker run cannot update this checkpoint")
            for name, value in (("completed_steps", completed_steps), ("artifacts", artifacts)):
                if value is not None:
                    state[name] = _strings(state[name] + _strings(value, name), name)
            if next_step is not None:
                state["next_step"] = next_step
            state["status"] = status
            return self._write(state)

    def resume_context(self) -> str:
        state = self.load()
        if state is None:
            return ""
        lines = [f"任务 {self.task_id} 检查点：{self.path}",
                 "先核对输入版本和已有产物，再从下一步继续。",
                 "输入版本：" + json.dumps(state["input_versions"], ensure_ascii=False),
                 "已完成：" + "；".join(state["completed_steps"]),
                 "已有产物：" + "；".join(state["artifacts"]),
                 "下一步：" + state.get("next_step", ""),
                 "每完成一个阶段，更新检查点中的 completed_steps、artifacts、next_step。"]
        return "\n".join(lines)

    def _allowed_path(self, raw: str) -> Path:
        path = Path(raw)
        if ".." in path.parts:
            raise CheckpointError("parent traversal is not allowed in artifact paths")
        path = _local(path if path.is_absolute() else self.workspace / path)
        if not _inside(path, self.allowed_roots):
            raise CheckpointError("artifact path is outside the task's approved roots")
        return path

    def _matches(self, check: dict[str, Any]) -> list[Path]:
        if "path" in check:
            path = self._allowed_path(check["path"])
            return [path] if path.exists() else []
        raw = check["glob"]
        parent, pattern = str(Path(raw).parent), Path(raw).name
        if "**" in raw or any(c in parent for c in "*?["):
            raise CheckpointError("glob permits wildcards only in the final filename")
        directory = self._allowed_path(parent)
        if not directory.exists():
            return []
        result = []
        with os.scandir(directory) as entries:
            for count, entry in enumerate(entries, 1):
                if count > MAX_SCAN:
                    raise CheckpointError("artifact directory exceeds scan limit; use a narrower directory")
                if fnmatch.fnmatch(entry.name, pattern) and entry.is_file():
                    result.append(self._allowed_path(str(Path(directory, entry.name))))
                    if len(result) > MAX_MATCHES:
                        raise CheckpointError("artifact count exceeds verification limit")
        return sorted(result)

    def validate_completion(self, artifacts: list[str] | None = None) -> dict[str, Any]:
        """Validate contract plus declared artifacts, without changing card/checkpoint state."""
        state = self.load()
        if state is None:
            raise CheckpointError("initialize the checkpoint before completion validation")
        acceptance = state["acceptance"]
        declared = _strings(state["artifacts"] + _strings(artifacts or [], "artifacts"), "artifacts")
        checks = list(acceptance) + [{"path": p, "kind": _inferred_kind(p)} for p in declared]
        errors, checked = [], []
        for check in checks:
            label = check.get("path", check.get("glob", ""))
            try:
                paths = self._matches(check)
                low = check.get("count", check.get("min_count", 1))
                high = check.get("count", check.get("max_count", MAX_MATCHES))
                if not low <= len(paths) <= high:
                    raise CheckpointError(f"expected {low}..{high} files, found {len(paths)}")
                for path in paths:
                    _verify_file(path, check.get("kind", "file"), check.get("min_bytes", 1))
                checked.append(dict(target=label, count=len(paths), kind=check.get("kind", "file")))
            except (OSError, ValueError, RuntimeError, zipfile.BadZipFile) as exc:
                errors.append(f"{label}: {exc}")
        return dict(author=AUTHOR, ok=not errors, mode="contract" if acceptance else "legacy",
                    checked=checked, errors=errors)


def _inferred_kind(path: str) -> str:
    return {".json": "json", ".zip": "zip", ".docx": "zip", ".xlsx": "zip", ".pptx": "zip",
            ".png": "image", ".jpg": "image", ".jpeg": "image", ".webp": "image",
            ".gif": "image", ".txt": "text", ".md": "text", ".csv": "text"}.get(Path(path).suffix.lower(), "file")


def _verify_file(path: Path, kind: str, min_bytes: int) -> None:
    if not path.is_file():
        raise CheckpointError("expected a file")
    size = path.stat().st_size
    if size < min_bytes:
        raise CheckpointError(f"file is smaller than {min_bytes} bytes")
    with path.open("rb") as stream:
        stream.read(min(size, 4096))
    if kind == "file":
        return
    if size > MAX_FILE_BYTES:
        raise CheckpointError("file exceeds 32 MiB lightweight verification limit; use a dedicated verifier")
    if kind == "text":
        path.read_text(encoding="utf-8-sig")
    elif kind == "json":
        json.loads(path.read_text(encoding="utf-8-sig"))
    elif kind == "zip":
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            if len(entries) > 2048 or sum(i.file_size for i in entries) > 64 * 1024 * 1024:
                raise CheckpointError("archive exceeds lightweight verification limit")
            if any(i.flag_bits & 1 for i in entries):
                raise CheckpointError("encrypted archive requires a dedicated verifier")
            broken = archive.testzip()
            if broken:
                raise CheckpointError(f"archive CRC failure: {broken}")
    elif kind == "image":
        try:
            from PIL import Image
        except ImportError as exc:
            raise CheckpointError("image verification requires Pillow in the executing environment") from exc
        with Image.open(path) as picture:
            if picture.width * picture.height > 25_000_000:
                raise CheckpointError("image exceeds 25 megapixel verification limit")
            picture.verify()
        with Image.open(path) as picture:
            picture.load()
