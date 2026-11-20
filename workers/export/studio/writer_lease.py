"""Cooperating project writer lease, shared with the C# backend session."""
import os
from contextlib import contextmanager


class WriterBusyError(OSError):
    pass


@contextmanager
def writer_lease(path):
    # Keep the inode: removing a lock file allows two writers to lock different files.
    stream = None
    try:
        stream = open(path, 'a+b')
        stream.seek(0)
        if os.name == 'nt':
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.lockf(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB, 1)
    except OSError as error:
        if stream is not None:
            stream.close()
        raise WriterBusyError('Проект принадлежит другому writer. Завершите его сессию перед передачей записи.') from error
    try:
        yield
    finally:
        stream.close()
