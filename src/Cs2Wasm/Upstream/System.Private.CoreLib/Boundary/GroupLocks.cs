// cs2wasm runtime: the locks of the groups (arenas) of cycle-capable objects, in a program with threads.
#pragma warning disable CS0626, CS0436
using System;

namespace Cs2Wasm.Runtime
{
    public static partial class Rt
    {
        // ------------------------------------------------------------------
        // Group locks (threads). An arena operation (a store's link and unlink, a member's first or last outside
        // reference, a copy's link) locks the groups it touches, not the whole runtime: two threads that work on
        // structures of their own never wait for each other. A lock is one of `Stripes` words, each on a cache line of
        // its own; a group's is its stripe, kept in every member's lock word (LockWord) and in its arena's record
        // (+28), and an object that is no member is its own group, of the stripe its address gives (HashStripe).
        // Whoever changes what a member's lock word says (a join, a merge, a member that leaves) holds both stripes,
        // the old and the new, so a thread that holds a stripe and reads it in an object's lock word knows the
        // object is in a group it holds, and may read and change the object's words (GroupHeld).
        // Order: the runtime lock (G) before any stripe, stripes lowest first, then the places' locks (rt.store_ref),
        // then the heap's lock. A thread that holds stripes and needs another below them only tries it: a release
        // that cannot have it waits for the thread's locks to go (QueueRelease); the objects a store may join to a
        // group are found under the locks before anything changes (LinkLock).
        // Nothing of the program runs while a stripe is held: a destruction waits in the thread's lists, run when
        // its last stripe goes (AfterLocks), as with the runtime lock.
        // ------------------------------------------------------------------
        const int Stripes = 256, StripeBytes = 64, LockWord = 36;
        // The stripe of an object of a group of classes whose groups are each one thread's (Arenas.LocalScc, decided at
        // compile time: no object of those classes can be reached by another thread than the one that made it): no
        // word of the table, always the thread's own. Taken, it is only pushed (the thread's count of what it holds,
        // which the destructions wait for); below every stripe, it changes no order. CS2WASM_NO_LOCAL_GROUPS=1: none.
        const int LocalStripe = -2;
        // (the table: made before the first thread starts, Threads.SpawnCore; until then one thread runs and no
        // stripe is taken, though the thread's count of what it holds is kept)
        static int s_stripes;
        // per thread: the stack of what it took (stripe numbers; a reentrant take too), its size and room; the
        // releases that wait for its locks to go; the store whose container is locked from its link to its unlink
        [ThreadStatic] static int t_lkQ, t_lkN, t_lkCap, t_relQ, t_relN, t_relCap, t_regionC, t_regionSlot, t_regionMk, t_regionP, t_after;
        [Intrinsic] static extern int PlaceLockWord(int addr);
        // (LinkLock: the objects found, their number and room; the stripes that a try did not get)
        [ThreadStatic] static int t_walkQ, t_walkN, t_walkCap, t_missQ, t_missN;

        internal static void InitGroupLocks()
        {
            if (s_stripes != 0) return;
            int raw = Alloc(Stripes * StripeBytes + StripeBytes);
            int t = (raw + StripeBytes - 1) & ~(StripeBytes - 1);
            Mem.Fill(t, 0, Stripes * StripeBytes);
            Atomic.Store32(ref s_stripes, t);
        }

        /// <summary>How many locks of groups this thread holds (0: none). rt.destroy waits while it is not 0.</summary>
        public static int LocksHeld() => t_lkN;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static int HashStripe(int x) => (int)(((uint)x >> 3) * 2654435761u >> 24);

