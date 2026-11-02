"""Engine runtime limits and leased MxZip-only temporary workspaces (no appcore import)."""
from __future__ import annotations

import collections
import os
import re
import sys
import threading


class _LazyModule:
    """Stands in for a module until its first attribute is used, then imports it and hands attributes through.
    Unlike importlib's LazyLoader it never enters sys.modules, so another module's plain `import bz2` does not
    count as a use, and creating it costs nothing."""
    __slots__ = ("_name", "_module")

    def __init__(self, name: str):
        object.__setattr__(self, "_name", name)
        object.__setattr__(self, "_module", None)

    def _load(self):
        module = self._module
        if module is None:
            import importlib
            module = importlib.import_module(self._name)
            object.__setattr__(self, "_module", module)
        return module

    def __getattr__(self, attr):
        return getattr(self._load(), attr)

    def __setattr__(self, attr, value):  # tests patch attributes of the real module
        setattr(self._load(), attr, value)

    def __delattr__(self, attr):
        delattr(self._load(), attr)

    def __repr__(self):
        return f"<lazy module {self._name!r}>"


def lazy_import(name: str):
    """The module, imported on first attribute access. For modules only some runs need (tarfile, bz2, gzip,
    hashlib, ...): together ~25 ms of every start of the command line tool. An already imported module is
    returned as is."""
    if name in sys.modules:
        return sys.modules[name]
    return _LazyModule(name)


shutil = lazy_import("shutil")
tempfile = lazy_import("tempfile")
uuid = lazy_import("uuid")

_lock = threading.RLock()
_threads = 0
_system_temp = None  # the system temp folder, read before configure() points tempfile elsewhere


def _system_temp_dir() -> str:
    global _system_temp
    if _system_temp is None:
        _system_temp = tempfile.gettempdir()
    return _system_temp
_sessions: dict[str, tuple[str, object]] = {}
_MARKER = b"MxZip-temp-session-v1\n"


def worker_count(default_cap: int | None = None) -> int:
    """CPU count limited by the user's ceiling and the engine's existing cap."""
    count = os.cpu_count() or 2
    if _threads:
        count = min(count, _threads)
    if default_cap is not None:
        count = min(count, default_cap)
    return max(1, count)


def _lease(file):
    file.seek(0)
    if os.name == "nt":
        import msvcrt
        msvcrt.locking(file.fileno(), msvcrt.LK_NBLCK, 1)
    else:
        import fcntl
        fcntl.flock(file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)


def _redirect(path: str) -> bool:
    return os.path.islink(path) or os.path.isjunction(path)


def validate(worker_threads: int = 0, temp_dir: str = "") -> str:
    """Probe the selected private root without changing threads, tempdir or leases.

    A not-yet-created directory is created so writability is actually tested.
    Returns its resolved MxZip root; all probe files are removed immediately.
    """
    if type(worker_threads) is not int or not 0 <= worker_threads <= 256:
        raise ValueError("worker_threads must be an integer from 0 to 256")
    if not isinstance(temp_dir, str) or "\0" in temp_dir:
        raise ValueError("temp_dir must be a path string")
    base = os.path.realpath(os.path.expanduser(temp_dir or _system_temp_dir()))
    root = os.path.join(base, "MxZip-temp")
    if _redirect(root):
        raise ValueError("MxZip-temp must not be a link or junction")
    os.makedirs(root, exist_ok=True)
    fd, probe = tempfile.mkstemp(prefix=".probe-", dir=root)
    os.close(fd)
    os.remove(probe)
    return root


