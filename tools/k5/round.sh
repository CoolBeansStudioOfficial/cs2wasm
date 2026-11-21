#!/bin/bash
# One heavy-lock round. usage: round.sh [build:<name>]... [naot] [prof:<name>] [profnaot] [time:<n>:<name,name>] [pp:<n>:<a.wasm>:<b.wasm>] [micro:<a>:<b>]
# Self-host builds: compiler k/cli-<name>, sources k/m-src (main 3810a98+), no threads, named.
K=/c/Users/Levi/Documents/GitHub/cs2wasm/tests/out/self2/k
S=/c/Users/Levi/AppData/Local/Temp/claude/3ccce5aebe9475ed/294999ef-5a13-4d5e-9833-31e9bb447660/scratchpad
WT=/c/Users/Levi/Documents/GitHub/cs2wasm/.claude/worktrees/stream-k-codegen-08b888
W=/Users/Levi/Documents/GitHub/cs2wasm/tests/out/self2
REF=$(ls -d "/c/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/"*/ref/net11.0 | tail -1)
for step in "$@"; do
  echo "== $step $(date +%T)"
  case "$step" in
    build:*) n=${step#build:}; ev=""; if [[ "$n" == *:* ]]; then ev=${n#*:}; n=${n%%:*}; ev=${ev//+/ }; fi; cli=${CLI:-$n}; s=$(date +%s)
      (cd $K/m-src && env $ev CS2WASM_ROSLYN_DIR=$K/cli-$cli CS2WASM_NO_THREADS=1 CS2WASM_NAMES=1 CS2WASM_TIMES=1 DOTNET_GCHeapHardLimit=0x380000000 node tools/selfhost.mjs $K/cli-$cli/cs2wasmc.exe $K/$n.wasm --pathmap "C:/Users/Levi/Documents/GitHub/cs2wasm/tests/out/self2/k/m-src=/src" > $K/$n.log 2>&1)
      echo "built $n in $(( $(date +%s) - s )) s: $(stat -c %s $K/$n.wasm) bytes" ;;
    naot) (cd $K/m-src && EMSDK='C:\tools\emsdk' dotnet publish tools/selfhost-naot-wasm -c Release -o 'C:\Users\Levi\Documents\GitHub\cs2wasm\tests\out\self2\k\naotm' > $K/naotm-build.log 2>&1); tail -1 $K/naotm-build.log ;;
    prof:*) n=${step#prof:}; rm -rf $K/prof-$n
      (cd $WT && CS2WASM_PROCESSORS=1 SELF=$W/k/$n.wasm bash tools/selfrun.sh --no-wasm-lazy-compilation --cpu-prof --cpu-prof-dir=tests/out/prof-$n -- $W/w42.cs -o $W/k/w42-$n.wasm 2>&1 | tail -1; rm -rf $K/prof-$n; mv tests/out/prof-$n $K/prof-$n) ;;
    profnaot) rm -rf $K/pnaotm
      (cd $K/naotm && MSYS_NO_PATHCONV=1 CS2WASM_CACHE=/c/Users/Levi/AppData/Local/cs2wasm CS2WASM_REFERENCE_DIR="$REF" node --cpu-prof --cpu-prof-dir='C:\Users\Levi\Documents\GitHub\cs2wasm\tests\out\self2\k\pnaotm' cs2wasmc-naot.js /c$W/w42.cs -o /c$W/k/w42nm.wasm 2>&1 | tail -1) ;;
    time:*) r=${step#time:}; rounds=${r%%:*}; names=${r#*:}
      powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\Users\Levi\AppData\Local\Temp\claude\3ccce5aebe9475ed\294999ef-5a13-4d5e-9833-31e9bb447660\scratchpad\cpuab.ps1' -Rounds $rounds -Names $names -Naot naotm ;;
    pp:*) r=${step#pp:}; IFS=: read -r rounds a b <<< "$r"; bash $S/ppab.sh $rounds $K/$a $K/$b ;;
    micro:*) r=${step#micro:}; IFS=: read -r a b <<< "$r"; node $S/microab.mjs 5 $K/$a $K/$b C:/Users/Levi/Documents/GitHub/cs2wasm/tests/out/micro/naot-wasm | tail -12 ;;
    suite) (cd $WT && node tools/suite.mjs 2>&1 | tail -6) ;;
    suite:*) ev=${step#suite:}; ev=${ev//+/ }; (cd $WT && env $ev node tools/suite.mjs 2>&1 | tail -12) ;;
    bench:*) ev=${step#bench:}; ev=${ev//+/ }; [ "$ev" = "-" ] && ev=""; (cd $WT && env $ev node tools/bench.mjs 2>&1 | tail -25) ;;
    ftime:*) r=${step#ftime:}; IFS=: read -r rounds n fsets <<< "$r"; IFS=';' read -ra FS <<< "$fsets"
      for q in $(seq 1 $rounds); do for fl in "${FS[@]}"; do f=${fl//+/ }; [ "$f" = "-" ] && f=""
        t0=$(date +%s%N); (cd $WT && CS2WASM_PROCESSORS=1 SELF=$W/k/$n.wasm bash tools/selfrun.sh $f -- $W/w42.cs -o $W/k/w42-$n.wasm > /dev/null 2>&1); echo "$n [$fl] $(( ($(date +%s%N)-t0)/1000000 )) ms"
        t0=$(date +%s%N); (cd $K/naotm && MSYS_NO_PATHCONV=1 CS2WASM_CACHE=/c/Users/Levi/AppData/Local/cs2wasm CS2WASM_REFERENCE_DIR="$REF" node $f cs2wasmc-naot.js /c$W/w42.cs -o /c$W/k/w42nm.wasm > /dev/null 2>&1); echo "naot [$fl] $(( ($(date +%s%N)-t0)/1000000 )) ms"
      done; done ;;
    ctrace:*) n=${step#ctrace:}
      (cd $WT && CS2WASM_PROCESSORS=1 SELF=$W/k/$n.wasm bash tools/selfrun.sh --trace-wasm-compilation-times -- $W/w42.cs -o $W/k/w42-$n.wasm > $K/ctrace-$n.txt 2>&1); node $S/ctrace.mjs $K/ctrace-$n.txt
      (cd $K/naotm && MSYS_NO_PATHCONV=1 CS2WASM_CACHE=/c/Users/Levi/AppData/Local/cs2wasm CS2WASM_REFERENCE_DIR="$REF" node --trace-wasm-compilation-times cs2wasmc-naot.js /c$W/w42.cs -o /c$W/k/w42nm.wasm > $K/ctrace-naot.txt 2>&1); echo naot:; node $S/ctrace.mjs $K/ctrace-naot.txt ;;
    tinl:*) n=${step#tinl:}
      (cd $WT && CS2WASM_PROCESSORS=1 SELF=$W/k/$n.wasm bash tools/selfrun.sh --trace-wasm-inlining -- $W/w42.cs -o $W/k/w42-$n.wasm > $K/tinl-$n.txt 2>&1); wc -l $K/tinl-$n.txt
      (cd $K/naotm && MSYS_NO_PATHCONV=1 CS2WASM_CACHE=/c/Users/Levi/AppData/Local/cs2wasm CS2WASM_REFERENCE_DIR="$REF" node --trace-wasm-inlining cs2wasmc-naot.js /c$W/w42.cs -o /c$W/k/w42nm.wasm > $K/tinl-naot.txt 2>&1); wc -l $K/tinl-naot.txt ;;
    mcompile:*) n=${step#mcompile:}; for m in $K/$n.wasm $K/naotm/cs2wasmc-naot.wasm; do node --single-threaded --liftoff-only --no-wasm-lazy-compilation $S/compile.mjs $m 2; done ;;
    quiet) for q in $(seq 1 40); do l=$(powershell -NoProfile -Command "(Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 5 -MaxSamples 6 | ForEach-Object { \$_.CounterSamples.CookedValue } | Measure-Object -Maximum).Maximum" 2>/dev/null | tr -d '
' | cut -d. -f1); echo "load max ${l:-?}%"; [ -n "$l" ] && [ "$l" -lt 30 ] && break; done ;;
  esac
done
echo "== done $(date +%T)"