        /// <summary>The stripe of the object's group: its arena's, for a member; else its own.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static int KeyStripe(int x)
        {
            if (SccLocal(Mem.LoadI32(x + 4))) return LocalStripe;
            if (Atomic.Load32(ref Mem.RefI32(x)) < -1)
            {
                // (the lock word is in the member's block, before its header: 0 until it joins a group)
                int s = Mem.LoadI32(x - LockWord) - 1;
                if (s >= 0) return s & (Stripes - 1);
            }
            return HashStripe(x);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool StripeMine(int s)
        {
            int t = s_stripes;
            // (no other thread yet: every object is this thread's to read, as without threads; a group that is one
            // thread's, LocalStripe)
            if (t == 0 || s < 0) return true;
            return Atomic.Load32(ref Mem.RefI32(t + s * StripeBytes)) == ThreadId();
        }

        /// <summary>The object (a member) is in a group whose lock this thread holds.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool GroupHeld(int x) => StripeMine(KeyStripe(x));

        static int MaxHeld()
        {
            int m = -1;
            for (int i = 0; i < t_lkN; i++) { int s = Mem.LoadI32(t_lkQ + 4 * i); if (s > m) m = s; }
            return m;
        }

        static void LkPush(int s)
        {
            if (t_lkN == t_lkCap)
            {
                int cap = t_lkCap == 0 ? 32 : t_lkCap * 2;
                int nq = Alloc(cap * 4);
                if (t_lkQ != 0) { Mem.Copy(nq, t_lkQ, t_lkN * 4); Free(t_lkQ, t_lkCap * 4); }
                t_lkQ = nq; t_lkCap = cap;
            }
            Mem.StoreI32(t_lkQ + 4 * t_lkN, s);
            t_lkN++;
        }

        /// <summary>Takes stripe s (again, when it is this thread's). False, without it, only when it may not wait
        /// and another thread holds it.</summary>
        static bool StripeLock(int s, bool mayWait)
        {
            int t = s_stripes;
            if (t != 0 && s >= 0)
            {
                int w = t + s * StripeBytes, me = ThreadId();
                if (Atomic.Load32(ref Mem.RefI32(w)) == me) Mem.StoreI32(w + 4, Mem.LoadI32(w + 4) + 1);
                else
                {
                    if (Atomic.CompareExchange32(ref Mem.RefI32(w), 0, me) != 0)
                    {
                        if (!mayWait) return false;
                        StripeWait(w, me);
                    }
                    Mem.StoreI32(w + 4, 1);
                }
            }
            LkPush(s);
            return true;
        }

        // A stripe's words: [0] the thread that holds it, [4] how often (reentrant), [8] the threads asleep on it, [12]
        // the threads spinning for it. At most MaxSpinners spin; the others sleep, and an unlock wakes one only when
        // none spins (one spinning is there to take it). With every waiter spinning, a group many threads use (the
        // self-hosted compiler's one of Roslyn's nodes) had each of its operations wait on a lock word and group words
        // that moved between all the spinning cores at each handover: twelve threads 1.6x four threads' time.
        const int MaxSpinners = 2, SpinRounds = 4096;

        static void StripeWait(int w, int me)
        {
            int n = 0, total = 0;
            bool spinning = false;
            while (true)
            {
                int cur = Atomic.Load32(ref Mem.RefI32(w));
                if (cur == 0)
                {
                    if (Atomic.CompareExchange32(ref Mem.RefI32(w), 0, me) == 0) { if (spinning) Atomic.Add32(ref Mem.RefI32(w + 12), -1); return; }
                    continue;
                }
                if (DeadlockDump() && ++total > 2000000) Threads.LockTimeout(cur);
                if (!Threads.MayBlock()) continue;
                if (!spinning)
                {
                    // (a spinner's place, if one is free; else asleep until an unlock with none spinning, or a while)
                    if (Atomic.Load32(ref Mem.RefI32(w + 12)) < MaxSpinners)
                    {
                        if (Atomic.Add32(ref Mem.RefI32(w + 12), 1) < MaxSpinners) { spinning = true; n = 0; continue; }
                        Atomic.Add32(ref Mem.RefI32(w + 12), -1);
                    }
                }
                else if (++n < SpinRounds) continue;
                else { spinning = false; Atomic.Add32(ref Mem.RefI32(w + 12), -1); }
                Atomic.Add32(ref Mem.RefI32(w + 8), 1);
                // (no spinner left meanwhile, and none to wake us: one more look first)
                if (Atomic.Load32(ref Mem.RefI32(w + 12)) == 0 && Atomic.Load32(ref Mem.RefI32(w)) == 0) { Atomic.Add32(ref Mem.RefI32(w + 8), -1); continue; }
                Atomic.Wait32(ref Mem.RefI32(w), cur, 1000000);
                Atomic.Add32(ref Mem.RefI32(w + 8), -1);
            }
        }

        static void StripeUnlock(int s)
        {
            int t = s_stripes;
            if (t == 0 || s < 0) return;
            int w = t + s * StripeBytes;
            int d = Mem.LoadI32(w + 4) - 1;
            Mem.StoreI32(w + 4, d);
            if (d != 0) return;
            Atomic.Store32(ref Mem.RefI32(w), 0);
            if (Atomic.Load32(ref Mem.RefI32(w + 8)) != 0 && Atomic.Load32(ref Mem.RefI32(w + 12)) == 0) Atomic.Notify(ref Mem.RefI32(w), 1);
        }

        /// <summary>Gives back what was taken since `mark` (t_lkN then), newest first.</summary>
        static void ReleaseTo(int mark)
        {
            while (t_lkN > mark) { t_lkN--; StripeUnlock(Mem.LoadI32(t_lkQ + 4 * t_lkN)); }
        }

        /// <summary>An operation's locks given back: with the thread's last, what waited for it runs.</summary>
        static void GroupUnlock(int mark)
        {
            // (a member left with no reference waits in this thread's list, which no other thread looks in: destroyed
            // while its group is still held)
            if (s_killN != 0) DrainKills();
            ReleaseTo(mark);
            if (t_lkN == 0 && (t_relN | t_pendN | t_defN) != 0) AfterLocks();
        }

        /// <summary>The thread holds no group's lock: the releases that waited for that, then the destructions
        /// (rt.lock_exit runs them when the runtime lock is not held either).</summary>
        static void AfterLocks()
        {
            if (t_after != 0) return;
            t_after = 1;
            // (what a release here leaves waiting is taken by the loop: AfterLocks does not nest)
            bool more = true;
            while (more)
            {
                while (t_relN > 0)
                {
                    t_relN--;
                    Release(Borrow<object>(Mem.LoadI32(t_relQ + 4 * t_relN)));
                }
                more = false;
                // (not the counts a store gives back, AfterStore: those wait for the store to be written, its unlink)
                // (the destructions: rt.lock_exit runs them when the runtime lock is not this thread's, else its exit;
                // once: inside a destruction it leaves them to the destruction's own loop)
                if ((t_pendN != 0 || t_defN != 0) && !RuntimeLockHeld() && t_defRunning == 0) { RuntimeLock(); RuntimeUnlock(); more = t_relN != 0; }
            }
            t_after = 0;
        }

        /// <summary>A release that needs a group's lock this thread may not wait for: after its locks go.</summary>
        static void QueueRelease(int p)
        {
            if (t_relN == t_relCap)
            {
                int cap = t_relCap == 0 ? 16 : t_relCap * 2;
                int nq = Alloc(cap * 4);
                if (t_relQ != 0) { Mem.Copy(nq, t_relQ, t_relN * 4); Free(t_relQ, t_relCap * 4); }
                t_relQ = nq; t_relCap = cap;
            }
            Mem.StoreI32(t_relQ + 4 * t_relN, p);
            t_relN++;
        }

        /// <summary>Locks the group of x (nothing for 0). mayFail: false when the thread holds stripes above x's and
        /// another thread holds x's (nothing is taken then). Otherwise returns with x's group verified held.</summary>
        static bool LockGroupOf(int x, bool mayFail)
        {
            if (x == 0) return true;
            if (ShareCheck() && SccLocal(Mem.LoadI32(x + 4))) LocalGroupCheck(x);
            // (a loop with its result in a local: the compiler closes no endless loop of a function with a value)
            int got = -1;
            while (got < 0)
            {
                int s = KeyStripe(x);
                bool inOrder = t_lkN == 0 || StripeMine(s) || s > MaxHeld();
                if (!StripeLock(s, inOrder || !mayFail)) got = 0;
                else if (KeyStripe(x) == s) got = 1;
                else ReleaseTo(t_lkN - 1);
            }
            return got != 0;
        }

        /// <summary>Locks the groups of a and b, lowest stripe first (the thread holds none of a lower one, or holds
        /// them). Verified: when either moved to another group meanwhile, again.</summary>
        static void LockGroups2(int a, int b)
        {
            while (true)
            {
                int mark = t_lkN;
                int sa = a != 0 ? KeyStripe(a) : -1, sb = b != 0 ? KeyStripe(b) : -1;
                int lo = sa < sb ? sa : sb, hi = sa < sb ? sb : sa;
                if (lo >= 0) StripeLock(lo, true);
                if (hi >= 0 && hi != lo) StripeLock(hi, true);
                if ((a == 0 || KeyStripe(a) == sa) && (b == 0 || KeyStripe(b) == sb)) return;
                ReleaseTo(mark);
            }
        }

        /// <summary>Every stripe, lowest first (the thread holds none: LinkLock's last resort).</summary>
        static void LockAllGroups()
        {
            for (int s = 0; s < Stripes; s++) StripeLock(s, true);
        }

        // ---- The store's region: a store into a slot that can close a cycle, of an object another thread may
        // reach, is its link, the exchange and its unlink with the container's group locked throughout (StoreLockIf).
        // The lock is taken at the link, which knows the value too: both groups then, in order (LinkLock).

        // The slot's place lock is held too, from the link to the unlink: the exchange is an atomic one, without
        // rt.store_ref (whose place lock a copy of a range takes before it counts, rt.bulk_enter and rt.mask_enter,
        // and so before a group's lock). It is only tried while the groups are held: when another thread has it,
        // they are given back, and taken again once the place is free (no thread waits for a place holding a group).
        // StoreLockIf's result: 0 none, 1 the region (its exchange RegionExchange), 2 + mark a region inside one
        // (never seen: the container's group at once; its exchange rt.store_ref).

        /// <summary>A plain object in a static readonly field (ArenaPin): it never joins a group (MayJoin, ArenaLinkCore)
        /// and stays plain, so a store into it links nothing and its old value is a counted reference: no lock.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool PinnedPlain(int x) => Atomic.Load32(ref Mem.RefI32(x)) > 0 && Mem.LoadI32(x + 8) < 0;

        static int RegionEnter(int container, int slot)
        {
            if (PinnedPlain(container)) return 0;
            if (t_regionC != 0)
            {
                int mark = t_lkN;
                LockGroupOf(container, false);
                return mark + 2;
            }
            t_regionC = container;
            t_regionSlot = slot;
            t_regionMk = -1;
            return 1;
        }

        static void RegionExit(int took)
        {
            if (took == 0) return;
            if (took >= 2) { GroupUnlock(took - 2); return; }
            int mk = t_regionMk;
            t_regionC = 0;
            t_regionMk = -1;
            if (mk >= 0) { Atomic.Store32(ref Mem.RefI32(t_regionP), 0); ReleaseTo(mk); }
            if (t_lkN == 0 && (t_relN | t_pendN | t_defN) != 0) AfterLocks();
        }

        /// <summary>The store's region, at its link or its unlink (the first of them): the container's group and the
        /// value's (at the unlink, the container's: an old value of another group is read nothing of), then the place.</summary>
        static void RegionLock(int container, int val, bool link)
        {
            if (t_regionC != container || t_regionMk >= 0) return;
            if (t_backN != 0) AfterStore();
            int mark = t_lkN, place = PlaceLockWord(t_regionSlot);
            while (true)
            {
                if (link) LinkLock(val, container);
                else { LockGroupOf(container, false); t_linkOver = 0; }
                if (Atomic.CompareExchange32(ref Mem.RefI32(place), 0, 1) == 0) break;
                ReleaseTo(mark);
                while (Atomic.Load32(ref Mem.RefI32(place)) != 0) { }
            }
            t_regionP = place;
            t_regionMk = mark;
        }

        /// <summary>The exchange of a store in its region (generated code, when StoreLockIf gave 1); the region is
        /// locked now if neither its link nor its unlink has been (a link that had nothing to link).</summary>
        public static int RegionExchange(int addr, int val)
        {
            if (t_regionC != 0 && t_regionMk < 0) RegionLock(t_regionC, 0, false);
            return Atomic.Exchange32(ref Mem.RefI32(addr), val);
        }

        /// <summary>Interlocked.CompareExchange in a store's region, before its link (made only when the exchange
        /// happened): the region locked here, for the value's link too.</summary>
        public static int RegionCompareExchange(int addr, int expected, int val)
        {
            if (t_regionC != 0 && t_regionMk < 0) RegionLock(t_regionC, val, true);
            return Atomic.CompareExchange32(ref Mem.RefI32(addr), expected, val);
        }

        // ---- What a store may join to a group: the container and the value when either is a plain object, the
        // objects their slots hold, and so on (ArenaLinkJoined links them all, merging the groups of the members it
        // finds). Their locks are taken before anything changes: the two first, in order; then each object the walk
        // finds is tried, read only once its lock is held. A try that fails gives every lock back, and the next
        // attempt takes that stripe in order with the first two.

        // (LinkLock left the value out: a member into a plain container, whose words it did not lock; ArenaLink links
        // nothing then, for the value may have left its group meanwhile, TryLeave, and be plain by now)
        [ThreadStatic] static int t_linkOver;

        /// <summary>Locks what a link of val into container may touch; returns the mark to give it back to.</summary>
        static int LinkLock(int val, int container, bool contents = false)
        {
            int mark = t_lkN;
            t_linkOver = 0;
            // (no other thread yet: every object is this thread's, StripeMine; the two counted as held, no walk)
            if (s_stripes == 0)
            {
                if (val != 0) StripeLock(KeyStripe(val), true);
                if (container != 0) StripeLock(KeyStripe(container), true);
                return mark;
            }
            // (a container of a group of classes whose groups are each one thread's: so is the value, if it can be
            // linked at all; nothing another thread can touch, as before the first thread. CS2WASM_NO_LOCAL_GROUPS=1:
            // the walk and the locks)
            if (container != 0 && SccLocal(Mem.LoadI32(container + 4)))
            {
                if (ShareCheck()) { LocalGroupCheck(container); if (val != 0 && SccLocal(Mem.LoadI32(val + 4))) LocalGroupCheck(val); }
                LkPush(LocalStripe);
                return mark;
            }
            // (a value that cannot be on a cycle with the container is linked to nothing: CanLink; read nothing of)
            if (val != 0 && !CanLink(val, container)) val = 0;
            // (a member stored into a plain object stays a counted reference, the container plain: ArenaLinkCore. Only
            // the container's lock then, which keeps it plain meanwhile; nothing of the value is read. Checked again
            // under it: a container that became a member meanwhile is linked as any)
            int over = 0;
            if (!contents && val != 0 && Atomic.Load32(ref Mem.RefI32(container)) > 0 && Atomic.Load32(ref Mem.RefI32(val)) < -1) { over = val; val = 0; }
            t_missN = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (attempt > 0 && over != 0 && Atomic.Load32(ref Mem.RefI32(container)) <= 0) { val = over; over = 0; }
                int sa = val != 0 ? KeyStripe(val) : -1, sb = container != 0 ? KeyStripe(container) : -1;
                if (t_missN == 0)
                {
                    // (the usual case: the two, lowest first)
                    int lo = sa < sb ? sa : sb, hi = sa < sb ? sb : sa;
                    if (lo >= 0) StripeLock(lo, true);
                    if (hi >= 0 && hi != lo) StripeLock(hi, true);
                }
                else
                {
                    // the two and those a try missed before, lowest first
                    if (sa >= 0) MissPush(sa);
                    if (sb >= 0) MissPush(sb);
                    for (int i = 1; i < t_missN; i++)
                    {
                        int x = Mem.LoadI32(t_missQ + 4 * i), j = i - 1;
                        while (j >= 0 && Mem.LoadI32(t_missQ + 4 * j) > x) { Mem.StoreI32(t_missQ + 4 * (j + 1), Mem.LoadI32(t_missQ + 4 * j)); j--; }
                        Mem.StoreI32(t_missQ + 4 * (j + 1), x);
                    }
                    for (int i = 0; i < t_missN; i++) StripeLock(Mem.LoadI32(t_missQ + 4 * i), true);
                }
                if ((val == 0 || KeyStripe(val) == sa) && (container == 0 || KeyStripe(container) == sb)
                    && (over == 0 || Atomic.Load32(ref Mem.RefI32(container)) > 0))
                {
                    // (nothing joins a group unless the value or the container is a plain object: no walk then)
                    bool join = contents || val != 0 && (Atomic.Load32(ref Mem.RefI32(val)) > 0 || Atomic.Load32(ref Mem.RefI32(container)) > 0) && MayJoin(val, container);
                    if (!join || LinkWalk(val, container, contents)) { t_linkOver = over != 0 ? 1 : 0; return mark; }
                }
                ReleaseTo(mark);
            }
            // (a structure that keeps changing under the walk: every stripe, which nothing can miss)
            if (over != 0) val = over;
            LockAllGroups();
            LinkWalk(val, container, contents);
            return mark;
        }

