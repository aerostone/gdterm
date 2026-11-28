#!/usr/bin/env python3
"""Isolated OpenSSH fixture. Never edits system sshd, authorized_keys or known_hosts."""
import os
import pathlib
import pwd
import shutil
import socket
import subprocess
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
DOTNET = os.environ.get("GDT_DOTNET") or shutil.which("dotnet") or str(pathlib.Path.home() / "dotnet/dotnet")
SSHD = shutil.which("sshd") or "/usr/sbin/sshd"


def main():
    subprocess.run([DOTNET, "build", str(ROOT / "tools/ssh-jump-smoke/JumpSmoke.csproj"), "--nologo", "-v:q"], check=True)
    with tempfile.TemporaryDirectory(prefix="gdterm-jump-smoke-") as temp:
        folder = pathlib.Path(temp)
        folder.chmod(0o755)
        for name in ("host_key", "client_key"):
            subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(folder / name)], check=True)
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        config = folder / "sshd_config"
        config.write_text(f"""ListenAddress 127.0.0.1
Port {port}
HostKey {folder / 'host_key'}
PidFile {folder / 'sshd.pid'}
AuthorizedKeysFile {folder / 'client_key.pub'}
PasswordAuthentication no
ChallengeResponseAuthentication no
UsePAM no
StrictModes no
AllowUsers {pwd.getpwuid(os.getuid()).pw_name}
AllowTcpForwarding yes
PermitRootLogin prohibit-password
Subsystem sftp internal-sftp
LogLevel VERBOSE
""")
        prefix = ["sudo", "-n"] if os.environ.get("GDT_SSHD_SUDO") == "1" else []
        with (folder / "sshd.log").open("w+") as log:
            server = subprocess.Popen(prefix + [SSHD, "-D", "-e", "-f", str(config)], stdout=log, stderr=log)
            try:
                for attempt in range(100):
                    if server.poll() is not None:
                        log.seek(0)
                        raise RuntimeError("sshd fixture exited: " + log.read())
                    try:
                        with socket.create_connection(("127.0.0.1", port), timeout=.1):
                            break
                    except OSError:
                        time.sleep(.05)
                else:
                    raise TimeoutError("sshd fixture did not listen")
                subprocess.run([DOTNET, str(ROOT / "tools/ssh-jump-smoke/bin/Debug/net8.0/JumpSmoke.dll"),
                                str(port), str(folder / "client_key"), str(folder / "known_hosts"),
                                str(folder / "host_key.pub")], check=True, timeout=90)
            except Exception:
                log.flush()
                log.seek(0)
                print("Isolated sshd diagnostic:\n" + log.read(), flush=True)
                raise
            finally:
                if prefix and (folder / "sshd.pid").exists():
                    subprocess.run(prefix + ["kill", "-TERM", (folder / "sshd.pid").read_text().strip()], check=False)
                else:
                    server.terminate()
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait(timeout=5)
        print("Fixture stopped; temporary SSH keys removed on scope exit", flush=True)


if __name__ == "__main__":
    main()
