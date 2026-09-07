"""Exercise a staged Linux publish in Production without exposing its owner key."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.request
import urllib.error

app = Path(__file__).resolve().parent / 'app'
with tempfile.TemporaryDirectory(prefix='enactive-smoke-') as data:
    env = dict(os.environ, ENACTIVE_OWNER_KEY=secrets.token_hex(32),
               ENACTIVE_DATA=data, ASPNETCORE_ENVIRONMENT='Production',
               ASPNETCORE_URLS='http://127.0.0.1:15187', ASPNETCORE_HTTPS_PORT='443',
               ENACTIVE_LOCAL_PROXY='true', AllowedHosts='remote.enactive.dev')
    with open(Path(data) / 'server.log', 'w+') as log:
        process = subprocess.Popen([str(app / 'Enactive.Server')], cwd=app, env=env,
                                   stdout=log, stderr=log)
        try:
            def request(path, host='remote.enactive.dev'):
                return urllib.request.urlopen(urllib.request.Request(
                    'http://127.0.0.1:15187' + path,
                    headers={'Host': host, 'X-Forwarded-Proto': 'https'}), timeout=3)
            for attempt in range(30):
                try:
                    with request('/health') as response:
                        assert json.load(response)['status'] == 'ok'
                    break
                except (OSError, urllib.error.URLError):
                    if process.poll() is not None:
                        raise RuntimeError('Server exited during startup')
                    time.sleep(1)
            else:
                raise RuntimeError('Server did not become healthy')
            with request('/api/session') as response:
                payload = json.load(response)
                assert payload['csrfToken'] and not payload['authenticated']
                assert 'secure' in response.headers['Set-Cookie'].lower()
            with request('/') as response:
                assert response.status == 200
            try:
                request('/health', 'untrusted.example')
                raise AssertionError('Invalid host accepted')
            except urllib.error.HTTPError as error:
                assert error.code == 400
            print('PASS: Linux startup, SQLite, HTTPS proxy, Secure CSRF cookie, UI, Host filtering')
        except Exception:
            log.flush()
            log.seek(0)
            print(log.read())
            raise
        finally:
            process.terminate()
            process.wait(timeout=10)
