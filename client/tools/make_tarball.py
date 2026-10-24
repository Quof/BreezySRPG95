"""A Linux or macOS release archive from a folder: a .tar.gz with the executable bits set, which zip and rar files made on
Windows lose.

    python client/tools/make_tarball.py <folder> <out.tar.gz> --exec BreezySRPG95

Everything in <folder> goes in under the folder's own name (BreezySRPG/...), owned by root: folders and the files named
after --exec 0755, the rest 0644. Names are UTF-8 (pax), so Japanese file names survive. The members are listed after.
"""
import argparse
import os
import tarfile
import time


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('folder')
    ap.add_argument('out')
    ap.add_argument('--exec', nargs='*', default=[], dest='executables', help='file names to mark executable')
    a = ap.parse_args()
    root = os.path.abspath(a.folder)
    base = os.path.basename(root.rstrip('/\\'))
    now = int(time.time())

    def member(name, path=None):
        ti = tarfile.TarInfo(name)
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = 'root'
        ti.mtime = now
        if path is None:
            ti.type, ti.mode = tarfile.DIRTYPE, 0o755
        else:
            ti.size = os.path.getsize(path)
            ti.mode = 0o755 if os.path.basename(path) in a.executables else 0o644
        return ti

    missing = [e for e in a.executables if not os.path.isfile(os.path.join(root, e))]
    if missing:
        raise SystemExit(f'not in {root}: {", ".join(missing)}')
    with tarfile.open(a.out, 'w:gz', format=tarfile.PAX_FORMAT) as t:
        t.addfile(member(base))
        for dirpath, dirs, files in os.walk(root):
            dirs.sort()
            rel = os.path.relpath(dirpath, root).replace(os.sep, '/')
            prefix = base if rel == '.' else f'{base}/{rel}'
            for d in dirs:
                t.addfile(member(f'{prefix}/{d}'))
            for f in sorted(files):
                p = os.path.join(dirpath, f)
                with open(p, 'rb') as fh:
                    t.addfile(member(f'{prefix}/{f}', p), fh)
    with tarfile.open(a.out) as t:
        for m in t.getmembers():
            print(f'{m.mode:04o} {m.uname}:{m.gname} {m.size:>10} {m.name}')
    print(a.out, os.path.getsize(a.out), 'bytes')


if __name__ == '__main__':
    main()