        // (the objects found so far, as a set: a table of (object, walk) pairs, open addressing; a pair of an earlier
        // walk is an empty slot, so the table is never cleared. A list searched through made a walk over a large
        // structure of plain objects quadratic: test 133, 98 s)
        [ThreadStatic] static int t_wsTab, t_wsCap, t_wsGen;

        /// <summary>Puts o in the walk's set: false when it was there.</summary>
        static bool WalkAdd(int o)
        {
            int mask = t_wsCap - 1, i = (int)(((uint)o >> 3) * 2654435761u) & mask;
            while (true)
            {
                int e = t_wsTab + 8 * i;
                if (Mem.LoadI32(e + 4) != t_wsGen) { Mem.StoreI32(e, o); Mem.StoreI32(e + 4, t_wsGen); return true; }
                if (Mem.LoadI32(e) == o) return false;
                i = (i + 1) & mask;
            }
        }

        static void WalkPush(int o)
        {
            if (t_walkN * 2 >= t_wsCap)
            {
                // (a larger table: what the walk found so far, again)
                if (t_wsTab != 0) Free(t_wsTab, t_wsCap * 8);
                t_wsCap = t_wsCap == 0 ? 64 : t_wsCap * 2;
                t_wsTab = Alloc(t_wsCap * 8);
                Mem.Fill(t_wsTab, 0, t_wsCap * 8);
                for (int k = 0; k < t_walkN; k++) WalkAdd(Mem.LoadI32(t_walkQ + 4 * k));
            }
            if (!WalkAdd(o)) return;
            if (t_walkN == t_walkCap)
            {
                int cap = t_walkCap == 0 ? 32 : t_walkCap * 2;
                int nq = Alloc(cap * 4);
                if (t_walkQ != 0) { Mem.Copy(nq, t_walkQ, t_walkN * 4); Free(t_walkQ, t_walkCap * 4); }
                t_walkQ = nq; t_walkCap = cap;
            }
            Mem.StoreI32(t_walkQ + 4 * t_walkN, o);
            t_walkN++;
        }

