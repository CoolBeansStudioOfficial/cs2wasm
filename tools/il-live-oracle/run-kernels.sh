#!/bin/bash
# Live objects per type at exit under .NET for hello world and tools/bench's kernels, each a program of its own
# (tools/bench/Programs.cs plus a Main that runs one kernel once and prints its checksum, the programs docs/S1-BARS.md
# measures), for tools/il-rcstats.mjs live --oracle and tools/s1-bars.mjs --oracles.
# HOMELAB ONLY (never .NET programs on the desk): bash tools/homelab.sh <branch> bash tools/il-live-oracle/run-kernels.sh [kernel...]
# Each result is printed as "== <name>" (hello, k_<kernel>) then its JSON line.
set -e
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
out=$(mktemp -d)
dotnet build -c Release "$here" -o "$out/hook" -v q -nologo 2>&1 | grep -E " error |rror\(s\)" || true
hook=$(cygpath -m "$out/hook/il-live-oracle.dll")
kernels=${*:-Tree DictionaryInts ListSort Linq WordCount Strings NBody Shapes LinkedNodes}
for k in hello $kernels; do
  if [ $k = hello ]; then name=hello; else name=k_$k; fi
  d="$out/$name"; mkdir -p "$d"
  printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><AssemblyName>%s</AssemblyName><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><Optimize>true</Optimize></PropertyGroup></Project>\n' $name > "$d/$name.csproj"
  if [ $k = hello ]; then printf 'class P { static void Main() { System.Console.WriteLine(42); } }\n' > "$d/Program.cs"
  else { cat "$repo/tools/bench/Programs.cs"; printf '\nstatic class KernelMain { static void Main() { System.Console.WriteLine(Programs.%s()); } }\n' $k; } > "$d/Program.cs"; fi
  dotnet build -c Release "$d" -o "$d/bin" -v q -nologo 2>&1 | grep -E " error " || true
  f=$(cygpath -m "$out/$name.json")
  (cd "$d/bin" && env DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_DbgJITDebugLaunchSetting=1 COMPlus_DbgJITDebugLaunchSetting=1 \
    DOTNET_STARTUP_HOOKS="$hook" IL2WASM_LIVE_OUT="$f" timeout 300 dotnet $name.dll > /dev/null) || echo "(exit $?)" >&2
  echo "== $name"
  if [ -f "$out/$name.json" ]; then cat "$out/$name.json"; echo; else echo "{\"error\":\"no output\"}"; fi
done
rm -rf "$out"
