"""Verify two real Unity WebSocket clients recover against an isolated MCP server.

Only generated projects and processes created by this script are managed. The
copied MCP client's EditorPrefKeys receive a per-run namespace so existing live
Editors never observe the temporary HTTP URL. The network protocol is unchanged.
"""
from __future__ import annotations

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import subprocess
import time
import threading
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / 'Packages/com.vrclearn.mcp-for-unity-launcher'


def stop_owned(process):
    if process is None or process.poll() is not None:
        return
    if os.name == 'nt':
        # A console-script entry point can have a Python child. Target only this
        # still-running process tree, never discover or stop an existing server.
        subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                       capture_output=True, timeout=20, check=False)
    else:
        os.killpg(process.pid, signal.SIGTERM)
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        if os.name == 'nt':
            process.kill()
        else:
            os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=10)


def run(args):
    unity = args.unity_executable.resolve()
    mcp = args.mcp_package.resolve()
    server_executable = args.server_executable.resolve()
    output = args.output.resolve()
    cold_start_delay = args.cold_start_delay
    if not math.isfinite(cold_start_delay) or cold_start_delay < 0:
        raise ValueError('--cold-start-delay must be a finite, nonnegative number of seconds')
    for path in (unity, server_executable, mcp / 'package.json'):
        if not path.is_file():
            raise ValueError(f'Required input is missing: {path}')
    metadata = json.loads((mcp / 'package.json').read_text(encoding='utf-8-sig'))
    if metadata.get('name') != 'com.coplaydev.unity-mcp' or metadata.get('version') != '10.3.0':
        raise ValueError('The real-network verification requires MCP for Unity 10.3.0')
    if output.exists():
        raise ValueError('--output must be a new directory; existing projects are never modified')
    version = subprocess.run([str(unity), '-version'], capture_output=True, text=True, check=True).stdout.strip()
    if not re.fullmatch(r'\d+\.\d+\.\d+[abcfpx]\d+.*', version):
        raise ValueError('Unity -version did not return a recognizable Editor version')
    output.mkdir(parents=True)
    control = output / 'control'
    control.mkdir()
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    if port == 8080:
        raise RuntimeError('Refusing to use the normal user MCP port')
    base_url = f'http://127.0.0.1:{port}'
    prefix = 'MCPLauncher.NetworkTest.' + uuid.uuid4().hex + '.'
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    handles = []
    editors = {}
    server = None
    unrelated_server = None
    unrelated_worker = None
    events = []
    report = {'endpoint': base_url, 'unity_version': version, 'mcp_version': metadata['version'],
              'preferences_namespace': prefix, 'events': events,
              'cold_start_delay_seconds': cold_start_delay,
              'isolation': 'Generated MCP client preference keys use a per-run namespace; production transport and server protocol are unchanged.'}
    popen_options = {'creationflags': subprocess.CREATE_NO_WINDOW} if os.name == 'nt' else {'start_new_session': True}

    def get_json(path):
        with opener.open(base_url + path, timeout=2) as response:
            return json.load(response)

    def wait_for(predicate, description, timeout=180):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for label, process in editors.items():
                if process.poll() is not None:
                    raise RuntimeError(f'Unity {label} exited early ({process.returncode}); inspect {output / label / "unity.log"}')
                failure = control / f'{label}.failed.txt'
                if failure.exists():
                    raise RuntimeError(f'Unity {label} failed: {failure.read_text(encoding="utf-8")}')
            try:
                value = predicate()
                if value:
                    return value
            except (OSError, ValueError, KeyError):
                pass
            time.sleep(0.2)
        raise RuntimeError(f'Timed out: {description}')

    def read_stage(label, phase):
        path = control / f'{label}.{phase}.json'
        return json.loads(path.read_text(encoding='utf-8')) if path.is_file() else None

    def hold_without_mcp(seconds, description):
        until = time.monotonic() + seconds
        def completed():
            if read_stage('A', 'ready'):
                raise AssertionError('Unity A reported Ready before a real MCP server was launched')
            return time.monotonic() >= until
        wait_for(completed, description, seconds + 10)

    def start_unrelated_health():
        class UnrelatedHealth(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.headers.get('Upgrade', '').lower() == 'websocket':
                    self.server.websocket_attempts += 1
                if self.path == '/health':
                    self.server.health_requests += 1
                body = b'{"status":"healthy","message":"Unrelated service"}'
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            def log_message(self, format, *args):
                pass
        fake = ThreadingHTTPServer(('127.0.0.1', port), UnrelatedHealth)
        fake.websocket_attempts = 0
        fake.health_requests = 0
        worker = threading.Thread(target=lambda: fake.serve_forever(poll_interval=0.1), daemon=True)
        worker.start()
        return fake, worker

    def stop_unrelated_health():
        nonlocal unrelated_server, unrelated_worker
        if unrelated_server is not None:
            unrelated_server.shutdown()
            unrelated_server.server_close()
            unrelated_worker.join(timeout=5)
            if unrelated_worker.is_alive():
                raise RuntimeError('The isolated unrelated health fixture did not stop')
            unrelated_server = None
            unrelated_worker = None

    def start_server(index):
        log = (output / f'server-{index}.log').open('wb')
        handles.append(log)
        return subprocess.Popen([str(server_executable), '--transport', 'http', '--http-url', base_url,
                                 '--project-scoped-tools'], stdout=log, stderr=subprocess.STDOUT,
                                cwd=output, **popen_options)

    def start_editor(label):
        project = output / label
        (project / 'Assets/Editor').mkdir(parents=True)
        (project / 'Packages').mkdir()
        (project / 'ProjectSettings').mkdir()
        (project / 'ProjectSettings/ProjectVersion.txt').write_text(f'm_EditorVersion: {version}\n', encoding='utf-8')
        (project / 'Packages/manifest.json').write_text('{"dependencies":{}}\n', encoding='utf-8')
        ignore = shutil.ignore_patterns('.git', '__pycache__', '__pycache__.meta', '*.pyc', '*.pyc.meta')
        shutil.copytree(PACKAGE, project / 'Packages' / PACKAGE.name, ignore=ignore)
        copied_mcp = project / 'Packages/com.coplaydev.unity-mcp'
        shutil.copytree(mcp, copied_mcp, ignore=ignore)
        preference_keys = copied_mcp / 'Editor/Constants/EditorPrefKeys.cs'
        source = preference_keys.read_text(encoding='utf-8-sig')
        if '"MCPForUnity.HttpUrl"' not in source:
            raise ValueError('Unexpected MCP EditorPrefKeys; refusing to change global user preferences')
        preference_keys.write_text(source.replace('"MCPForUnity.', '"' + prefix), encoding='utf-8')
        shutil.copy2(ROOT / 'tests/unity/LauncherNetworkProbe.cs', project / 'Assets/Editor/LauncherNetworkProbe.cs')
        environment = dict(os.environ, MCP_LAUNCHER_CONTROL_DIR=str(control),
                           MCP_LAUNCHER_PROJECT_LABEL=label, MCP_LAUNCHER_BASE_URL=base_url,
                           MCP_LAUNCHER_PREF_PREFIX=prefix)
        log = (project / 'process.log').open('wb')
        handles.append(log)
        editors[label] = subprocess.Popen([str(unity), '-batchmode', '-nographics', '-projectPath', str(project),
                                          '-executeMethod', 'LauncherNetworkProbe.Run', '-logFile', str(project / 'unity.log')],
                                         env=environment, stdout=log, stderr=subprocess.STDOUT, **popen_options)

    try:
        print('Starting real Unity project A before the MCP server', flush=True)
        start_editor('A')
        connecting_a = wait_for(lambda: read_stage('A', 'connecting'), 'Unity A first cold connection attempt', 240)
        cold_started = time.monotonic()
        unrelated_delay = min(2.0, cold_start_delay / 2.0)
        hold_without_mcp(cold_start_delay - unrelated_delay, 'Unity A pumping while the MCP server is absent')
        if unrelated_delay > 0:
            unrelated_server, unrelated_worker = start_unrelated_health()
            hold_without_mcp(unrelated_delay, 'Unity A rejecting an unrelated healthy HTTP service')
            def checked_signature():
                if unrelated_server.websocket_attempts:
                    raise AssertionError('An unrelated healthy service triggered a WebSocket connection')
                return unrelated_server.health_requests > 0
            # A long requested delay can put the client into ordinary retry backoff;
            # keep the fake alive until a health check actually observes it.
            wait_for(checked_signature, 'A health check of the unrelated service', 40)
        (control / 'A.check-cold').touch()
        waiting_a = wait_for(lambda: read_stage('A', 'waiting'), 'Unity A cold startup diagnostic check', 15)
        if waiting_a['startup_diagnostic_count'] != 0:
            raise AssertionError('Cold startup emitted MCP errors or premature verification failures')
        unrelated_health_requests = unrelated_server.health_requests if unrelated_server is not None else 0
        unrelated_websocket_attempts = unrelated_server.websocket_attempts if unrelated_server is not None else 0
        if unrelated_websocket_attempts:
            raise AssertionError('Cold startup opened a WebSocket to an unrelated healthy service')
        stop_unrelated_health()
        report['cold_start'] = {'connecting': connecting_a, 'waiting': waiting_a,
                                'measured_mcp_absence_seconds': time.monotonic() - cold_started,
                                'unrelated_health_seconds': unrelated_delay,
                                'unrelated_health_requests': unrelated_health_requests,
                                'unrelated_websocket_attempts': unrelated_websocket_attempts}
        events.append('A kept pumping without Ready or MCP errors before the real server existed')
        if unrelated_delay > 0:
            events.append('An unrelated HTTP 200 healthy response was rejected without opening a WebSocket')
        print('Starting the isolated real MCP server after the cold-start delay', flush=True)
        server = start_server(1)
        wait_for(lambda: get_json('/health').get('status') == 'healthy', 'isolated MCP server startup', 60)
        events.append('Isolated real MCP server became healthy')
        ready_a = wait_for(lambda: read_stage('A', 'ready'), 'Unity A registration, tools and round trip', 240)
        if ready_a['startup_diagnostic_count'] != 0:
            raise AssertionError('Initial connection logged MCP errors or premature verification failures')
        print('Starting real Unity project B', flush=True)
        start_editor('B')
        ready_b = wait_for(lambda: read_stage('B', 'ready'), 'Unity B registration, tools and round trip', 240)
        events.append('Both real Unity projects confirmed registration, enabled tools and a command round trip')
        (control / 'A.retire').touch()
        recovered_a = wait_for(lambda: read_stage('A', 'recovered'), 'only A client recovery', 120)
        if recovered_a['session_id'] == ready_a['session_id']:
            raise AssertionError('A did not obtain a new session after retirement')
        (control / 'B.check').touch()
        stable_b = wait_for(lambda: read_stage('B', 'stable'), 'B session stability during A recovery', 60)
        if stable_b['session_id'] != ready_b['session_id']:
            raise AssertionError('A recovery interrupted B')
        events.append('Retiring only A recovered A with a fresh session while B retained its original session and tools')
        print('Restarting only the isolated MCP server', flush=True)
        stop_owned(server)
        server = None
        time.sleep(3)
        server = start_server(2)
        wait_for(lambda: get_json('/health').get('status') == 'healthy', 'replacement isolated server startup', 60)
        (control / 'server-restarted').touch()
        restarted_a = wait_for(lambda: read_stage('A', 'server-recovered'), 'A recovery after server restart', 180)
        restarted_b = wait_for(lambda: read_stage('B', 'server-recovered'), 'B recovery after server restart', 180)
        if restarted_a['session_id'] == recovered_a['session_id'] or restarted_b['session_id'] == stable_b['session_id']:
            raise AssertionError('Server restart retained a stale confirmed session')
        events.append('Both real Unity projects recovered new confirmed sessions after the shared server restarted')
        report['sessions'] = {'initial_A': ready_a, 'initial_B': ready_b,
                              'recovered_A': recovered_a, 'stable_B': stable_b,
                              'restarted_A': restarted_a, 'restarted_B': restarted_b}
        # B captured settings after A. Restore B first, then the original snapshot in A.
        for label in ('B', 'A'):
            (control / f'{label}.finish').touch()
            process = editors[label]
            process.wait(timeout=30)
            if process.returncode:
                raise RuntimeError(f'Unity {label} cleanup failed ({process.returncode})')
            editors.pop(label)
        report['success'] = True
        (output / 'result.json').write_text(json.dumps(report, indent=4) + '\n', encoding='utf-8')
        return report
    except Exception as exception:
        report['success'] = False
        report['error'] = str(exception)
        (output / 'result.json').write_text(json.dumps(report, indent=4) + '\n', encoding='utf-8')
        raise
    finally:
        stop_unrelated_health()
        for label in ('B', 'A'):
            process = editors.get(label)
            if process is not None:
                (control / f'{label}.finish').touch()
                try:
                    process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    stop_owned(process)
        stop_owned(server)
        for handle in handles:
            handle.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity-executable', type=Path, required=True)
    parser.add_argument('--mcp-package', type=Path, required=True)
    parser.add_argument('--server-executable', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True, help='New isolated validation directory')
    parser.add_argument('--cold-start-delay', type=float, default=5.0,
                        help='Seconds without a real MCP server after A starts connecting (default: 5; use 55 to cross the first deadline)')
    print(json.dumps(run(parser.parse_args()), indent=4))