        static void MissPush(int s)
        {
            if (t_missQ == 0) t_missQ = Alloc(Stripes * 4);
            for (int i = 0; i < t_missN; i++) if (Mem.LoadI32(t_missQ + 4 * i) == s) return;
            Mem.StoreI32(t_missQ + 4 * t_missN, s);
            t_missN++;
        }

        /// <summary>Under the locks of both: whether ArenaLinkCore can join either to a group, by the reasons it has not to
        /// (an object being destroyed, one whose finalizer runs: a finalizer's stores into its own dying structure
        /// walked all of it at each store, test 133; a pinned one; a group being destroyed).</summary>
        static bool MayJoin(int val, int container)
        {
            int cv = Atomic.Load32(ref Mem.RefI32(val)), ca = Atomic.Load32(ref Mem.RefI32(container));
            if (cv == -1 || cv == 0 || ca == -1 || ca == 0) return false;
            if (((PL(val, 24) | PL(container, 24)) & Dying) != 0) return false;
            if (ca > 0 && Mem.LoadI32(container + 8) < 0 || cv > 0 && Mem.LoadI32(val + 8) < 0) return false;
            if (ca < -1 && (Mem.LoadI32(ArenaOf(container) + 12) & ArenaBusy) != 0) return false;
            if (cv < -1 && (Mem.LoadI32(ArenaOf(val) + 12) & ArenaBusy) != 0) return false;
            return true;
        }

