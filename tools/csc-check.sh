#!/usr/bin/env bash
# csc-check.sh——Linux 无 MSBuild 时的本地 C# 编译验证（旧式 net462 csproj 的替代检查）。
#
# 背景（2026-11-02 沉淀，CI 0.1.345-347 三连红的教训）：
#   本机无 .NET Framework SDK，旧式工程 dotnet build 不接 PackageReference，
#   曾导致"本地无法验证 → 提交 → CI 才报 CS 错误"的多轮返工。
#   本脚本用便携 dotnet SDK 内置的 Roslyn csc 一次性编译全部 src 工程源码
#   （含 Gdterm.Tests）+ 外部 DLL 引用，能抓：类型缺失、命名空间错误、
#   重名冲突、语法错误。抓不到：csproj Compile 清单与磁盘不一致（脚本按目录枚举 .cs，
#   与 csproj 无关）——因此编译通过后会再跑 tools/check-csproj-files.py 兜住
#   "多登记(CS2001)/少登记(CS0246)"两类只在 Windows msbuild 才炸的问题。
#
# 依赖：~/dotnet/dotnet（便携 SDK，arm64）
#       ~/.nuget/packages/microsoft.netframework.referenceassemblies.net462/1.0.3
#       ~/.nuget/packages/ssh.net/2026.0.0  ~/.nuget/packages/keepasslib/2.30.0
#
# 用法：tools/csc-check.sh            # 全量
#       tools/csc-check.sh <子目录>…  # 仅语法级快速检查（仍全量引用，但只编给定目录的 .cs）
set -u
cd "$(dirname "$0")/.."

DOTNET="${HOME}/dotnet/dotnet"
CSC="${HOME}/dotnet/sdk/8.0.404/Roslyn/bincore/csc.dll"
REF="${HOME}/.nuget/packages/microsoft.netframework.referenceassemblies.net462/1.0.3/build/.NETFramework/v4.6.2"
NUGET="${HOME}/.nuget/packages"

if [ ! -x "$DOTNET" ] || [ ! -f "$CSC" ] || [ ! -d "$REF" ]; then
  echo "csc-check: 缺少便携 SDK / csc.dll / net462 参考程序集（见脚本头注释）" >&2
  exit 2
fi

# 外部程序集
EXTRA_REFS="
  lib/AntdUI.dll
  lib/VtNetCore.dll
  ${NUGET}/ssh.net/2026.0.0/lib/net462/Renci.SshNet.dll
  ${NUGET}/keepasslib/2.30.0/lib/KeePassLib.dll
"
for f in $EXTRA_REFS; do
  [ -f "$f" ] || { echo "csc-check: 缺外部 DLL: $f" >&2; exit 2; }
done

# 参考程序集（按需全拉，csc 只解析实际用到的）
FRAME_REFS=""
for a in mscorlib System System.Core System.Data System.Drawing System.Windows.Forms \
         System.Web.Extensions System.IO.Compression System.IO.Compression.FileSystem \
         System.Xml System.Xml.Linq System.Net.Http System.Management Microsoft.CSharp; do
  [ -f "$REF/$a.dll" ] && FRAME_REFS="$FRAME_REFS -r:$REF/$a.dll"
done

# 源码集
if [ "$#" -gt 0 ]; then
  SRC_DIRS=("$@")
else
  SRC_DIRS=()
  for d in src/*/; do
    [ -d "$d" ] && SRC_DIRS+=("${d%/}")
  done
fi

SOURCES=""
for d in "${SRC_DIRS[@]}"; do
  while IFS= read -r f; do
    SOURCES="$SOURCES $f"
  done < <(find "$d" -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" \
           -not -path "*/Properties/AssemblyInfo.cs" -not -name "AssemblyInfo.cs")
done

OUT=$(mktemp -d /tmp/csc-check.XXXXXX)
trap 'rm -rf "$OUT"' EXIT

echo "csc-check: $(echo $SOURCES | wc -w) 个源文件 → $OUT/check.dll"
# shellcheck disable=SC2086
"$DOTNET" exec "$CSC" \
  /nologo /nostdlib /noconfig /t:library /langversion:7.3 \
  /nowarn:CS0618,CS0414,CS0219,CS8019 \
  /out:"$OUT/check.dll" \
  $FRAME_REFS \
  -r:lib/AntdUI.dll -r:lib/VtNetCore.dll \
  -r:"${NUGET}/ssh.net/2026.0.0/lib/net462/Renci.SshNet.dll" \
  -r:"${NUGET}/keepasslib/2.30.0/lib/KeePassLib.dll" \
  $SOURCES 2>&1 | tee "$OUT/errors.txt"

ERRS=$(grep -c "error" "$OUT/errors.txt" || true)
if [ "$ERRS" -eq 0 ]; then
  echo "csc-check: OK（无编译错误）"
  # 编译通过不等于 CI 通过：本脚本按目录枚举源码，csproj 里的 Compile 清单
  # 多一条（CS2001）或少一条（CS0246）它都看不见，这里补上。
  python3 tools/check-csproj-files.py || exit 1
  exit 0
fi
echo "csc-check: $ERRS 个错误" >&2
exit 1
