#!/bin/bash
# The host's snapshot (host/snapshot.mjs, IL2WASM_SNAPSHOT) on a small module with state that lasts between calls of
# its entry (tools/snapshot-check.cs): what is kept, and each rule that lets go of a snapshot. Prints each step's
# output and compares the whole with what is expected; exits 1 when it differs.
#   bash tools/snapshot-check.sh          (from the checkout's root; needs src/Il2Wasm.Cli built, Release)
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
D=tests/out/snapshot-check
rm -rf $D; mkdir -p $D/lib
src/Il2Wasm.Cli/bin/Release/net11.0/il2wasm.exe tools/snapshot-check.cs -o $D/m.wasm 2> $D/build.txt || { cat $D/build.txt; exit 1; }
echo one > $D/lib/a.txt; echo two > $D/lib/b.txt; echo bad > $D/lib/fail.txt
export IL2WASM_FS_ROOT="$(pwd -W 2>/dev/null || pwd)" IL2WASM_SNAPSHOT=$D/m.snap IL2WASM_SNAPSHOT_WHY=1 SNAP_MODE=m1
r() { node host/run.mjs $D/m.wasm main -- "$@" 2>&1 | sed -E 's/ in [0-9]+ ms//; s/[0-9]+ pages \([0-9]+ (not zero|written)\)/N pages/; s/\(SNAP_[A-Z, _]*\)/(...)/'; echo "exit ${PIPESTATUS[0]}"; }
{
echo "--- 1: no snapshot: from the start, written"; r
echo "--- 2: on the snapshot: the second call"; r
echo "--- 3: a file the command line names is read every time and is no dependency: nothing new, not written"; r /$D/lib/a.txt
echo "--- 4: so still the second call"; r
echo "--- 5: the named file changed: the snapshot holds"; echo one-changed > $D/lib/a.txt; r /$D/lib/a.txt
echo "--- 6: another environment: from the start"; SNAP_MODE=m2 r
echo "--- 7: and on its snapshot"; SNAP_MODE=m2 r
export SNAP_FILE=/$D/lib/b.txt
echo "--- 8: a file not named (the environment differs too: from the start): read, kept, a dependency"; r
echo "--- 9: on the snapshot: kept, not read"; r
echo "--- 10: that file changed: the snapshot is let go, read again"; echo three > $D/lib/b.txt; r
echo "--- 11: on the new one"; r
echo "--- 12: an entry that returns 1: nothing written"; r /$D/lib/fail.txt
echo "--- 13: the module changed: from the start"; printf '\0asm' > /dev/null; cp $D/m.wasm $D/m2.wasm; node -e "
const fs=require('fs');const b=fs.readFileSync('$D/m.wasm');const n=Buffer.from([0,5,4,115,110,97,112]);fs.writeFileSync('$D/m.wasm',Buffer.concat([b,n]));"; r
echo "--- 14: a snapshot cut short is not used: from the start"; node -e "
const fs=require('fs');const b=fs.readFileSync('$D/m.snap');fs.writeFileSync('$D/m.snap',b.subarray(0,b.length-70000));"; r
echo "--- 15: on a snapshot, a file read that it does not cover (no path the command line names): written again"; r keep=/$D/lib/a.txt
echo "--- 16: on that one: both kept"; r
export IL2WASM_SNAPSHOT_KEEP=1
echo "--- 17: IL2WASM_SNAPSHOT_KEEP=1 (another environment: from the start)"; r
echo "--- 18: a file it does not cover is read, and the snapshot is not written again"; r keep=/$D/lib/a.txt
echo "--- 19: so it is as step 17 left it"; r
} > $D/out.txt 2>&1
cat $D/out.txt
cat > $D/expected.txt <<EOF
--- 1: no snapshot: from the start, written
call 1, mode m1
call 1 on the error stream
[snapshot] written: N pages, 0 paths (0 new)
exit 0
--- 2: on the snapshot: the second call
[snapshot] restored N pages and 7 globals; 0 paths checked
call 2, mode m1
call 2 on the error stream
exit 0
--- 3: a file the command line names is read every time and is no dependency: nothing new, not written
[snapshot] restored N pages and 7 globals; 0 paths checked
given /$D/lib/a.txt: one
call 2, mode m1
call 2 on the error stream
exit 0
--- 4: so still the second call
[snapshot] restored N pages and 7 globals; 0 paths checked
call 2, mode m1
call 2 on the error stream
exit 0
--- 5: the named file changed: the snapshot holds
[snapshot] restored N pages and 7 globals; 0 paths checked
given /$D/lib/a.txt: one-changed
call 2, mode m1
call 2 on the error stream
exit 0
--- 6: another environment: from the start
[snapshot] not used: another environment (...)
call 1, mode m2
call 1 on the error stream
[snapshot] written: N pages, 0 paths (0 new)
exit 0
--- 7: and on its snapshot
[snapshot] restored N pages and 7 globals; 0 paths checked
call 2, mode m2
call 2 on the error stream
exit 0
--- 8: a file not named (the environment differs too: from the start): read, kept, a dependency
[snapshot] not used: another environment (...)
read /$D/lib/b.txt
kept /$D/lib/b.txt: two
call 1, mode m1
call 1 on the error stream
[snapshot] written: N pages, 1 paths (1 new)
exit 0
--- 9: on the snapshot: kept, not read
[snapshot] restored N pages and 7 globals; 1 paths checked
kept /$D/lib/b.txt: two
call 2, mode m1
call 2 on the error stream
exit 0
--- 10: that file changed: the snapshot is let go, read again
[snapshot] not used: changed: $(node -e "console.log(require('path').join(process.env.IL2WASM_FS_ROOT,'$D','lib','b.txt'))")
read /$D/lib/b.txt
kept /$D/lib/b.txt: three
call 1, mode m1
call 1 on the error stream
[snapshot] written: N pages, 1 paths (1 new)
exit 0
--- 11: on the new one
[snapshot] restored N pages and 7 globals; 1 paths checked
kept /$D/lib/b.txt: three
call 2, mode m1
call 2 on the error stream
exit 0
--- 12: an entry that returns 1: nothing written
[snapshot] restored N pages and 7 globals; 1 paths checked
kept /$D/lib/b.txt: three
given /$D/lib/fail.txt: bad
call 2, mode m1
call 2 on the error stream
exit 0
--- 13: the module changed: from the start
[snapshot] not used: another module
read /$D/lib/b.txt
kept /$D/lib/b.txt: three
call 1, mode m1
call 1 on the error stream
[snapshot] written: N pages, 1 paths (1 new)
exit 0
--- 14: a snapshot cut short is not used: from the start
[snapshot] not used: cut short
read /$D/lib/b.txt
kept /$D/lib/b.txt: three
call 1, mode m1
call 1 on the error stream
[snapshot] written: N pages, 1 paths (1 new)
exit 0
--- 15: on a snapshot, a file read that it does not cover (no path the command line names): written again
[snapshot] restored N pages and 7 globals; 1 paths checked
read /$D/lib/a.txt
kept /$D/lib/a.txt: one-changed
kept /$D/lib/b.txt: three
call 2, mode m1
call 2 on the error stream
[snapshot] written: N pages, 2 paths (1 new)
exit 0
--- 16: on that one: both kept
[snapshot] restored N pages and 7 globals; 2 paths checked
kept /$D/lib/a.txt: one-changed
kept /$D/lib/b.txt: three
call 3, mode m1
call 3 on the error stream
exit 0
--- 17: IL2WASM_SNAPSHOT_KEEP=1 (another environment: from the start)
[snapshot] not used: another environment (IL2WASM_SNAPSHOT_KEEP)
read /$D/lib/b.txt
kept /$D/lib/b.txt: three
call 1, mode m1
call 1 on the error stream
[snapshot] written: N pages, 1 paths (1 new)
exit 0
--- 18: a file it does not cover is read, and the snapshot is not written again
[snapshot] restored N pages and 7 globals; 1 paths checked
read /$D/lib/a.txt
kept /$D/lib/a.txt: one-changed
kept /$D/lib/b.txt: three
call 2, mode m1
call 2 on the error stream
[snapshot] not written again (IL2WASM_SNAPSHOT_KEEP): 1 paths it does not cover were read
exit 0
--- 19: so it is as step 17 left it
[snapshot] restored N pages and 7 globals; 1 paths checked
kept /$D/lib/b.txt: three
call 2, mode m1
call 2 on the error stream
exit 0
EOF
if diff <(tr -d '\r' < $D/out.txt) <(tr -d '\r' < $D/expected.txt) > $D/diff.txt; then echo "snapshot-check: as expected"; else echo "snapshot-check: DIFFERS"; cat $D/diff.txt; exit 1; fi
