"""User-scoped MCP supervisor for Windows, macOS, and Linux. Standard library only.

Lease files are persistent registrations, not expiring heartbeats. Editor liveness
is determined by PID plus process creation FILETIME. A validated identity-only
record with released=true explicitly unregisters a still-running editor; a new
full registration at the same filename enables it again. Windows compares exactly;
Linux /proc has clock-tick precision; macOS uses libproc's microsecond timestamp.
Windows owns children using a kill-on-close Job; POSIX uses a pipe-connected
guardian that cleans up its private process group when the supervisor exits.
No command arguments or child output are logged, because they may contain secrets.
"""

import argparse
import ctypes
import datetime
import errno
from functools import lru_cache
import ipaddress
import json
import logging
import logging.handlers
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass


FILETIME_EPOCH = 116444736000000000
HEALTH_MESSAGE = "MCP for Unity server is running"


class MacProcessInfo(ctypes.Structure):
    # Darwin proc_bsdinfo, from <sys/proc_info.h> (PROC_PIDTBSDINFO).
    _fields_ = [(name, ctypes.c_uint32) for name in
                ("flags", "status", "exit_status", "pid", "parent_pid", "uid", "gid",
                 "real_uid", "real_gid", "saved_uid", "saved_gid", "reserved")] + [
                ("command", ctypes.c_char * 16), ("name", ctypes.c_char * 32)] + [
                (name, ctypes.c_uint32) for name in
                ("file_count", "group_id", "job_count", "tty_device", "tty_group")] + [
                ("nice", ctypes.c_int32), ("start_seconds", ctypes.c_uint64),
                ("start_microseconds", ctypes.c_uint64)]


@lru_cache(maxsize=1)
def mac_process_query():
    library = ctypes.CDLL("/usr/lib/libproc.dylib", use_errno=True)
    query = library.proc_pidinfo
    query.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int]
    query.restype = ctypes.c_int
    return query


def mac_process_start_filetime(pid):
    info = MacProcessInfo()
    size = ctypes.sizeof(info)
    ctypes.set_errno(0)
    result = mac_process_query()(pid, 3, 0, ctypes.byref(info), size)
    if result != size:
        error = ctypes.get_errno()
        if result == 0 and error == errno.ESRCH:
            return None
        raise OSError(error or errno.EIO, "Could not query macOS process identity")
    if info.status == 5:  # SZOMB: exited but not yet reaped.
        return None
    return FILETIME_EPOCH + info.start_seconds * 10000000 + info.start_microseconds * 10


def utc_timestamp(offset=0):
    return (datetime.datetime.now(datetime.timezone.utc) +
            datetime.timedelta(seconds=offset)).isoformat().replace("+00:00", "Z")


def atomic_json(path, value):
    """Publish status without exposing partial JSON to editor readers."""
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False)
    # Windows readers may briefly hold a handle without FILE_SHARE_DELETE.
    for attempt in range(6):
        try:
            os.replace(temporary, path)
            return
        except PermissionError:
            if attempt == 5:
                raise
            time.sleep(0.05)


