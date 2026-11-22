#!/bin/bash
# What .NET itself prints for a test under load: the suite's runner, the .NET run alone (--oracle), PROCS processes at
# once, each RUNS times in turn; every distinct output with how often it came. For a test that fails under load:
# when .NET gives the other output too, the test asks what .NET does not promise.
#   bash tools/oracle-load.sh <filter[,filter..]> [dir]        (PROCS=8 RUNS=50; dir: tests/Load, the tests as they were, by default)
#   KEY=<regex>: told apart by the first match of the regex in the output alone (a test with lines that differ for
#   other reasons; the lines are joined by |)
#   on the homelab: bash tools/homelab.sh <branch> env PROCS=8 RUNS=50 bash tools/oracle-load.sh 127_
cd "$(dirname "$0")/.."
filter=${1:?filter}; dir=${2:-tests/Load}
dotnet build tests/Cs2Wasm.Tests -c Release -v q --nologo 2>&1 | grep -E " error " | head
# (several filters with commas: one after the other)
for f in ${filter//,/ }; do
tmp=$(mktemp -d)
for i in $(seq 1 ${PROCS:-8}); do
  dotnet run --project tests/Cs2Wasm.Tests -c Release --no-build -- --dir $dir $f --oracle ${RUNS:-50} > $tmp/$i.txt 2>&1 &
done
wait
# "ORACLE <name> <k> of <n>: <output, lines joined by |>"
cat $tmp/*.txt | grep "^ORACLE " | awk -v key="${KEY:-}" '{
  name = $2; k = $3; o = $0; sub(/^ORACLE [^ ]+ [0-9]+ of [0-9]+: /, "", o);
  if (key != "") { if (match(o, key)) o = substr(o, RSTART, RLENGTH); else o = "(no match)"; }
  id = name ": " o; count[id] += k; total[name] += k;
}
END { for (id in count) print count[id], id; for (n in total) print "total", n, total[n]; }' | sort -rn | cut -c1-400
grep -L "^ORACLE " $tmp/*.txt | while read f; do echo "no result in $f:"; tail -5 $f; done
rm -rf $tmp
done
