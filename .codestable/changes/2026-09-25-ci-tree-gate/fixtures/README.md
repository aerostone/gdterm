# 夹具说明（2026-09-25-ci-tree-gate）

| 文件 | 性质 | 期望结果 |
|---|---|---|
| `check-ci-gate.py` | 本地驱动（零依赖，stdlib） | `python3 check-ci-gate.py [ciDumpDir]` **必须 exit 0**；无参时默认读 `/tmp/ci327` |

覆盖四组、20 项检查：

- **S1 编码安全** 3 项：校验器无非 GBK 字符；断言有牙齿（含 `²` 的探针必须判非空）；驱动不留临时物
- **S2 口径不变** 2 项：对真实 CI dump 全量仍 `ALL OK`
- **S3 门结构** 8 项：调用存在、**在产物上传之后**、解析 python、`py -3` 回退、缺失即 throw（不静默跳过）、设 `PYTHONIOENCODING`/`PYTHONUTF8`、非零 throw、未侵入 `after_test`
- **S4 字节卫生/零依赖/未越界** 7 项：无 BOM、shebang 在 0、LF、可解析、仅 stdlib、未碰 `src/`、未改 `ui-check.py`

**本机无 dotnet / 无 PowerShell**，所以"门是否真的被执行"不由本驱动判定，而由真实 CI 仲裁：

- 全绿 → CI 日志里应出现校验器输出，且构建 success
- 若门生效，日志应在产物上传之后出现 `Running ui-tree-check gate` 与 `UI-TREE-CHECK ALL OK`

`appveyor.yml` 一改，`freerdp-bin` 缓存键即失效（键取自 `appveyor.yml` + `tools/build-freerdp.ps1`），下一次构建会重编 FreeRDP：更慢、更易抖动，属已接受的代价。
