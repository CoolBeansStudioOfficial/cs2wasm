#!/bin/bash
# Run a branch's build and a command on the homelab (a second Windows machine reachable as `ssh homelab`), so heavy
# work (the suite, self-hosted builds, timings) does not compete with this machine's.
# usage: bash tools/homelab.sh <branch> [command...]     (default command: node tools/suite.mjs)
# The branch must be pushed to GitHub (git push origin <branch>). On the homelab each branch gets its own worktree
# (C:/Users/Levi/hl/<branch>), is built (src/Il2Wasm.Cli, Release), and the command runs there through the homelab's
# own heavy lock (tests/out/heavy.sh: one slot per checkout, two at once). The output comes back on stdout.
set -e
BR=${1:?branch}; shift
CMD=${*:-node tools/suite.mjs}
SAFE=$(echo "$BR" | tr '/' '_')
REMOTE=$(cat <<EOF
set -e
cd /c/Users/Levi/Documents/GitHub/cs2wasm
git fetch -q origin "+$BR:refs/hl/$SAFE"
C=\$(git rev-parse "refs/hl/$SAFE")
W=/c/Users/Levi/hl/$SAFE
if [ -d "\$W" ]; then cd "\$W"; git checkout -q --detach "\$C" 2>/dev/null || git -C /c/Users/Levi/Documents/GitHub/cs2wasm worktree remove --force "\$W"; fi
if [ ! -d "\$W" ]; then mkdir -p /c/Users/Levi/hl; git worktree add -q --detach "\$W" "\$C"; fi
cd "\$W"
git checkout -q --detach "\$C"
echo "[homelab] \$(git log --oneline -1 | cut -c1-80)"
dotnet build src/Il2Wasm.Cli -c Release -v q -nologo 2>&1 | grep -E " error |Elapsed" || true
bash /c/Users/Levi/Documents/GitHub/cs2wasm/tests/out/heavy.sh $CMD
EOF
)
# (the homelab's ssh shell is PowerShell: hand the script to Git Bash on stdin)
echo "$REMOTE" | ssh -o BatchMode=yes homelab '& "C:\Program Files\Git\bin\bash.exe" -l -s' 2>&1 | grep --line-buffered -v -E "post-quantum|store now, decrypt|server may need to be upgraded"