def configure(worker_threads: int = 0, temp_dir: str = "") -> str:
    """Validate/create a writable session, then publish limits. Return temp path.

    Each session's marker remains exclusively locked for the process lifetime,
    including after switching roots, protecting open archives and background jobs.
    """
    global _threads
    _system_temp_dir()  # remember the system temp before this session becomes tempfile.tempdir
    root = validate(worker_threads, temp_dir)
    with _lock:
        if _redirect(root):
            raise ValueError("MxZip-temp must not be a link or junction")
        os.makedirs(root, exist_ok=True)
        if root not in _sessions:
            session = os.path.join(root, "session-" + uuid.uuid4().hex)
            os.mkdir(session)
            marker = os.path.join(session, ".MxZip-owner")
            file = None
            try:
                file = open(marker, "x+b")
                file.write(_MARKER)
                file.flush()
                _lease(file)
            except BaseException:
                if file is not None:
                    file.close()
                if os.path.isfile(marker):
                    os.remove(marker)
                os.rmdir(session)
                raise
            _sessions[root] = session, file
        session, _ = _sessions[root]
        # Probe again even for a previously configured directory.
        fd, probe = tempfile.mkstemp(prefix=".probe-", dir=session)
        os.close(fd)
        os.remove(probe)
        _threads = worker_threads
        tempfile.tempdir = session
        return session


def cleanup_temp(*, dry_run: bool = False, paths=None) -> dict | list[str]:
    """Delete only marked, unlocked session-UUID trees below configured roots.

    Never deletes a root, user-selected directory, unknown file, or redirected
    tree. Active sessions (including this process) are always skipped. Returns
    counts {removed, skipped, errors}; removed counts session directories.
    dry_run lists eligible paths; paths restricts deletion to that exact list,
    with ownership, containment, redirection and lease checks repeated at use.
    """
    result = {"removed": 0, "skipped": 0, "errors": 0}
    candidates = []
    if isinstance(paths, (str, bytes, os.PathLike)):
        raise ValueError("paths must be a collection of session paths")
    with _lock:
        selected = None if paths is None else {os.path.normcase(os.path.abspath(p)) for p in paths}
        for root in _sessions:
            if _redirect(root) or not os.path.isdir(root):
                continue
            try:
                names = os.listdir(root)
            except OSError:
                result["errors"] += 1
                continue
            for name in names:
                if not re.fullmatch(r"session-[0-9a-f]{32}", name):
                    continue
                path = os.path.join(root, name)
                if selected is not None and os.path.normcase(os.path.abspath(path)) not in selected:
                    continue
                owner = os.path.join(path, ".MxZip-owner")
                if _redirect(path) or not os.path.isdir(path) or _redirect(owner):
                    result["skipped"] += 1
                    continue
                if any(path == s[0] for s in _sessions.values()):
                    result["skipped"] += 1
                    continue
                try:
                    with open(owner, "r+b") as file:
                        _lease(file)
                        if file.read(len(_MARKER) + 1) != _MARKER:
                            result["skipped"] += 1
                            continue
                        redirected = any(_redirect(os.path.join(d, n))
                                         for d, dirs, files in os.walk(path, followlinks=False)
                                         for n in dirs + files)
                        if redirected:
                            result["skipped"] += 1
                            continue
                        if dry_run:
                            candidates.append(path)
                            continue
                        # Claim only a verified abandoned session.
                        claimed = os.path.join(root, "deleting-" + uuid.uuid4().hex)
                        # Windows forbids renaming an open marker's parent directory;
                        # all participating processes honor this marker lock, and
                        # only abandoned sessions reach here.
                    os.rename(path, claimed)
                    # Verify the resolved final deletion target immediately before
                    # recursive removal. Only an immediate child of this root is owned.
                    resolved = os.path.realpath(claimed)
                    if (_redirect(claimed) or os.path.normcase(os.path.dirname(resolved)) !=
                            os.path.normcase(os.path.realpath(root))):
                        result["skipped"] += 1
                        continue
                    shutil.rmtree(claimed)
                    result["removed"] += 1
                except (PermissionError, BlockingIOError):
                    result["skipped"] += 1
                except OSError:
                    result["errors"] += 1
    return candidates if dry_run else result


class CancelledError(Exception):
    """result() of a job cancelled before it started."""


