import importlib.util
import ctypes
import errno
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "Packages/com.vrclearn.mcp-for-unity-launcher/Editor/Supervisor/supervisor.py"
FIXTURE = Path(__file__).parent / "fixtures/dummy_mcp.py"
spec = importlib.util.spec_from_file_location("supervisor", SCRIPT)
s = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = s
spec.loader.exec_module(s)


def wait_for(predicate, timeout=8):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            result = predicate()
            if result:
                return result
        except (OSError, ValueError):
            pass
        time.sleep(0.05)
    raise AssertionError("Condition did not become true within {} seconds".format(timeout))


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def lease_data(pid=111, start="123456", port=48123, arguments=None):
    return {"schemaVersion": 1, "processId": pid, "processStartFileTimeUtc": start,
            "projectPath": "C:/My Unity Project", "baseUrl": "http://127.0.0.1:{}".format(port),
            "executable": sys.executable, "arguments": arguments or [], "packageVersion": "0.1.0"}


class FakeProcess:
    def __init__(self, executable, args):
        self.pid, self.exit_code, self.closed = 999, None, False
        self.command = executable, tuple(args)

    def poll(self):
        return self.exit_code

    def close(self):
        self.closed = True


class SupervisorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="MCP launcher tests ")
        self.root = Path(self.temp.name)
        (self.root / "editors").mkdir()
        self.now = 0
        self.alive = {111, 222}
        self.health = False
        self.busy = False
        self.spawned = []
        def spawn(executable, arguments):
            process = FakeProcess(executable, arguments)
            self.spawned.append(process)
            return process
        self.supervisor = s.Supervisor(self.root, clock=lambda: self.now,
                                       probe=lambda pid, start: "alive" if pid in self.alive else "dead",
                                       health=lambda url: self.health, busy=lambda url: self.busy,
                                       spawn=spawn)

    def tearDown(self):
        self.supervisor.close()
        self.temp.cleanup()

    def write(self, pid=111, **kwargs):
        data = lease_data(pid=pid, **kwargs)
        path = self.root / "editors" / "{}-{}.json".format(pid, data["processStartFileTimeUtc"])
        path.write_text(json.dumps(data), encoding="utf-8")
        return path

    def tick(self, now=None):
        if now is not None:
            self.now = now
        return self.supervisor.tick()

    def release(self, pid=111):
        path = self.root / "editors" / "{}-123456.json".format(pid)
        s.atomic_json(path, {"schemaVersion": 1, "processId": pid,
                             "processStartFileTimeUtc": "123456", "released": True})
        return path

    def service(self):
        return next(iter(self.supervisor.services.values()))

    def test_a_closes_b_keeps_owned_service_until_last_editor_idle(self):
        self.write(111)
        self.write(222)
        self.tick()
        child = self.spawned[0]
        self.health = True
        self.alive.remove(111)
        self.tick(1)
        self.assertFalse(child.closed)
        self.assertEqual(len(self.supervisor.store.cache), 1)
        self.alive.remove(222)
        self.tick(2)
        self.assertFalse(child.closed)
        self.assertTrue(self.tick(11.9))
        self.assertFalse(self.tick(12))
        self.assertTrue(child.closed)

    def test_explicit_release_a_keeps_b_service_alive(self):
        self.write(111)
        self.write(222)
        self.tick()
        child = self.spawned[0]
        self.health = True
        path = self.release(111)
        self.tick(1)
        self.tick(100)
        self.assertEqual([lease.process_id for lease in self.supervisor.store.cache.values()], [222])
        self.assertFalse(self.supervisor.store.uncertain)
        self.assertFalse(child.closed)
        self.assertTrue(path.exists())
        self.assertIn(111, self.alive)

    def test_last_live_editor_release_stops_after_idle_and_preserves_tombstone(self):
        self.write()
        self.tick()
        child = self.spawned[0]
        path = self.release()
        self.assertTrue(self.tick(1))
        self.assertTrue(self.tick(10.9))
        self.assertFalse(self.tick(11))
        self.assertTrue(child.closed)
        self.assertFalse(self.supervisor.store.uncertain)
        self.assertTrue(path.exists())
        self.assertIn(111, self.alive)

    def test_new_active_lease_reenables_same_identity_after_release(self):
        self.write()
        self.tick()
        self.health = True
        self.release()
        self.tick(1)
        self.assertEqual(len(self.supervisor.store.cache), 0)
        self.write()
        self.tick(5)
        self.assertEqual(len(self.supervisor.store.cache), 1)
        self.assertEqual(len(self.supervisor.store.released), 0)
        self.assertIsNone(self.supervisor.idle_since)
        self.assertEqual(len(self.spawned), 1)
        self.tick(100)
        self.assertFalse(self.spawned[0].closed)

    def test_dead_released_identity_is_cleaned_without_affecting_another_editor(self):
        path = self.release(111)
        self.write(222)
        self.tick()
        self.assertTrue(path.exists())
        self.alive.remove(111)
        self.tick(1)
        self.assertFalse(path.exists())
        self.assertEqual(len(self.supervisor.store.released), 0)
        self.assertEqual(len(self.supervisor.store.cache), 1)

    def test_invalid_tombstone_does_not_unregister_cached_live_editor(self):
        path = self.write()
        self.tick()
        s.atomic_json(path, {"schemaVersion": 1, "processId": 222,
                             "processStartFileTimeUtc": "123456", "released": True})
        self.tick(1)
        self.assertEqual(len(self.supervisor.store.cache), 1)
        self.assertTrue(self.supervisor.store.uncertain)
        self.assertFalse(self.spawned[0].closed)

    def test_release_protocol_survives_supervisor_restart_without_reactivating_editor(self):
        self.release()
        restarted = s.LeaseStore(self.root / "editors", probe=lambda *_: "alive")
        self.assertEqual(restarted.read(), [])
        self.assertFalse(restarted.uncertain)
        self.write()
        self.assertEqual(len(restarted.read()), 1)

    def test_busy_reload_ignores_mtime_and_retains_missing_or_malformed_registration(self):
        path = self.write()
        self.tick()
        self.health = True
        os.utime(path, (1, 1))
        self.tick(3600)
        path.write_text("{", encoding="utf-8")
        self.tick(7200)
        self.assertEqual(len(self.supervisor.store.cache), 1)
        self.assertTrue(path.exists())
        path.unlink()
        self.tick(10800)
        self.assertEqual(len(self.supervisor.store.cache), 1)
        self.assertFalse(self.spawned[0].closed)

    def test_crash_backoff_cap_and_stable_health_reset(self):
        self.write()
        self.tick()
        for failure in range(1, 8):
            self.spawned[-1].exit_code = 3
            self.tick(self.now + 1)
            delay = min(30, 2 ** min(failure, 5))
            self.assertEqual(self.service().retry_at - self.now, delay)
            self.tick(self.now + delay - .1)
            count = len(self.spawned)
            self.tick(self.now + .1)
            self.assertEqual(len(self.spawned), count + 1)
        self.health = True
        self.tick(self.now + 1)
        self.tick(self.now + 30)
        self.assertEqual(self.service().failures, 0)
        self.spawned[-1].exit_code = 1
        self.tick(self.now + 1)
        self.assertEqual(self.service().retry_at - self.now, 2)

    def test_startup_download_grace_then_health_hang_restart(self):
        self.write()
        self.tick()
        self.tick(299)
        self.assertFalse(self.spawned[0].closed)
        self.tick(300)
        self.assertTrue(self.spawned[0].closed)
        self.tick(302)
        self.health = True
        self.tick(303)
        self.health = False
        self.tick(304)
        self.tick(363.9)
        self.assertFalse(self.spawned[-1].closed)
        self.tick(364)
        self.assertTrue(self.spawned[-1].closed)

    def test_busy_owned_server_recovers_without_replacing_process(self):
        self.write()
        self.tick()
        original = self.spawned[0]
        self.health = True
        self.tick(1)
        self.health = False
        for now in (2, 12, 22, 31):
            self.tick(now)
            self.assertEqual(self.service().state, "unhealthy")
            self.assertIs(self.service().process, original)
            self.assertFalse(original.closed)
        self.health = True
        self.tick(32)
        self.assertEqual(self.service().state, "healthy")
        self.assertIsNone(self.service().unhealthy_since)
        self.assertIs(self.service().process, original)
        self.assertEqual(self.service().restart_count, 1)
        self.assertEqual(len(self.spawned), 1)
        # A later busy period gets its own full grace, rather than inheriting
        # the first period's elapsed time.
        self.health = False
        self.tick(40)
        self.tick(70)
        self.assertIs(self.service().process, original)
        self.assertFalse(original.closed)

    def test_persistently_unresponsive_owned_server_restarts_after_grace(self):
        self.write()
        self.tick()
        original = self.spawned[0]
        self.health = True
        self.tick(1)
        self.health = False
        self.tick(2)
        self.tick(61.9)
        self.assertFalse(original.closed)
        self.tick(62)
        self.assertTrue(original.closed)
        self.assertIsNone(self.service().process)
        self.assertEqual(self.service().state, "backoff")
        self.assertEqual(self.service().error, "MCP health check timed out")
        self.tick(63.9)
        self.assertEqual(len(self.spawned), 1)
        self.tick(64)
        self.assertEqual(len(self.spawned), 2)
        self.assertEqual(self.service().restart_count, 2)

    def test_owned_exit_during_busy_grace_is_handled_immediately(self):
        self.write()
        self.tick()
        original = self.spawned[0]
        self.health = True
        self.tick(1)
        self.health = False
        self.tick(2)
        original.exit_code = 3
        with patch.object(self.supervisor, "health") as health:
            self.tick(3)
        health.assert_not_called()
        self.assertTrue(original.closed)
        self.assertEqual(self.service().state, "backoff")
        self.assertEqual(self.service().error, "Owned MCP process exited")
        self.assertEqual(self.service().retry_at, 5)

    def test_busy_external_server_is_never_replaced_after_health_grace(self):
        self.write()
        self.health = True
        self.tick()
        self.health = False
        self.busy = True
        for now in (1, 31, 61, 121):
            self.tick(now)
            self.assertEqual(self.service().state, "blocked")
            self.assertIsNone(self.service().process)
            self.assertEqual(self.spawned, [])
        self.health = True
        self.tick(122)
        self.assertEqual(self.service().state, "healthy")
        self.assertIsNone(self.service().process)
        self.assertEqual(self.spawned, [])

    def test_healthy_external_adoption_never_kills_external_then_takes_over(self):
        self.write()
        self.health = True
        self.tick()
        self.assertEqual(self.service().state, "healthy")
        self.assertIsNone(self.service().process)
        self.assertEqual(self.spawned, [])
        self.health = False
        self.tick(1)
        self.assertEqual(len(self.spawned), 1)

    def test_unrelated_port_never_spawned_or_killed(self):
        self.write()
        self.busy = True
        self.tick()
        self.assertEqual(self.service().state, "blocked")
        self.tick(100)
        self.assertEqual(self.spawned, [])

    def test_conflicting_sources_do_not_restart_or_choose_alternating_commands(self):
        self.write(111, arguments=["first"])
        self.tick()
        self.write(222, arguments=["second"])
        for now in (1, 30, 600):
            self.tick(now)
            self.assertEqual(self.service().state, "conflict")
        self.assertEqual(len(self.spawned), 1)
        self.assertFalse(self.spawned[0].closed)

    def test_conflict_resolves_once_when_only_other_configuration_remains(self):
        self.write(111, arguments=["first"])
        self.tick()
        old = self.spawned[0]
        self.write(222, arguments=["second"])
        self.tick(1)
        self.alive.remove(111)
        self.tick(2)
        self.assertTrue(old.closed)
        self.assertEqual(self.service().command[1], ("second",))
        self.assertEqual(len(self.spawned), 2)
        self.tick(3)
        self.assertEqual(len(self.spawned), 2)

    def test_offline_probe_flips_preserve_healthy_server(self):
        arguments = ["--from", "mcpforunityserver==10.3.0", "mcp-for-unity"]
        self.write(arguments=arguments)
        self.tick()
        original = self.spawned[0]
        self.health = True
        self.tick(1)
        for now, offline in ((30, True), (60, False), (90, True)):
            latest = ["--offline", *arguments] if offline else arguments
            self.write(arguments=latest)
            self.tick(now)
            self.assertIs(self.service().process, original)
            self.assertFalse(original.closed)
            self.assertEqual(self.service().state, "healthy")
            self.assertEqual(self.service().healthy_since, 1)
            self.assertEqual(self.service().restart_count, 1)
            self.assertEqual(self.service().command[1], tuple(latest))
        self.assertEqual(len(self.spawned), 1)

    def test_offline_probe_flip_preserves_startup_deadline(self):
        arguments = ["--from", "mcpforunityserver==10.3.0", "mcp-for-unity"]
        self.write(arguments=arguments)
        self.tick()
        original = self.spawned[0]
        self.write(arguments=["--offline", *arguments])
        self.tick(299)
        self.assertIs(self.service().process, original)
        self.assertFalse(original.closed)
        self.tick(300)
        self.assertTrue(original.closed)
        self.assertEqual(self.service().state, "backoff")
        self.assertEqual(len(self.spawned), 1)

    def test_offline_probe_flip_preserves_unhealthy_grace(self):
        arguments = ["--from", "mcpforunityserver==10.3.0", "mcp-for-unity"]
        self.write(arguments=arguments)
        self.tick()
        original = self.spawned[0]
        self.health = True
        self.tick(1)
        self.health = False
        self.tick(2)
        self.write(arguments=["--offline", *arguments])
        self.tick(61)
        self.assertFalse(original.closed)
        self.tick(62)
        self.assertTrue(original.closed)
        self.assertEqual(self.service().error, "MCP health check timed out")

    def test_recovery_uses_latest_cache_policy_without_resetting_backoff(self):
        arguments = ["--from", "mcpforunityserver==10.3.0", "mcp-for-unity"]
        self.write(arguments=arguments)
        self.tick()
        self.spawned[0].exit_code = 3
        self.tick(1)
        self.assertEqual(self.service().retry_at, 3)
        self.write(arguments=["--offline", *arguments])
        self.tick(2)
        self.assertEqual(len(self.spawned), 1)
        self.assertEqual(self.service().retry_at, 3)
        self.tick(3)
        self.assertEqual(self.spawned[1].command[1], ("--offline", *arguments))
        self.spawned[1].exit_code = 3
        self.tick(4)
        self.write(arguments=arguments)
        self.tick(5)
        self.assertEqual(self.service().retry_at, 8)
        self.tick(8)
        self.assertEqual(self.spawned[2].command[1], tuple(arguments))

    def test_editors_with_different_cache_results_share_one_server(self):
        arguments = ["--from", "mcpforunityserver==10.3.0", "mcp-for-unity"]
        self.write(111, arguments=arguments)
        self.tick()
        original = self.spawned[0]
        self.health = True
        self.write(222, arguments=["--offline", *arguments])
        self.tick(1)
        self.assertEqual(self.service().state, "healthy")
        self.alive.remove(111)
        self.tick(2)
        self.assertIs(self.service().process, original)
        self.assertFalse(original.closed)
        self.assertEqual(len(self.spawned), 1)

    def test_real_config_changes_still_replace_owned_server(self):
        configurations = [
            ["--from", "mcpforunityserver==10.3.0", "mcp-for-unity"],
            ["--offline", "--from", "mcpforunityserver==10.3.1", "mcp-for-unity"],
            ["--no-cache", "--refresh", "--from", "mcpforunityserver==10.3.1", "mcp-for-unity"],
            ["--from", "mcpforunityserver==10.3.1", "mcp-for-unity", "--project-scoped-tools"],
            ["--from", "mcpforunityserver==10.3.1", "mcp-for-unity", "--project-scoped-tools", "--offline"],
            ["--from", "--offline", "mcp-for-unity"],
        ]
        for now, arguments in enumerate(configurations):
            if now:
                self.health = True
                self.tick(now - 0.1)
                self.assertEqual(self.service().state, "healthy")
                self.health = False
            self.write(arguments=arguments)
            self.tick(now)
            self.assertEqual(len(self.spawned), now + 1)
            self.assertEqual(self.spawned[-1].command[1], tuple(arguments))
            if now:
                self.assertTrue(self.spawned[-2].closed)

    def test_offline_policy_does_not_hide_real_multi_editor_conflicts(self):
        self.write(111, arguments=["--from", "mcpforunityserver==10.3.0", "mcp-for-unity"])
        self.tick()
        original = self.spawned[0]
        self.write(222, arguments=["--offline", "--from", "mcpforunityserver==10.3.1", "mcp-for-unity"])
        self.tick(1)
        self.assertEqual(self.service().state, "conflict")
        self.assertFalse(original.closed)
        self.alive.remove(111)
        self.tick(2)
        self.assertTrue(original.closed)
        self.assertEqual(len(self.spawned), 2)
        self.assertIn("mcpforunityserver==10.3.1", self.spawned[1].command[1])

    def test_offline_probe_flips_preserve_external_server(self):
        arguments = ["--from", "mcpforunityserver==10.3.0", "mcp-for-unity"]
        self.health = True
        self.write(arguments=arguments)
        self.tick()
        self.write(arguments=["--offline", *arguments])
        self.tick(1)
        self.assertEqual(self.service().state, "healthy")
        self.assertIsNone(self.service().process)
        self.assertEqual(self.spawned, [])

    def test_atomic_status_read_contention_does_not_restart_owned_service(self):
        self.write()
        self.tick()
        self.health = True
        with patch.object(s, "atomic_json", side_effect=PermissionError("Reader sharing violation")):
            self.assertTrue(self.tick(1))
        self.assertEqual(len(self.spawned), 1)
        self.assertFalse(self.spawned[0].closed)

    def test_pid_reuse_removes_dead_registration_only(self):
        path = self.write()
        with patch.object(s, "process_start_filetime", return_value=999999999):
            self.assertEqual(s.process_state(111, "123456"), "dead")
            self.supervisor.store.probe = s.process_state
            self.tick()
        self.assertFalse(path.exists())
        self.assertEqual(self.spawned, [])

    def test_unverifiable_identity_does_not_delete_or_expire(self):
        path = self.write()
        self.supervisor.store.probe = lambda *_: "unknown"
        self.tick()
        self.health = True
        self.tick(9000)
        self.assertTrue(path.exists())
        self.assertEqual(len(self.supervisor.store.cache), 1)

    def test_new_partial_file_does_not_cause_idle_shutdown(self):
        path = self.write()
        path.write_text("{")
        self.tick(0)
        self.assertTrue(self.tick(1000))
        self.assertTrue(path.exists())

    def test_unidentified_garbage_file_does_not_keep_supervisor_alive_forever(self):
        path = self.root / "editors/garbage.json"
        path.write_text("{")
        self.tick(0)
        self.assertFalse(self.tick(10))
        self.assertTrue(path.exists())

    def test_url_and_argv_validation(self):
        self.assertEqual(s.normalize_url("http://localhost:48123/"), "http://127.0.0.1:48123")
        for value in ("https://localhost:8080", "http://example.com:8080", "http://localhost:8080/mcp",
                      "http://user:pass@127.0.0.1:8080", "http://127.0.0.1:8080?secret=x"):
            with self.assertRaises(ValueError):
                s.normalize_url(value)
        with self.assertRaises(ValueError):
            s.Lease.from_json(lease_data(arguments=["bad\0arg"]))

    def test_status_omits_args_and_has_atomic_parseable_schema(self):
        self.write(arguments=["secret credential"])
        self.tick()
        raw = (self.root / "status.json").read_text()
        data = json.loads(raw)
        self.assertEqual(data["schemaVersion"], 1)
        self.assertNotIn("secret credential", raw)
        self.assertTrue(data["services"][0]["owned"])

    def test_one_cleanup_failure_does_not_block_other_owned_services(self):
        self.write(111)
        self.write(222, port=48124)
        self.tick()
        with patch.object(self.spawned[0], "close", side_effect=OSError("Unresponsive guardian")):
            self.supervisor.close()
        self.assertTrue(self.spawned[1].closed)


class ProcessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="MCP real process ")
        self.root = Path(self.temp.name)
        self.children = []

    def tearDown(self):
        for process in self.children:
            if isinstance(process, s.OwnedProcess):
                process.close()
            elif process.poll() is None:
                process.kill()
                process.wait(timeout=5)
        self.temp.cleanup()

    def start_fixture(self, mode="healthy", extra=()):
        port = free_port()
        process = subprocess.Popen([sys.executable, str(FIXTURE), "--port", str(port), "--mode", mode, *extra],
                                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.children.append(process)
        url = "http://127.0.0.1:{}".format(port)
        wait_for(lambda: s.port_busy(url))
        return process, url

    def test_exact_native_creation_time_and_pid_reuse(self):
        actual = s.process_start_filetime(os.getpid())
        self.assertIsInstance(actual, int)
        self.assertEqual(s.process_state(os.getpid(), str(actual)), "alive")
        self.assertEqual(s.process_state(os.getpid(), str(actual + 10000000)), "dead")

    def test_health_signature_and_hanging_response_timeout(self):
        process, url = self.start_fixture()
        self.assertTrue(s.healthy(url))
        foreign, foreign_url = self.start_fixture("unrelated")
        self.assertFalse(s.healthy(foreign_url))
        self.assertIsNone(foreign.poll())
        hanging, hang_url = self.start_fixture("hang")
        started = time.monotonic()
        self.assertFalse(s.healthy(hang_url, timeout=.2))
        self.assertLess(time.monotonic() - started, 2)

    def test_health_accepts_response_delayed_longer_than_one_second(self):
        class DelayedHealthHandler(BaseHTTPRequestHandler):
            def do_GET(self):
                time.sleep(1.25)
                body = json.dumps({"status": "healthy", "message": s.HEALTH_MESSAGE}).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *_):
                pass

        with HTTPServer(("127.0.0.1", 0), DelayedHealthHandler) as server:
            worker = threading.Thread(target=server.handle_request, daemon=True)
            worker.start()
            try:
                self.assertTrue(s.healthy("http://127.0.0.1:{}".format(server.server_port)))
            finally:
                worker.join(timeout=5)

    def test_owned_process_tree_cleanup_and_exact_argv_with_spaces(self):
        child_file = self.root / "child pid.txt"
        echo_file = self.root / "argv echo.txt"
        value = 'spaces & $(no shell) "quoted" \\ trailing\\'
        owned = s.OwnedProcess(sys.executable, [str(FIXTURE), "--port", str(free_port()),
                                              "--child-file", str(child_file), "--echo-file",
                                              str(echo_file), "--value", value])
        self.children.append(owned)
        wait_for(lambda: child_file.exists() and echo_file.exists())
        child_pid = int(child_file.read_text())
        wait_for(lambda: s.process_start_filetime(child_pid))
        self.assertEqual(echo_file.read_text(), value)
        owned.close()
        wait_for(lambda: s.process_start_filetime(child_pid) is None)
        self.assertEqual(owned.poll(), 0)

    def test_external_mcp_is_preserved_when_last_lease_closes(self):
        process, url = self.start_fixture()
        (self.root / "editors").mkdir()
        port = int(url.rsplit(":", 1)[1])
        data = lease_data(pid=os.getpid(), start=str(s.process_start_filetime(os.getpid())), port=port)
        s.atomic_json(self.root / "editors" / "{}-{}.json".format(data["processId"], data["processStartFileTimeUtc"]), data)
        states = ["alive"]
        now = [0]
        supervisor = s.Supervisor(self.root, probe=lambda *_: states[0], clock=lambda: now[0])
        try:
            supervisor.tick()
            self.assertFalse(next(iter(supervisor.services.values())).status(0)["owned"])
            self.assertEqual(next(iter(supervisor.services.values())).status(0)["pid"], 0)
            states[0] = "dead"
            supervisor.tick()
            now[0] = 10
            self.assertFalse(supervisor.tick())
            self.assertIsNone(process.poll())
            self.assertTrue(s.healthy(url))
        finally:
            supervisor.close()

    def test_concurrent_supervisors_start_one_mcp_and_loser_exits(self):
        (self.root / "editors").mkdir()
        port = free_port()
        starts = self.root / "starts.txt"
        data = lease_data(pid=os.getpid(), start=str(s.process_start_filetime(os.getpid())), port=port,
                          arguments=[str(FIXTURE), "--port", str(port), "--starts-file", str(starts)])
        s.atomic_json(self.root / "editors" / "{}-{}.json".format(data["processId"], data["processStartFileTimeUtc"]), data)
        runners = [subprocess.Popen([sys.executable, str(SCRIPT), "--state-dir", str(self.root), "--poll", ".1"],
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) for _ in range(4)]
        self.children.extend(runners)
        url = data["baseUrl"]
        wait_for(lambda: s.healthy(url))
        wait_for(lambda: sum(p.poll() is None for p in runners) == 1)
        self.assertEqual(len(starts.read_text().splitlines()), 1)
        status = json.loads((self.root / "status.json").read_text())
        winner = next(p for p in runners if p.poll() is None)
        self.assertEqual(status["supervisorPid"], winner.pid)
        winner.kill()
        winner.wait(timeout=5)
        wait_for(lambda: not s.port_busy(url))

    def test_supervisor_crash_cleans_owned_descendant_tree(self):
        child_file = self.root / "descendant.txt"
        runner_file = self.root / "owner.py"
        runner_file.write_text(
            "import sys,time\nfrom pathlib import Path\n"
            "sys.path.insert(0, {!r})\nimport supervisor\n"
            "p=supervisor.OwnedProcess(sys.executable, {!r})\ntime.sleep(120)\n".format(
                str(SCRIPT.parent), [str(FIXTURE), "--port", str(free_port()), "--child-file", str(child_file)]))
        runner = subprocess.Popen([sys.executable, str(runner_file)])
        self.children.append(runner)
        wait_for(child_file.exists)
        descendant = int(child_file.read_text())
        self.assertIsNotNone(s.process_start_filetime(descendant))
        runner.kill()
        runner.wait(timeout=5)
        wait_for(lambda: s.process_start_filetime(descendant) is None)

    def test_exited_server_leader_does_not_leave_descendants(self):
        child_file = self.root / "surviving child.txt"
        owned = s.OwnedProcess(sys.executable, [str(FIXTURE), "--port", str(free_port()),
            "--child-file", str(child_file), "--exit-after-child"])
        self.children.append(owned)
        wait_for(child_file.exists)
        child_pid = int(child_file.read_text())
        wait_for(lambda: owned.poll() is not None)
        owned.close()
        wait_for(lambda: s.process_start_filetime(child_pid) is None)

    def test_invalid_executable_does_not_leave_an_owned_process(self):
        with self.assertRaises(OSError):
            s.OwnedProcess(str(self.root / "missing executable"), [])

    @unittest.skipIf(os.name == "nt", "POSIX guardian signals")
    def test_paused_guardian_is_resumed_for_owned_cleanup(self):
        owned = s.OwnedProcess(sys.executable, [str(FIXTURE), "--port", str(free_port())])
        self.children.append(owned)
        os.kill(owned.process.pid, s.signal.SIGSTOP)
        owned.close()
        self.assertTrue(owned.closed)
        wait_for(lambda: s.process_start_filetime(owned.pid) is None)


