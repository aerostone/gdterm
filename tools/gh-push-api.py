#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""github.com 直连不可用时的推送兜底（走 api.github.com 的 Git Data API）。

为什么需要它：本机到 github.com:443 会周期性 TLS 握手失败（gnutls_handshake），
而 api.github.com 仍然可达，git push 就一直失败。此时用 Git Data API 手工造一个
commit：blob（改动的文件）→ tree（base_tree = 远端当前 tree）→ commit（parent = 远端
head）→ PATCH refs/heads/master。推送语义与 git push 等价（快进，force=False）。

用法（先本地 commit，再用它发布本地改动）：

    python3 tools/gh-push-api.py                 # 发布 HEAD 相对 HEAD~1 的改动
    python3 tools/gh-push-api.py --base <sha>    # 发布 <sha>..HEAD（跨多个 commit 时）

令牌取自 ~/.git-credentials 的 https://aerostone:<token>@github.com 行；
没有令牌直接报错退出（不读环境变量，避免误用别的身份）。
"""
import base64
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "aerostone/gdterm"
BRANCH = "master"


def token():
    path = os.path.expanduser("~/.git-credentials")
    if not os.path.exists(path):
        sys.exit("未找到 ~/.git-credentials（本脚本不读环境变量）")
    for line in open(path, encoding="utf-8"):
        m = re.match(r"https://aerostone:([^@]+)@github\.com", line.strip())
        if m:
            return m.group(1)
    sys.exit("~/.git-credentials 里没有 aerostone 的 github 令牌")


def api(tok, path, method="GET", body=None):
    req = urllib.request.Request(
        "https://api.github.com" + path,
        data=json.dumps(body).encode() if body is not None else None,
        method=method,
        headers={
            "Authorization": "token " + tok,
            "Accept": "application/vnd.github+json",
            "User-Agent": "gdterm-gh-push-api",
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=90) as r:
            return json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        sys.exit("API %s %s -> %s %s" % (method, path, e.code, e.read().decode()[:400]))


def git(*args, **kw):
    return subprocess.run(["git"] + list(args), check=True, capture_output=True, text=True, **kw).stdout


def main():
    base_arg = None
    argv = sys.argv[1:]
    if "--base" in argv:
        base_arg = argv[argv.index("--base") + 1]
    base = base_arg or "HEAD~1"

    tok = token()
    ref = api(tok, "/repos/%s/git/ref/heads/%s" % (REPO, BRANCH))["object"]["sha"]
    remote_tree = api(tok, "/repos/%s/git/commits/%s" % (REPO, ref))["tree"]["sha"]

    local_tree = git("rev-parse", "HEAD^{tree}").strip()
    if local_tree == remote_tree:
        print("远端 tree 与本地一致，没有需要发布的内容")
        return

    diff = git("diff", "--name-status", "%s..HEAD" % base).strip().splitlines()
    if not diff:
        sys.exit("%s..HEAD 没有改动，无法发布" % base)
    print("远端 head %s（tree 与本地不同）" % ref)
    print("待发布 %d 个文件" % len(diff))

    entries = []
    for line in diff:
        parts = line.split("\t")
        status, path = parts[0], parts[-1]
        if status.startswith("D"):
            entries.append({"path": path, "mode": "100644", "type": "blob", "sha": None})
            continue
        raw = open(path, "rb").read()
        blob = api(tok, "/repos/%s/git/blobs" % REPO, "POST",
                   {"content": base64.b64encode(raw).decode(), "encoding": "base64"})
        mode = "100755" if (os.stat(path).st_mode & 0o111) else "100644"
        entries.append({"path": path, "mode": mode, "type": "blob", "sha": blob["sha"]})
        print("  %-6s %s (%d B)" % (status, path, len(raw)))

    message = git("log", "-1", "--format=%B", "HEAD").strip()
    tree = api(tok, "/repos/%s/git/trees" % REPO, "POST", {"base_tree": remote_tree, "tree": entries})
    commit = api(tok, "/repos/%s/git/commits" % REPO, "POST",
                 {"message": message + "\n", "tree": tree["sha"], "parents": [ref]})
    api(tok, "/repos/%s/git/refs/heads/%s" % (REPO, BRANCH), "PATCH",
        {"sha": commit["sha"], "force": False})

    ok = api(tok, "/repos/%s/git/commits/%s" % (REPO, commit["sha"]))["tree"]["sha"] == local_tree
    print("已推送 %s（tree 与本地一致：%s）" % (commit["sha"], "是" if ok else "否 —— 请复查"))


if __name__ == "__main__":
    main()
