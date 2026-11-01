# 作者：Codex（GPT-6）· 2026-10-21
"""统一 Live2D 工作台：保留原件，动态接入已完成的 GBP 模型，导出图片保存 E 盘。"""
import argparse
import base64
from datetime import datetime, timedelta, timezone
import hashlib
import http.server
import io
import json
import re
from pathlib import Path
import threading
from urllib.parse import parse_qs, unquote, urlencode, urlsplit
import uuid
from PIL import Image

ROOT = Path(r'F:\5_editSourceFile\Our Notes\Live2D\其他角色_20261013')
UIKA = Path(r'F:\5_editSourceFile\Ave Mujica\Live2D\三角初华_20261013')
GBP = Path(r'F:\5_editSourceFile\邦邦\Live2D\少女乐团派对_20261021')
HERE = Path(__file__).resolve().parent
PLAYER = HERE / 'live2d-player'
EXPORT = Path(r'E:\AgentData\ReviewHub\exports\Live2D')
LOG = Path(r'E:\AgentData\ReviewHub\logs\Live2D')
DEFAULT_UIKA = '006_adv-uika_006_live_01'
AUTHOR = 'Codex (GPT-6)'
FALLBACK_FILES = {'sakiko-runtime.js', 'live2dcubismcore.min.js', 'labels.js'}
EXPORT_NAME = re.compile(r'\d{8}_\d{6}_[a-f0-9]{32}\.png\Z')
MAX_BODY = 24 * 1024 * 1024


def inside(root, relative):
    """Check both URL components and resolved physical boundary before serving a file."""
    parts = relative.split('/')
    if any(part in ('.', '..') or part.rstrip(' .') != part
           or any(c in part for c in '\\:\x00') for part in parts):
        raise ValueError('Invalid resource path')
    resolved_root = root.resolve()
    value = root.joinpath(*parts).resolve()
    value.relative_to(resolved_root)
    return value


