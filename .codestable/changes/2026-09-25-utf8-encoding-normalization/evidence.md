---
doc_type: change-evidence
change: 2026-09-25-utf8-encoding-normalization
status: accepted
created: 2026-09-25
---

# 2026-09-25-utf8-encoding-normalization 执行证据

本文件是 [change.md](change.md) 的配套证据：记录可复跑的原始输出、逐文件字节比对与驱动明细。
设计意图、决策与验收结论仍以 `change.md` 为唯一口径来源。

## 执行证据

**状态口径** `kind: refactor` 的状态机是扁平的 `[draft, approved, in-progress, accepted, closed]`，
**没有** issue 那种 `phase` 字段（不写 `phase:`）。本包设计由助理自拟，依用户 standing 的
"请继续修改"＋完全 ACT 授权，直接从 `draft` 置 `in-progress`，不补形式上的 `approved` 流转。

**环境约束** 开发机无 `dotnet`/`mono`/`msbuild`，也无 `pwsh`/`powershell`，因此：
编译与 CI 门是否真跑起来**只能由后续 AppVeyor 实测仲裁**；本地可证伪的证据是
守卫本体、包内零依赖驱动、以及与 `HEAD` blob 的逐字节比对。

### Step 1 守卫本体（RED）

守卫按"默认纯 UTF-8、白名单例外"编辑后，先在 **`HEAD` 快照**上跑（`git archive HEAD` 导出，
再拷入守卫本体），得到权威 RED：

```
$ python3 /tmp/headsnap/tools/check-encoding.py /tmp/headsnap
encoding guard: 552 text files checked, 16 binary skipped (via walk)
  FAIL ... 18 行 unexpected UTF-8 BOM (should be plain UTF-8)
  FAIL tools/pack-release.ps1 : PowerShell 5.1 would read this as ANSI: non-ASCII .ps1 without BOM
ENCODING-CHECK FAIL: 19
exit=1
```

**为什么用快照而不是当场跑**：本会话第一次跑时，另一个脚本已先给 `MainFormSmoke.cs` 去掉了
BOM，于是当场只报 18 项。快照法给出的是可复现的权威数（19），不依赖会话中间态。

### Step 2 归一（GREEN）

以 `HEAD` blob 为完整性基准逐文件操作：`新内容 == git show HEAD:<f>` 去掉 3 字节前缀。
18 个文件各少 3 字节、`pack-release.ps1` 多 3 字节，**无一字节其他改动**：

```
$ git diff --numstat | 恰好 19 行 == "1  1   <path>"   # 每个文件只差首行
$ python3 tools/check-encoding.py
encoding guard: 551 text files checked, 16 binary skipped (via git)
ENCODING-CHECK ALL OK        exit=0
```

### Step 3 `.editorconfig` 与 CI 接线

`appveyor.yml` 仍是 LF、无 BOM，可被 `yaml.safe_load` 解析，`test_script` 仍是单块 `ps:`；
编码门追加在树检门之后，复用同一块的 `$py`/`$pyArgs` 与 `PYTHONIOENCODING`：

```
Running ui-tree-check gate -> $smokeDir   →  throw on non-zero
Running encoding guard                    →  throw on non-zero
```

### Step 4 包内零依赖驱动（42 项 ALL OK）

```
$ python3 .codestable/changes/2026-09-25-utf8-encoding-normalization/fixtures/check-utf8-normalization.py
UTF8-NORM-CHECK ALL OK over 42 checks    exit=0
```

其中 S2 是**牙齿证明**：在临时目录里合成 8 个样本，恰好报 3 处 FAIL
（不该带 BOM 的 `.md`、非法 UTF-8 字节、无 BOM 的非 ASCII `.ps1`），
并放过带 BOM 的非 ASCII `.ps1`、`third_party/` 下的 BOM、CI 夹具的 BOM 与二进制 `.dll`；
随后**给那个 `.ps1` 补上 BOM，该条 FAIL 随之消失**——证明规则按内容判定而非按文件名判定。

### 反向检查

- `git diff --numstat` 只有 19 个 `1/1` 文件（字节层）＋ `appveyor.yml`（唯一实质改动），
  与 `contract.include` 一致，无越界改动。
- 仓库 BOM 存量分布：`third_party/=70, ps1=2, fixtures/=2, lib/=1`，
  全部落在白名单内；项目自有 `.cs/.csproj/.md/.json` 无一带 BOM。

### 实施偏离

1. **D2 是减项修正**：初稿把两个 CI 产物夹具也列入"去 BOM"，实现时改为**保留**并加窄例外
   ——理由见 D2（夹具是 dumper 的逐字节产物，不该被本包改写）。
2. **`.ps1` 口径比 `.editorconfig` 更宽**：守卫"允许任何 `.ps1` 带 BOM、只对非 ASCII 强制"，
   故**未**给纯 ASCII 的 `tools/gen-version.ps1` 加 BOM（构建期由 csproj 以
   `powershell -File` 调用，ANSI 读法下字节相同）。`.editorconfig` 的
   `[*.ps1] charset = utf-8-bom` 更严，但守卫允许，二者不冲突（编辑器保存加 BOM 不会被判错）。
3. **白名单精度**：`/fixtures/` 是目录级许可，比 D2 描述的两个 `.json` 更粗；
   当前该目录下只有 2 个 `.json` 带 BOM、`README.md`/`.py` 无 BOM，故无违例，
   但规则本身许可更宽 —— 记为已知精度代价，不为此再加一层文件名匹配。
4. **驱动自身的两处缺陷**（已修）：S4 初稿写成"仓库 BOM 集合 == 期望集合"的等式，
   而守卫的规则是包含关系（带 BOM 必须被许可），等式会把"许可但未带 BOM"的 33 个文件误报；
   S3 初稿把 `appveyor.yml` 卷进字节比对。修正后改为由 git 的 `1/1` diff 自动界定字节层集合。

### 复现命令

```bash
python3 tools/check-encoding.py                                        # 守卫：ALL OK exit 0
python3 .codestable/changes/2026-09-25-utf8-encoding-normalization/fixtures/check-utf8-normalization.py
git archive HEAD | tar -x -C /tmp/headsnap && cp tools/check-encoding.py /tmp/headsnap/tools/ \
  && python3 /tmp/headsnap/tools/check-encoding.py /tmp/headsnap   # 权威 RED：FAIL 19 exit 1
```