        // (the walk's verdict: 0 all held, 1 a try failed)
        [ThreadStatic] static int t_walkFail, t_walkFrom;

        /// <summary>Under the locks of the two: every object the link may join or merge with is in a group this thread
        /// holds (trying the stripe of each as it is found). False when a try failed (MissPush has it).</summary>
        static bool LinkWalk(int val, int container, bool contents)
        {
            t_walkN = 0; t_walkFail = 0;
            // (a new walk: the set's pairs of the last are empty slots; at the counter's wrap, the table cleared)
            if (++t_wsGen == 0) { t_wsGen = 1; if (t_wsTab != 0) Mem.Fill(t_wsTab, 0, t_wsCap * 8); }
            if (container != 0 && (contents || Atomic.Load32(ref Mem.RefI32(container)) > 0)) WalkPush(container);
            if (val != 0 && val != container && Atomic.Load32(ref Mem.RefI32(val)) > 0) WalkPush(val);
            for (int i = 0; i < t_walkN && t_walkFail == 0; i++)
            {
                int o = Mem.LoadI32(t_walkQ + 4 * i);
                int sa = s_visitArena, sm = s_visitMode, so = s_visitObj, se = s_visitElem;
                s_visitMode = 16; s_visitObj = o; s_visitElem = 0; t_walkFrom = SccOf(Mem.LoadI32(o + 4));
                ArenaVisitObject(o);
                s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_visitElem = se;
            }
            return t_walkFail == 0;
        }