if os.name == "nt":
    from ctypes import wintypes

    class STARTUPINFO(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("lpReserved", wintypes.LPWSTR),
                    ("lpDesktop", wintypes.LPWSTR), ("lpTitle", wintypes.LPWSTR),
                    ("dwX", wintypes.DWORD), ("dwY", wintypes.DWORD),
                    ("dwXSize", wintypes.DWORD), ("dwYSize", wintypes.DWORD),
                    ("dwXCountChars", wintypes.DWORD), ("dwYCountChars", wintypes.DWORD),
                    ("dwFillAttribute", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                    ("wShowWindow", wintypes.WORD), ("cbReserved2", wintypes.WORD),
                    ("lpReserved2", ctypes.POINTER(ctypes.c_byte)),
                    ("hStdInput", wintypes.HANDLE), ("hStdOutput", wintypes.HANDLE),
                    ("hStdError", wintypes.HANDLE)]

    class PROCESS_INFORMATION(ctypes.Structure):
        _fields_ = [("hProcess", wintypes.HANDLE), ("hThread", wintypes.HANDLE),
                    ("dwProcessId", wintypes.DWORD), ("dwThreadId", wintypes.DWORD)]

    class BASIC_LIMIT(ctypes.Structure):
        _fields_ = [("PerProcessUserTimeLimit", ctypes.c_int64),
                    ("PerJobUserTimeLimit", ctypes.c_int64), ("LimitFlags", wintypes.DWORD),
                    ("MinimumWorkingSetSize", ctypes.c_size_t),
                    ("MaximumWorkingSetSize", ctypes.c_size_t),
                    ("ActiveProcessLimit", wintypes.DWORD), ("Affinity", ctypes.c_size_t),
                    ("PriorityClass", wintypes.DWORD), ("SchedulingClass", wintypes.DWORD)]

    class IO_COUNTERS(ctypes.Structure):
        _fields_ = [(name, ctypes.c_uint64) for name in
                    ("ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                     "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

    class EXTENDED_LIMIT(ctypes.Structure):
        _fields_ = [("BasicLimitInformation", BASIC_LIMIT), ("IoInfo", IO_COUNTERS),
                    ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                    ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]

    class SECURITY_ATTRIBUTES(ctypes.Structure):
        _fields_ = [("nLength", wintypes.DWORD), ("lpSecurityDescriptor", ctypes.c_void_p),
                    ("bInheritHandle", wintypes.BOOL)]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    signatures = {
        "OpenProcess": (wintypes.HANDLE, [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]),
        "CloseHandle": (wintypes.BOOL, [wintypes.HANDLE]),
        "GetProcessTimes": (wintypes.BOOL, [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4),
        "WaitForSingleObject": (wintypes.DWORD, [wintypes.HANDLE, wintypes.DWORD]),
        "GetExitCodeProcess": (wintypes.BOOL, [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]),
        "CreateJobObjectW": (wintypes.HANDLE, [ctypes.c_void_p, wintypes.LPCWSTR]),
        "SetInformationJobObject": (wintypes.BOOL, [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]),
        "AssignProcessToJobObject": (wintypes.BOOL, [wintypes.HANDLE, wintypes.HANDLE]),
        "ResumeThread": (wintypes.DWORD, [wintypes.HANDLE]),
        "TerminateProcess": (wintypes.BOOL, [wintypes.HANDLE, wintypes.UINT]),
        "CreateFileW": (wintypes.HANDLE, [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                        ctypes.POINTER(SECURITY_ATTRIBUTES), wintypes.DWORD,
                                        wintypes.DWORD, wintypes.HANDLE]),
        "CreateProcessW": (wintypes.BOOL, [wintypes.LPCWSTR, wintypes.LPWSTR, ctypes.c_void_p,
                                           ctypes.c_void_p, wintypes.BOOL, wintypes.DWORD,
                                           ctypes.c_void_p, wintypes.LPCWSTR,
                                           ctypes.POINTER(STARTUPINFO), ctypes.POINTER(PROCESS_INFORMATION)]),
    }
    for name, (restype, argtypes) in signatures.items():
        function = getattr(kernel, name)
        function.restype, function.argtypes = restype, argtypes


def process_start_filetime(pid):
    """Return creation FILETIME, None if dead, or raise OSError if unverifiable."""
    if os.name == "nt":
        handle = kernel.OpenProcess(0x1000 | 0x100000, False, pid)
        if not handle:
            error = ctypes.get_last_error()
            if error == 87:
                return None
            raise ctypes.WinError(error)
        try:
            if kernel.WaitForSingleObject(handle, 0) == 0:
                return None
            values = [wintypes.FILETIME() for _ in range(4)]
            if not kernel.GetProcessTimes(handle, *(ctypes.byref(value) for value in values)):
                raise ctypes.WinError(ctypes.get_last_error())
            return (values[0].dwHighDateTime << 32) | values[0].dwLowDateTime
        finally:
            kernel.CloseHandle(handle)
    if sys.platform == "darwin":
        return mac_process_start_filetime(pid)
    if sys.platform.startswith("linux"):
        try:
            stat = Path("/proc/{}/stat".format(pid)).read_text()
            fields = stat[stat.rindex(")") + 2:].split()
            if fields[0] == "Z":
                return None
            ticks = int(fields[19])
            boot = next(int(line.split()[1]) for line in Path("/proc/stat").read_text().splitlines()
                        if line.startswith("btime "))
            return FILETIME_EPOCH + boot * 10000000 + ticks * 10000000 // os.sysconf("SC_CLK_TCK")
        except FileNotFoundError:
            return None
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return None
    raise OSError("Process creation-time validation is unavailable on this platform")


def process_state(pid, start):
    """Return alive/dead/unknown; PID reuse is dead for the old registration."""
    try:
        actual = process_start_filetime(pid)
        if actual is None:
            return "dead"
        tolerance = 10000000 // os.sysconf("SC_CLK_TCK") if sys.platform.startswith("linux") else 0
        return "alive" if abs(actual - int(start)) <= tolerance else "dead"
    except (OSError, ValueError):
        return "unknown"


def normalize_url(value):
    url = urllib.parse.urlsplit(value)
    if url.scheme != "http" or url.username or url.password or url.query or url.fragment:
        raise ValueError("baseUrl must be a loopback HTTP URL")
    host = url.hostname or ""
    if host != "localhost" and not ipaddress.ip_address(host).is_loopback:
        raise ValueError("baseUrl must be a loopback HTTP URL")
    if url.path not in ("", "/"):
        raise ValueError("baseUrl must not contain a path")
    port = url.port or 80
    if not 1 <= port <= 65535:
        raise ValueError("Invalid port")
    # localhost and IPv4 loopback denote the same service for conflict detection.
    host = "127.0.0.1" if host == "localhost" else host
    return "http://{}:{}".format("[{}]".format(host) if ":" in host else host, port)


@dataclass(frozen=True)
class Lease:
    process_id: int
    process_start: str
    project_path: str
    base_url: str
    executable: str
    arguments: tuple
    package_version: str

    @classmethod
    def identity_from_json(cls, data):
        if type(data.get("schemaVersion")) is not int or data["schemaVersion"] != 1:
            raise ValueError("Unsupported lease schema")
        pid = data["processId"]
        start = data["processStartFileTimeUtc"]
        if (type(pid) is not int or pid <= 0 or not isinstance(start, str) or
                not start.isdigit() or int(start) <= 0):
            raise ValueError("Invalid process identity")
        return pid, start

    @classmethod
    def from_json(cls, data):
        pid, start = cls.identity_from_json(data)
        args = data["arguments"]
        executable = data["executable"]
        if not isinstance(args, list) or not all(isinstance(arg, str) and "\0" not in arg for arg in args):
            raise ValueError("Invalid arguments")
        if not isinstance(executable, str) or not os.path.isabs(executable) or "\0" in executable:
            raise ValueError("Executable must be an absolute path")
        project = data["projectPath"]
        version = data["packageVersion"]
        if not isinstance(project, str) or not isinstance(version, str):
            raise ValueError("Invalid lease metadata")
        return cls(pid, start, project, normalize_url(data["baseUrl"]), executable, tuple(args), version)

    @property
    def identity(self):
        return self.process_id, self.process_start

    @property
    def command(self):
        return self.executable, self.arguments

    def status(self):
        return {"processId": self.process_id, "processStartFileTimeUtc": self.process_start,
                "projectPath": self.project_path, "baseUrl": self.base_url,
                "packageVersion": self.package_version}


class LeaseStore:
    def __init__(self, directory, probe=process_state):
        self.directory = directory
        self.probe = probe
        self.cache = {}
        self.released = {}
        self.uncertain = False

    def read(self):
        self.uncertain = False
        try:
            paths = list(self.directory.glob("*.json"))
        except OSError:
            self.uncertain = True
            paths = []
        for path in paths:
            try:
                data = json.loads(path.read_text(encoding="utf-8-sig"))
                identity = Lease.identity_from_json(data)
                if path.name != "{}-{}.json".format(*identity):
                    raise ValueError("Lease filename must match process identity")
                if data.get("released") is True:
                    self.cache.pop(path, None)
                    self.released[path] = identity
                    continue
                lease = Lease.from_json(data)
                self.released.pop(path, None)
                self.cache[path] = lease
            except (OSError, ValueError, KeyError, TypeError, AttributeError):
                # New partially-written files still reserve the process registration.
                try:
                    pid, start = path.stem.split("-", 1)
                    if not start.isdigit() or int(pid) <= 0 or int(start) <= 0:
                        continue
                    state = self.probe(int(pid), start)
                except (ValueError, OSError):
                    # A file without a valid identity cannot be an editor registration.
                    continue
                if state != "dead":
                    self.uncertain = True
                # Never delete malformed/read-failed files: the writer may be replacing them.
        live = []
        for path, identity in list(self.released.items()):
            if self.probe(*identity) == "dead":
                del self.released[path]
                try:
                    path.unlink(missing_ok=True)
                except OSError:
                    pass
        for path, lease in list(self.cache.items()):
            if self.probe(*lease.identity) == "dead":
                del self.cache[path]
                try:
                    path.unlink(missing_ok=True)
                except OSError:
                    pass
            else:
                live.append(lease)
        return live


class SingleInstance:
    """Hold the first byte of a persistent lock file for the complete lifecycle."""
    def __init__(self, path):
        self.path, self.stream = path, None

    def acquire(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.stream = self.path.open("a+b")
        self.stream.seek(0, 2)
        if self.stream.tell() == 0:
            self.stream.write(b"\0")
            self.stream.flush()
        self.stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
            return True
        except OSError:
            self.stream.close()
            self.stream = None
            return False

    def close(self):
        if self.stream:
            self.stream.close()
            self.stream = None


class OwnedProcess:
    """Own handles, never rediscover or kill processes by their listening port."""
    def __init__(self, executable, arguments):
        self.pid = None
        self.process = None
        self.job = None
        self.closed = False
        if os.name == "nt":
            self._start_windows(executable, arguments)
        else:
            self.process = subprocess.Popen(
                [sys.executable, str(Path(__file__).resolve()), "--guard-process", executable, *arguments],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                text=True, start_new_session=True)
            try:
                self.pid = int(self.process.stdout.readline())
                if self.pid <= 0:
                    raise ValueError("Invalid owned process ID")
            except (ValueError, OSError):
                self.process.stdin.close()
                self.process.wait(timeout=5)
                raise OSError(errno.EIO, "Could not start the owned MCP process") from None
            finally:
                self.process.stdout.close()

    def _start_windows(self, executable, arguments):
        info = PROCESS_INFORMATION()
        null = None
        self.job = kernel.CreateJobObjectW(None, None)
        if not self.job:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            limits = EXTENDED_LIMIT()
            limits.BasicLimitInformation.LimitFlags = 0x2000  # KILL_ON_JOB_CLOSE
            if not kernel.SetInformationJobObject(self.job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
                raise ctypes.WinError(ctypes.get_last_error())
            attributes = SECURITY_ATTRIBUTES(ctypes.sizeof(SECURITY_ATTRIBUTES), None, True)
            null = kernel.CreateFileW("NUL", 0xC0000000, 3, ctypes.byref(attributes), 3, 0, None)
            if null == ctypes.c_void_p(-1).value:
                null = None
                raise ctypes.WinError(ctypes.get_last_error())
            startup = STARTUPINFO()
            startup.cb = ctypes.sizeof(startup)
            startup.dwFlags = 0x100  # STARTF_USESTDHANDLES
            startup.hStdInput = startup.hStdOutput = startup.hStdError = null
            argv = ctypes.create_unicode_buffer(subprocess.list2cmdline([executable, *arguments]))
            if not kernel.CreateProcessW(executable, argv, None, None, True, 0x08000004,
                                         None, None, ctypes.byref(startup), ctypes.byref(info)):
                raise ctypes.WinError(ctypes.get_last_error())
            self.process, self.pid = info.hProcess, info.dwProcessId
            if not kernel.AssignProcessToJobObject(self.job, self.process):
                raise ctypes.WinError(ctypes.get_last_error())
            if kernel.ResumeThread(info.hThread) == 0xFFFFFFFF:
                raise ctypes.WinError(ctypes.get_last_error())
        except BaseException:
            if info.hProcess:
                kernel.TerminateProcess(info.hProcess, 1)
                kernel.CloseHandle(info.hProcess)
                self.process = None
            kernel.CloseHandle(self.job)
            self.job = None
            raise
        finally:
            if info.hThread:
                kernel.CloseHandle(info.hThread)
            if null:
                kernel.CloseHandle(null)

    def poll(self):
        if self.closed:
            return 0
        if os.name == "nt":
            if kernel.WaitForSingleObject(self.process, 0) == 0x102:
                return None
            code = wintypes.DWORD()
            kernel.GetExitCodeProcess(self.process, ctypes.byref(code))
            return code.value
        return self.process.poll()

    def close(self):
        if self.closed:
            return
        if os.name == "nt":
            kernel.CloseHandle(self.job)
            kernel.WaitForSingleObject(self.process, 5000)
            kernel.CloseHandle(self.process)
        else:
            if not self.process.stdin.closed:
                self.process.stdin.close()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                # Resume a paused guardian before requesting its cleanup handler.
                self.process.send_signal(signal.SIGCONT)
                self.process.terminate()
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    raise OSError(errno.ETIMEDOUT, "Owned process cleanup timed out") from None
        self.closed = True


def guard_process(command):
    """A private child group lives only while the supervisor's stdin pipe is open."""
    stopping = threading.Event()

    def watch_parent():
        # No input is sent. EOF is also delivered after SIGKILL of the supervisor.
        sys.stdin.buffer.read()
        stopping.set()

    for event in (signal.SIGINT, signal.SIGTERM):
        signal.signal(event, lambda *_: stopping.set())
    process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                               stderr=subprocess.DEVNULL, start_new_session=True)
    try:
        print(process.pid, flush=True)
        threading.Thread(target=watch_parent, daemon=True).start()
        while not stopping.wait(.1):
            # Do not reap the leader until its descendants are cleaned up. Its PID
            # reserves the group ID, so cleanup cannot signal a reused process group.
            if process_start_filetime(process.pid) is None:
                break
    finally:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait(timeout=5)
    return process.returncode if process.returncode >= 0 else 1


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def healthy(base_url, timeout=1):
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        with opener.open(base_url + "/health", timeout=timeout) as response:
            if response.status != 200:
                return False
            data = json.loads(response.read(65537))
            return data.get("status") == "healthy" and data.get("message") == HEALTH_MESSAGE
    except (OSError, ValueError, AttributeError, urllib.error.URLError):
        return False


def port_busy(base_url, timeout=0.5):
    url = urllib.parse.urlsplit(base_url)
    try:
        with socket.create_connection((url.hostname, url.port), timeout=timeout):
            return True
    except OSError:
        return False


@dataclass
class Service:
    base_url: str
    command: tuple
    process: object = None
    state: str = "waiting"
    error: str = None
    restart_count: int = 0
    failures: int = 0
    retry_at: float = 0
    started_at: float = 0
    healthy_since: float = None
    unhealthy_since: float = None
    ever_healthy: bool = False

    def status(self, now):
        return {"baseUrl": self.base_url, "state": self.state,
                "owned": self.process is not None,
                "pid": self.process.pid if self.process else 0,
                "error": self.error, "restartCount": self.restart_count,
                "nextRetryUtc": utc_timestamp(max(0, self.retry_at - now)) if self.retry_at > now else None}


class Supervisor:
    def __init__(self, state_dir, poll_interval=2, idle_seconds=10, startup_seconds=300,
                 health_grace=10, stable_seconds=30, clock=time.monotonic,
                 probe=process_state, health=healthy, busy=port_busy, spawn=OwnedProcess):
        self.state_dir = Path(state_dir)
        self.poll_interval, self.idle_seconds = poll_interval, idle_seconds
        self.startup_seconds, self.health_grace, self.stable_seconds = startup_seconds, health_grace, stable_seconds
        self.clock, self.health, self.busy, self.spawn = clock, health, busy, spawn
        self.store = LeaseStore(self.state_dir / "editors", probe)
        self.services = {}
        self.idle_since = None
        self.stopping = False
        self.logger = logging.getLogger("mcp-supervisor")

    def _failure(self, service, now, error):
        if service.process:
            service.process.close()
            service.process = None
        service.failures += 1
        service.retry_at = now + min(30, 2 ** min(service.failures, 5))
        service.healthy_since = service.unhealthy_since = None
        service.ever_healthy = False
        service.state, service.error = "backoff", error
        self.logger.warning("Service failed at %s: %s", service.base_url, error)

    def _service_tick(self, service, now):
        if service.process and service.process.poll() is not None:
            self._failure(service, now, "Owned MCP process exited")
            return
        if self.health(service.base_url):
            service.state, service.error = "healthy", None
            service.unhealthy_since = None
            service.ever_healthy = True
            if service.healthy_since is None:
                service.healthy_since = now
            if now - service.healthy_since >= self.stable_seconds:
                service.failures = 0
            return
        service.healthy_since = None
        if service.process:
            service.state = "starting" if not service.ever_healthy else "unhealthy"
            if service.unhealthy_since is None:
                service.unhealthy_since = now
            deadline = (service.started_at + self.startup_seconds if not service.ever_healthy
                        else service.unhealthy_since + self.health_grace)
            if now >= deadline:
                self._failure(service, now, "MCP health check timed out")
            return
        if now < service.retry_at:
            service.state = "backoff"
            return
        if self.busy(service.base_url):
            service.state, service.error = "blocked", "Port is occupied by a listener without a valid MCP health response"
            service.retry_at = now + min(30, max(2, self.poll_interval))
            return
        try:
            service.process = self.spawn(service.command[0], service.command[1])
            service.started_at = now
            service.unhealthy_since = None
            service.ever_healthy = False
            service.restart_count += 1
            service.state, service.error = "starting", None
            self.logger.info("Started owned MCP at %s (PID %s)", service.base_url, service.process.pid)
        except OSError as error:
            # Exception text may embed argv; only publish error type/code.
            self._failure(service, now, "MCP launch failed: {} ({})".format(type(error).__name__, error.errno))

    def tick(self):
        now = self.clock()
        leases = self.store.read()
        groups = {}
        for lease in leases:
            groups.setdefault(lease.base_url, []).append(lease)
        if leases or self.store.uncertain:
            self.idle_since = None
        elif self.idle_since is None:
            self.idle_since = now
        stopping = self.idle_since is not None and now - self.idle_since >= self.idle_seconds
        for base_url, registrations in groups.items():
            commands = {lease.command for lease in registrations}
            service = self.services.get(base_url)
            if not service:
                service = self.services[base_url] = Service(base_url, registrations[0].command)
            if len(commands) > 1 or service.command not in commands:
                if len(commands) > 1:
                    # Hold the existing service while contradictory sources remain.
                    # An exited parent still requires closing its owned child Job.
                    if service.process and service.process.poll() is not None:
                        service.process.close()
                        service.process = None
                    service.state, service.error = "conflict", "Editors requested different commands for the same endpoint; close or align their configurations"
                    continue
                # Only one requested configuration remains. This is an intentional
                # change or resolution, so switch once without retaining a stale owner.
                if service.process:
                    service.process.close()
                    service.process = None
                service.command = registrations[0].command
                service.failures, service.retry_at = 0, 0
                service.healthy_since = service.unhealthy_since = None
                service.ever_healthy = False
            self._service_tick(service, now)
        for base_url, service in list(self.services.items()):
            if base_url not in groups:
                if leases or stopping:
                    if service.process:
                        service.process.close()
                    del self.services[base_url]
                else:
                    service.state = "idle"
        if stopping:
            self.stopping = True
        try:
            atomic_json(self.state_dir / "status.json", {
                "schemaVersion": 1, "supervisorPid": os.getpid(), "updatedUtc": utc_timestamp(),
                "editors": [lease.status() for lease in leases],
                "services": [service.status(now) for service in self.services.values()],
                "state": "stopped" if stopping else "running",
            })
        except OSError:
            self.logger.warning("Could not publish status; retaining running services")
        return not stopping

    def close(self):
        for service in self.services.values():
            if service.process:
                try:
                    service.process.close()
                except OSError:
                    self.logger.warning("Could not close an owned service; continuing cleanup")

    def run(self):
        self.state_dir.mkdir(parents=True, exist_ok=True)
        (self.state_dir / "editors").mkdir(exist_ok=True)
        lock = SingleInstance(self.state_dir / "supervisor.lock")
        if not lock.acquire():
            return 0
        handler = logging.handlers.RotatingFileHandler(self.state_dir / "supervisor.log",
                                                       maxBytes=262144, backupCount=2, encoding="utf-8")
        handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
        self.logger.addHandler(handler)
        self.logger.setLevel(logging.INFO)
        try:
            while not self.stopping and self.tick():
                time.sleep(self.poll_interval)
            return 0
        finally:
            self.close()
            handler.close()
            self.logger.removeHandler(handler)
            lock.close()


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    if argv and argv[0] == "--guard-process" and os.name != "nt":
        try:
            return guard_process(argv[1:])
        except OSError:
            return 1
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state-dir", type=Path, required=True)
    parser.add_argument("--poll", type=float, default=2)
    args = parser.parse_args(argv)
    if not 0.1 <= args.poll <= 60:
        parser.error("--poll must be between 0.1 and 60 seconds")
    supervisor = Supervisor(args.state_dir, poll_interval=args.poll)
    for event in (signal.SIGINT, signal.SIGTERM):
        signal.signal(event, lambda *_: setattr(supervisor, "stopping", True))
    try:
        return supervisor.run()
    except OSError:
        # Do not print exception details that could contain sensitive paths/argv.
        return 1


if __name__ == "__main__":
    sys.exit(main())