class MacIdentityTests(unittest.TestCase):
    def query(self, *, status=2, result=136, error=0):
        def native(pid, flavor, argument, buffer, size):
            self.assertEqual((pid, flavor, argument, size), (123, 3, 0, 136))
            info = ctypes.cast(buffer, ctypes.POINTER(s.MacProcessInfo)).contents
            info.status = status
            info.start_seconds = 1700000000
            info.start_microseconds = 123456
            ctypes.set_errno(error)
            return result
        return patch.object(s, "mac_process_query", return_value=native)

    def test_microsecond_birth_time_and_native_layout(self):
        self.assertEqual(ctypes.sizeof(s.MacProcessInfo), 136)
        self.assertEqual(s.MacProcessInfo.start_seconds.offset, 120)
        with self.query():
            self.assertEqual(s.mac_process_start_filetime(123),
                s.FILETIME_EPOCH + 1700000000 * 10000000 + 1234560)

    def test_exited_and_missing_processes_are_dead(self):
        with self.query(status=5):
            self.assertIsNone(s.mac_process_start_filetime(123))
        with self.query(result=0, error=errno.ESRCH):
            self.assertIsNone(s.mac_process_start_filetime(123))

    def test_denied_and_partial_native_queries_are_not_dead(self):
        for result, error in ((0, errno.EPERM), (12, 0)):
            with self.query(result=result, error=error):
                with self.assertRaises(OSError):
                    s.mac_process_start_filetime(123)


if __name__ == "__main__":
    unittest.main()