        /// <summary>ArenaVisit's mode 16 (LinkWalk): a slot's object, in the group the link may bring it into.</summary>
        static void WalkVisit(int child)
        {
            if (child == 0 || t_walkFail != 0) return;
            int c = Atomic.Load32(ref Mem.RefI32(child));
            if (c == -1 || c == 0) return;
            if (t_walkFrom == 0 || SccOf(Mem.LoadI32(child + 4)) != t_walkFrom) return;
            // (a pinned object never joins, and stays pinned: the empty array every list starts with is one, which
            // every walk would lock)
            if (c > 0 && Mem.LoadI32(child + 8) < 0) return;
            int s = KeyStripe(child);
            if (!StripeMine(s))
            {
                if (!StripeLock(s, s > MaxHeld())) { MissPush(s); t_walkFail = 1; return; }
                if (KeyStripe(child) != s) { MissPush(s); t_walkFail = 1; return; }
            }
            // (a plain object joins and has its slots linked in turn; not one ArenaLinkCore leaves, as MayJoin)
            if (Atomic.Load32(ref Mem.RefI32(child)) > 0 && (PL(child, 24) & Dying) == 0) WalkPush(child);
        }

        // ---- The slow paths of the counts (generated: rt.retain, rt.release, rt.load_retain) ----

