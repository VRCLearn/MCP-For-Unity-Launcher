"""Exercise the supervisor with a real MCP server and two editor-process stand-ins."""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
import socket
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'Packages/com.vrclearn.mcp-for-unity-launcher/Editor/Supervisor/supervisor.py'
spec = importlib.util.spec_from_file_location('launcher_supervisor_smoke', SCRIPT)
supervisor = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = supervisor
spec.loader.exec_module(supervisor)


def wait_for(predicate, timeout=40):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            result = predicate()
            if result:
                return result
        except (OSError, ValueError, KeyError):
            pass
        time.sleep(0.1)
    raise RuntimeError('Smoke check timed out')


def run(server_executable: Path, state: Path):
    state.mkdir(parents=True, exist_ok=False)
    (state / 'editors').mkdir()
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    url = f'http://127.0.0.1:{port}'
    editors = []
    processes = []
    events = []

    def status():
        return json.loads((state / 'status.json').read_text(encoding='utf-8'))

    def healthy_pid():
        services = status()['services']
        return services[0]['pid'] if services and services[0]['state'] == 'healthy' else 0

    def start_supervisor():
        process = subprocess.Popen([sys.executable, str(SCRIPT), '--state-dir', str(state), '--poll', '0.2'])
        processes.append(process)
        return process

    try:
        for project in ('A', 'B'):
            editor = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(180)'])
            editors.append(editor)
            start = str(supervisor.process_start_filetime(editor.pid))
            supervisor.atomic_json(state / 'editors' / f'{editor.pid}-{start}.json', {
                'schemaVersion': 1, 'processId': editor.pid, 'processStartFileTimeUtc': start,
                'projectPath': str(state / project), 'baseUrl': url,
                'executable': str(server_executable),
                'arguments': ['--transport', 'http', '--http-url', url, '--project-scoped-tools'],
                'packageVersion': '0.1.0',
            })
        worker = start_supervisor()
        first_pid = wait_for(healthy_pid)
        events.append('real MCP server healthy with two registrations')
        editors[0].terminate()
        editors[0].wait(timeout=5)
        wait_for(lambda: len(status()['editors']) == 1)
        assert healthy_pid() == first_pid
        assert supervisor.healthy(url)
        events.append('closing A preserves B and the original server process')

        # Terminate only the owned process recorded by this isolated supervisor.
        if sys.platform == 'win32':
            handle = supervisor.kernel.OpenProcess(1, False, first_pid)
            if not handle:
                raise OSError('Could not open the owned smoke server')
            try:
                if not supervisor.kernel.TerminateProcess(handle, 1):
                    raise OSError('Could not terminate the owned smoke server')
            finally:
                supervisor.kernel.CloseHandle(handle)
        else:
            import os
            import signal
            os.kill(first_pid, signal.SIGKILL)
        second_pid = wait_for(lambda: healthy_pid() if healthy_pid() != first_pid else 0)
        events.append('owned real server crash automatically recovers with a new PID')

        worker.kill()
        worker.wait(timeout=5)
        wait_for(lambda: not supervisor.healthy(url))
        # Unity's watchdog is separately compiled; this stand-in starts its replacement.
        worker = start_supervisor()
        wait_for(lambda: status()['supervisorPid'] == worker.pid and healthy_pid() != second_pid and healthy_pid())
        events.append('replacement supervisor recovers B after Windows Job cleanup')
        editors[1].terminate()
        editors[1].wait(timeout=5)
        worker.wait(timeout=20)
        assert worker.returncode == 0
        assert not supervisor.healthy(url)
        assert status()['state'] == 'stopped'
        events.append('closing B stops the owned server and supervisor after idle grace')
        report = {'events': events, 'endpoint': url,
                  'limitation': 'Editor processes are stand-ins; this does not test Unity bridge reconnect or Editor callbacks.'}
        (state / 'result.json').write_text(json.dumps(report, indent=4) + '\n', encoding='utf-8')
        return report
    finally:
        for process in editors + processes:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=5)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server-executable', type=Path, required=True)
    parser.add_argument('--state-dir', type=Path, required=True, help='New, empty isolated test directory.')
    args = parser.parse_args()
    print(json.dumps(run(args.server_executable.resolve(), args.state_dir.resolve()), indent=4))
