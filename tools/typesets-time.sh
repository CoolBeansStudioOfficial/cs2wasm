#!/bin/bash
# (temporary, for the homelab: sut-simdcodecs compiled twice by this checkout's il2wasm-il with IL2WASM_TYPESETS_TRACE,
# the TypeSets solves' time against the compile's)
set -e
dotnet build src/Il2Wasm.IL.Cli -c Release -o artifacts/tt-cli -v q -nologo 2>&1 | grep -E " error |rror\(s\)" || true
[ -d artifacts/tt-fw ] || node tools/build-corelib.mjs --out artifacts/tt-fw | tail -1
DLL=$(ls -d "$LOCALAPPDATA"/il2wasm/netwasm/work-*/cases/sut-simdcodecs/bin/Release/net11.0/win-x86/case.dll | head -1)
echo "case: $DLL"
for i in 1 2; do
  start=$(date +%s%N)
  IL2WASM_TYPESETS_TRACE=1 dotnet artifacts/tt-cli/il2wasm-il.dll "$DLL" -o artifacts/tt.wasm --framework artifacts/tt-fw > /dev/null 2> artifacts/tt-trace.txt
  total=$(( ($(date +%s%N) - start) / 1000000 ))
  echo "run $i: compile $total ms; typesets: $(grep -E '^typesets: [0-9]' artifacts/tt-trace.txt | sed -E 's/.*; ([0-9]+) ms.*/\1/' | tr '\n' ' ')"
done
grep -E '^typesets: (phases|evaluate)' artifacts/tt-trace.txt | cut -c1-220
