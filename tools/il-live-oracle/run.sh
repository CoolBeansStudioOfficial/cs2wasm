#!/bin/bash
# Live objects per type at exit under .NET, for tools/il-rcstats.mjs live --oracle (docs/MEMORY-IR.md 9.2).
# HOMELAB ONLY (never .NET cases on the desk): bash tools/homelab.sh <branch> bash tools/il-live-oracle/run.sh <case id>...
# Each case of the NetWasm suite's cache (IL2WASM_NETWASM_CACHE, else %LOCALAPPDATA%/il2wasm/netwasm; the newest work-*
# directory) runs its built case.exe with the startup hook; each result is printed as "== <id>" then its JSON line.
# IL2WASM_LIVE_DEBUG=1: the event payload shapes on stderr.
set -e
here=$(cd "$(dirname "$0")" && pwd)
out=$(mktemp -d)
dotnet build -c Release "$here" -o "$out/hook" -v q -nologo 2>&1 | grep -E " error |rror\(s\)" || true
cache=${IL2WASM_NETWASM_CACHE:-$LOCALAPPDATA/il2wasm/netwasm}
work=$(ls -d "$cache"/work-* | tail -1)
hook=$(cygpath -m "$out/hook/il-live-oracle.dll")
for id in "$@"; do
  bin="$work/cases/$id/bin/Release/net11.0/win-x86"
  if [ ! -f "$bin/case.exe" ]; then echo "== $id"; echo "{\"error\":\"no build\"}"; continue; fi
  f=$(cygpath -m "$out/$id.json")
  # (the suite's oracle environment: its x86 runtime, no just-in-time debugger)
  root=$(cygpath -w "$cache/dotnet-win-x86")
  (cd "$bin" && env DOTNET_ROOT_X86="$root" "DOTNET_ROOT(x86)=$root" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_DbgJITDebugLaunchSetting=1 COMPlus_DbgJITDebugLaunchSetting=1 \
    DOTNET_STARTUP_HOOKS="$hook" IL2WASM_LIVE_OUT="$f" timeout 300 ./case.exe > /dev/null) || echo "(exit $?)" >&2
  echo "== $id"
  if [ -f "$out/$id.json" ]; then cat "$out/$id.json"; echo; else echo "{\"error\":\"no output\"}"; fi
done
rm -rf "$out"