class Catalog:
    def __init__(self, gbp_root, log_root=LOG):
        self.gbp_root = gbp_root.resolve()
        self.log_root = log_root.resolve()
        if self.log_root.drive.upper() != 'E:':
            raise ValueError('Catalog logs must resolve to E drive')
        self.lock = threading.RLock()
        self.stamp = None
        self.models = []
        self.health = {}
        self.last_error = None
        self.refresh()

    def refresh(self):
        with self.lock:
            try:
                result = self._refresh_unchecked()
                self.last_error = None
                return result
            except (ValueError, UnicodeError, OSError, TypeError, KeyError) as exc:
                signature = (type(exc).__name__, str(exc))
                if signature != self.last_error:
                    self.log_root.mkdir(parents=True, exist_ok=True)
                    if self.log_root.resolve().drive.upper() != 'E:':
                        raise ValueError('Catalog logs must resolve to E drive') from exc
                    now = datetime.now(timezone(timedelta(hours=8)))
                    record = {'author': AUTHOR, 'date': now.isoformat(), 'operation': 'catalog-read',
                              'error': str(exc)[:2000], 'kept_previous_models': len(self.models)}
                    name = now.strftime('%Y%m%d_%H%M%S_') + uuid.uuid4().hex + '.json'
                    inside(self.log_root, name).write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding='utf-8')
                    self.last_error = signature
                if not self.models:
                    raise
                return self.models, {**self.health, 'catalogError': '模型清单更新读取失败，继续使用上一份清单。'}

    def _refresh_unchecked(self):
        with self.lock:
            files = (ROOT / '模型清单.json', UIKA / '资源清单.json', self.gbp_root / '模型清单.json')
            stamp = tuple((str(p), p.stat().st_mtime_ns, p.stat().st_size) if p.exists() else (str(p), None, None)
                          for p in files)
            if stamp == self.stamp:
                return self.models, self.health
            models = []
            for item in json.loads(files[0].read_text(encoding='utf-8-sig')):
                item = dict(item)
                number = int(item['character'].split('_')[0]) if item['character'][:3].isdigit() else 0
                item.update(lib='bdon', cubism=4, band='MyGO!!!!!' if 1 <= number <= 5 else
                            'Ave Mujica' if 6 <= number <= 10 else item.get('band', '其他角色'))
                if not inside(ROOT, item['local_dir'] + '/' + item['model3']).is_file():
                    raise FileNotFoundError(item['id'])
                models.append(item)
            for item in json.loads(files[1].read_text(encoding='utf-8-sig')):
                item = dict(item)
                item.update(label='三角初华', folder='006_三角初华', local_dir='__uika__/' + item['id'],
                            lib='bdon', cubism=4, band='Ave Mujica')
                if not inside(UIKA / '模型', item['id'] + '/' + item['model3']).is_file():
                    raise FileNotFoundError(item['id'])
                models.append(item)
            warnings = []
            if files[2].is_file():
                value = json.loads(files[2].read_text(encoding='utf-8-sig'))
                if not isinstance(value, dict) or not isinstance(value.get('models'), list):
                    raise ValueError('GBP catalog must contain a models array')
                for raw in value['models']:
                    if not isinstance(raw, dict) or raw.get('ready') is not True:
                        continue
                    item = dict(raw)
                    directory = item.get('dir') or str(item.get('id', '')).removeprefix('gbp-')
                    if not isinstance(directory, str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,200}', directory):
                        warnings.append('Invalid GBP directory')
                        continue
                    model3 = item.get('model3') or 'model.json'
                    if model3 != 'model.json':
                        warnings.append('Invalid manifest: ' + directory)
                        continue
                    character = str(item.get('character', directory.split('_')[0]))
                    if not character.startswith('gbp-'):
                        character = 'gbp-' + character.zfill(3)
                    item.update(id='gbp-' + directory, character=character, lib='gbp', cubism=2,
                                local_dir='__gbp__/' + directory, model3=model3,
                                label=item.get('label') or character, band=item.get('band') or '其他角色')
                    models.append(item)
            models.sort(key=lambda item: (item['lib'] != 'bdon', item['character'], item['id']))
            if len({item['id'] for item in models}) != len(models):
                raise ValueError('Duplicate model ID')
            libraries = {lib: {'cubism': cubism, 'models': sum(m['lib'] == lib for m in models),
                              'characters': len({m['character'] for m in models if m['lib'] == lib})}
                         for lib, cubism in (('bdon', 4), ('gbp', 2))}
            version_fields = [{k: m.get(k) for k in ('id', 'character', 'lib', 'local_dir', 'model3',
                                                   'band', 'label', 'outfit_label', 'motions', 'expressions')}
                              for m in models]
            version = hashlib.sha256(json.dumps(version_fields, ensure_ascii=False, sort_keys=True).encode()).hexdigest()
            self.models = models
            self.health = {'kind': 'OurNotesLive2D', 'root': str(ROOT.resolve()),
                           'runtimeRoot': str(HERE), 'models': len(models),
                           'characters': len({m['character'] for m in models}),
                           'uika_models': sum(m['character'] == '006_Uika' for m in models),
                           'libraries': libraries, 'catalogVersion': version,
                           'catalogWarnings': warnings[:20]}
            self.stamp = stamp
            return self.models, self.health


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map,
                      '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
                      '.json': 'application/json; charset=utf-8', '.webp': 'image/webp'}

    def __init__(self, request, client_address, server, **kwargs):
        super().__init__(request, client_address, server, directory=str(ROOT), **kwargs)

    def log_message(self, fmt, *args):
        pass

    def end_headers(self):
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        super().end_headers()

    def list_directory(self, path):
        self.send_error(403, 'Directory listing disabled')
        return None

    def translate_path(self, path):
        decoded = unquote(urlsplit(path).path, errors='strict')
        if not decoded.startswith('/'):
            raise ValueError('Invalid path')
        relative = decoded[1:]
        if relative.startswith('__uika__/'):
            return str(inside(UIKA / '模型', relative[len('__uika__/'):]))
        if relative.startswith('__gbp__/'):
            return str(inside(self.server.catalog.gbp_root / '模型', relative[len('__gbp__/'):]))
        if relative.startswith('播放器/'):
            name = relative[len('播放器/'):]
            candidate = inside(self.server.player_root, name)
            if candidate.is_file() or name not in FALLBACK_FILES:
                return str(candidate)
            return str(inside(ROOT / '播放器', name))
        if relative.startswith('exports/'):
            name = relative[len('exports/'):]
            if not EXPORT_NAME.fullmatch(name) or self.server.export_root.resolve().drive.upper() != 'E:':
                raise ValueError('Invalid export name')
            return str(inside(self.server.export_root, name))
        return str(inside(ROOT, relative))

    def send_head(self):
        try:
            parsed = urlsplit(self.path)
            decoded = unquote(parsed.path, errors='strict')
            if decoded == '/api/health':
                _, value = self.server.catalog.refresh()
                return self.json_response({**value, 'port': self.server.server_port})
            if decoded == '/模型清单.json':
                models, _ = self.server.catalog.refresh()
                return self.json_response(models)
            if decoded == '/':
                self.path = '/播放器/index.html'
            if decoded in ('/__gbp__', '/__uika__', '/exports'):
                raise ValueError('Directory listing disabled')
            return super().send_head()
        except (ValueError, UnicodeError):
            self.send_error(404, 'Resource path unavailable')
        except OSError:
            self.send_error(503, 'Model catalog unavailable')
        return None

    def json_response(self, value, status=200):
        payload = json.dumps(value, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        return io.BytesIO(payload)

    def do_POST(self):
        try:
            if urlsplit(self.path).path != '/api/export-png':
                self.send_error(404)
                return
            allowed = {f'http://127.0.0.1:{self.server.server_port}', f'http://localhost:{self.server.server_port}'}
            origin = self.headers.get('Origin', '')
            host = self.headers.get('Host', '')
            if origin not in allowed or 'http://' + host != origin:
                self.send_error(403, 'Same-origin requests required')
                return
            if self.headers.get('Content-Type', '').split(';')[0].strip().lower() != 'application/json':
                self.send_error(415, 'JSON required')
                return
            length = int(self.headers.get('Content-Length', '0'))
            if not 0 < length <= MAX_BODY or self.headers.get('Transfer-Encoding'):
                self.send_error(413, 'Invalid request size')
                return
            self.connection.settimeout(15)
            body = self.rfile.read(length)
            if len(body) != length:
                raise ValueError('Incomplete request')
            value = json.loads(body)
            if not isinstance(value, dict) or not isinstance(value.get('data'), str):
                raise ValueError('PNG data required')
            png = base64.b64decode(value['data'], validate=True)
            if not png.startswith(b'\x89PNG\r\n\x1a\n') or not png.endswith(b'\x00\x00\x00\x00IEND\xaeB`\x82'):
                raise ValueError('Complete PNG required')
            with Image.open(io.BytesIO(png)) as image:
                width, height = image.size
                if image.format != 'PNG' or not 0 < width <= 4096 or not 0 < height <= 4096 or width * height > 16 * 1024 * 1024:
                    raise ValueError('PNG dimensions exceed limit')
                image.verify()
            with Image.open(io.BytesIO(png)) as image:
                image.load()
                if getattr(image, 'n_frames', 1) != 1:
                    raise ValueError('Single PNG frame required')
            raw_metadata = value.get('metadata', {})
            if not isinstance(raw_metadata, dict):
                raise ValueError('Metadata must be an object')
            metadata = {key: str(raw_metadata[key])[:2000] for key in
                        ('modelId', 'motion', 'expression', 'source', 'band', 'width', 'height') if key in raw_metadata}
            now = datetime.now(timezone(timedelta(hours=8)))
            name = now.strftime('%Y%m%d_%H%M%S_') + uuid.uuid4().hex + '.png'
            export_root = self.server.export_root
            export_root.mkdir(parents=True, exist_ok=True)
            if export_root.resolve().drive.upper() != 'E:':
                raise ValueError('Export storage must resolve to E drive')
            output = inside(export_root, name)
            record = {'author': AUTHOR, 'date': now.isoformat(), 'name': str(value.get('name', ''))[:200],
                      'path': str(output), 'sha256': hashlib.sha256(png).hexdigest(),
                      'bytes': len(png), 'width': width, 'height': height, 'metadata': metadata}
            with self.server.export_lock:
                with output.open('xb') as stream:
                    stream.write(png)
                try:
                    with output.with_suffix('.json').open('x', encoding='utf-8') as stream:
                        json.dump(record, stream, ensure_ascii=False, indent=2)
                except Exception:
                    output.unlink(missing_ok=True)
                    raise
            response = self.json_response({'ok': True, 'path': str(output), 'url': '/exports/' + name})
            self.copyfile(response, self.wfile)
        except (ValueError, UnicodeError, OSError, SyntaxError, RecursionError, Image.DecompressionBombError) as exc:
            response = self.json_response({'ok': False, 'error': str(exc)[:200]}, 400)
            self.copyfile(response, self.wfile)


class LegacyRedirect(http.server.BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        models, _ = self.server.catalog.refresh()
        uika_ids = {item['id'] for item in models if item['character'] == '006_Uika'}
        model = parse_qs(urlsplit(self.path).query).get('modelId', [DEFAULT_UIKA])[0]
        if model not in uika_ids:
            model = DEFAULT_UIKA
        self.send_response(302)
        self.send_header('Location', 'http://127.0.0.1:8999/?' + urlencode({'modelId': model}))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', '0')
        self.end_headers()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('legacy_port', nargs='?', type=int, choices=(8998, 8999))
    parser.add_argument('--port', type=int)
    parser.add_argument('--player-root', type=Path, default=PLAYER)
    parser.add_argument('--gbp-root', type=Path, default=GBP)
    parser.add_argument('--export-root', type=Path, default=EXPORT)
    parser.add_argument('--log-root', type=Path, default=LOG)
    args = parser.parse_args()
    port = args.port if args.port is not None else args.legacy_port or 8999
    if not 1024 <= port <= 65535 or args.port is not None and args.legacy_port is not None:
        parser.error('Use one valid port argument')
    if args.export_root.resolve().drive.upper() != 'E:':
        parser.error('Export path must resolve to E drive')
    if port != 8998 and not (args.player_root / 'index.html').is_file():
        parser.error('Missing player index.html')
    catalog = Catalog(args.gbp_root, args.log_root)
    server = http.server.ThreadingHTTPServer(('127.0.0.1', port), LegacyRedirect if port == 8998 else Handler)
    server.daemon_threads = True
    server.catalog = catalog
    server.player_root = args.player_root.resolve()
    server.export_root = args.export_root.resolve()
    server.export_lock = threading.Lock()
    try:
        server.serve_forever(poll_interval=.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()