        /// <summary>A member's first outside reference (its count -2 when read): under its group's lock. 0 when the
        /// object was no member by then (counted again the usual way).</summary>
        public static int ArenaRetainSlow(int p)
        {
            int mark = t_lkN;
            LockGroupOf(p, false);
            int done = 0;
            // (a member ArenaRetain left with no reference waits in this thread's list: destroyed before the lock goes,
            // for no other thread looks in that list)
            if (Atomic.Load32(ref Mem.RefI32(p)) < -1) { ArenaRetain(p); done = 1; if (s_killN != 0) DrainKills(); }
            GroupUnlock(mark);
            return done;
        }

        /// <summary>A member's last outside reference: under its group's lock, or after this thread's locks go when
        /// that is a lock it may not wait for. 0 when the object was no member by then.</summary>
        public static int ArenaReleaseSlow(int p)
        {
            int mark = t_lkN;
            if (!LockGroupOf(p, true)) { QueueRelease(p); return 1; }
            int done = 0;
            if (Atomic.Load32(ref Mem.RefI32(p)) < -1) { ArenaRelease(p); done = 1; }
            GroupUnlock(mark);
            return done;
        }

        /// <summary>A read that counts, of a member with no outside reference: under its group's lock, where its
        /// members are freed, so what the place holds then, if a member of that group, is alive. 0: read again.</summary>
        public static int LoadRetainSlow(int addr)
        {
            int got = -1;
            while (got == -1)
            {
                int p = Atomic.Load32(ref Mem.RefI32(addr));
                if (p == 0) { got = 0; continue; }
                int c = Atomic.Load32(ref Mem.RefI32(p));
                if (c != -2)
                {
                    // (no longer so: the read that counts without a lock, rt.load_retain)
                    if (c == 0) continue;
                    if (c == -1) { if (Atomic.Load32(ref Mem.RefI32(addr)) == p) got = p; continue; }
                    // (a group's member whose finalizers are under way: counted only under the runtime lock, which the
                    // check after them holds, and only if the place still holds it: rt.load_retain, FinPhaseCuts)
                    if (c > 0 && (c & FinBias) != 0)
                    {
                        RuntimeLock();
                        if (Atomic.Load32(ref Mem.RefI32(addr)) == p && (Atomic.Load32(ref Mem.RefI32(p)) & FinBias) != 0 && Atomic.Load32(ref Mem.RefI32(p)) > 0) { Retain(Borrow<object>(p)); got = p; }
                        RuntimeUnlock();
                        continue;
                    }
                    if (Atomic.CompareExchange32(ref Mem.RefI32(p), c, c > 0 ? c + 1 : c - 1) != c) continue;
                    if (Atomic.Load32(ref Mem.RefI32(addr)) == p) { got = p; continue; }
                    Release(Borrow<object>(p));
                    continue;
                }
                int mark = t_lkN;
                int s = KeyStripe(p);
                StripeLock(s, true);
                if (Atomic.Load32(ref Mem.RefI32(addr)) == p && Atomic.Load32(ref Mem.RefI32(p)) < -1 && KeyStripe(p) == s)
                {
                    Retain(Borrow<object>(p));
                    got = p;
                }
                GroupUnlock(mark);
            }
            return got;
        }