class _Future:
    __slots__ = ("_done", "_state", "_value")

    def __init__(self):
        self._done = threading.Event()
        self._state = "pending"  # pending -> running -> finished | cancelled
        self._value = None

    def cancel(self) -> bool:
        with _pool_lock:
            if self._state != "pending":
                return self._state == "cancelled"
            self._state = "cancelled"
        self._done.set()
        return True

    def cancelled(self) -> bool:
        return self._state == "cancelled"

    def done(self) -> bool:
        return self._done.is_set()

    def result(self, timeout=None):
        if not self._done.wait(timeout):
            raise TimeoutError()
        if self._state == "cancelled":
            raise CancelledError()
        if isinstance(self._value, _Raised):
            raise self._value.exc
        return self._value


class _Raised:
    __slots__ = ("exc",)

    def __init__(self, exc):
        self.exc = exc


_pool_lock = threading.Lock()


class _PoolState:
    """What worker threads share with their pool. Workers never reference the pool object itself, so a pool
    that is dropped without shutdown() is collected and its idle threads end (as in concurrent.futures)."""
    __slots__ = ("queue", "cond", "idle", "closed")

    def __init__(self):
        self.queue = collections.deque()
        self.cond = threading.Condition()
        self.idle = 0
        self.closed = False


def _pool_worker(state: _PoolState):
    while True:
        with state.cond:
            while not state.queue and not state.closed:
                state.idle += 1
                state.cond.wait()
                state.idle -= 1
            if not state.queue:
                return
            future, fn, args, kwargs = state.queue.popleft()
        with _pool_lock:
            runnable = future._state == "pending"
            if runnable:
                future._state = "running"
        if runnable:
            try:
                future._value = fn(*args, **kwargs)
            except BaseException as e:  # handed to result(), like concurrent.futures
                future._value = _Raised(e)
            future._state = "finished"
            future._done.set()
        del future, fn, args, kwargs  # keep nothing of a finished job alive (it may own open files)


class ThreadPoolExecutor:
    """The part of concurrent.futures.ThreadPoolExecutor MxZip uses (submit / map / shutdown / with, futures with
    result / cancel), built on threading alone. Importing concurrent.futures pulls in logging, traceback and
    inspect: 20-50 ms of every run that starts a pool, which was most of the gap to the third-party archiver on small archives.
    Threads start on demand up to max_workers (daemon threads)."""

    def __init__(self, max_workers=None, thread_name_prefix=""):
        self._max = max(1, max_workers or worker_count())
        self._prefix = thread_name_prefix or "MxZip-pool"
        self._state = _PoolState()
        self._threads = []

    def submit(self, fn, *args, **kwargs):
        future = _Future()
        st = self._state
        with st.cond:
            if st.closed:
                raise RuntimeError("cannot schedule new futures after shutdown")
            st.queue.append((future, fn, args, kwargs))
            if st.idle == 0 and len(self._threads) < self._max:
                t = threading.Thread(target=_pool_worker, args=(st,), daemon=True,
                                     name=f"{self._prefix}_{len(self._threads)}")
                self._threads.append(t)
                t.start()
            else:
                st.cond.notify()
        return future

    def map(self, fn, *iterables):
        futures = [self.submit(fn, *args) for args in zip(*iterables)]

        def results():
            try:
                for f in futures:
                    yield f.result()
            finally:
                for f in futures:
                    f.cancel()
        return results()

    def shutdown(self, wait=True, cancel_futures=False):
        st = self._state
        with st.cond:
            st.closed = True
            if cancel_futures:
                while st.queue:
                    st.queue.popleft()[0].cancel()
            st.cond.notify_all()
        if wait:
            for t in self._threads:
                if t is not threading.current_thread():
                    t.join()

    def __del__(self):  # dropped without shutdown(): let idle workers finish the queue and end
        st = getattr(self, "_state", None)
        if st is not None:
            with st.cond:
                st.closed = True
                st.cond.notify_all()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.shutdown(wait=True)
        return False