        /// <summary>CS2WASM_SHARE_CHECK: an object of a group of classes whose groups are each one thread's (LocalStripe)
        /// that another thread made, locked here: a fault of the analysis (Arenas.LocalScc). A pinned one is any
        /// thread's (an array with no element, Rt.AllocArray).</summary>
        static unsafe void LocalGroupCheck(int x)
        {
            int c = Atomic.Load32(ref Mem.RefI32(x));
            if (c > 0 && Mem.LoadI32(x + 8) < 0) return;
            int maker = (Mem.LoadI32(x + 8) >> 24) & 0x1F;
            if (maker == (ThreadId() & 0x1F)) return;
            string m = "share check: a group's lock left out for an object of " + TypeInfo.Type(Mem.LoadI32(x + 4)).ToString() + " made by thread " + maker.ToString() + ", on thread " + ThreadId().ToString() + "\n";
            fixed (char* p = m) Engine.Write((int)p, m.Length);
            Mem.StoreI32(-4, 0);
        }

        /// <summary>A thread ends: its lists go.</summary>
        internal static void EndGroupLocks()
        {
            if (t_lkQ != 0) { Free(t_lkQ, t_lkCap * 4); t_lkQ = 0; t_lkCap = 0; }
            if (t_relQ != 0) { Free(t_relQ, t_relCap * 4); t_relQ = 0; t_relCap = 0; }
            if (t_walkQ != 0) { Free(t_walkQ, t_walkCap * 4); t_walkQ = 0; t_walkCap = 0; }
            if (t_wsTab != 0) { Free(t_wsTab, t_wsCap * 8); t_wsTab = 0; t_wsCap = 0; }
            if (t_missQ != 0) { Free(t_missQ, Stripes * 4); t_missQ = 0; }
            if (s_killQ != 0) { Free(s_killQ, s_killCap * 4); s_killQ = 0; s_killCap = 0; }
            if (s_linkQ != 0) { Free(s_linkQ, s_linkCap * 4); s_linkQ = 0; s_linkCap = 0; }
            if (s_cutQ != 0) { Free(s_cutQ, s_cutCap * 8); s_cutQ = 0; s_cutCap = 0; }
            if (s_trQ != 0) { Free(s_trQ, s_trCap * 4); s_trQ = 0; s_trCap = 0; }
            if (t_backQ != 0) { Free(t_backQ, t_backCap * 4); t_backQ = 0; t_backCap = 0; }
            if (t_defQ != 0) { Free(t_defQ, t_defCap * DefEntry); t_defQ = 0; t_defCap = 0; }
        }
    }
}
