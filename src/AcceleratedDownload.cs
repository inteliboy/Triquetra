// ============================================================
// AcceleratedDownload.cs
// Opt-in hedged multi-mirror downloader, scoped down from our
// DeploymentTool project's `updater\Program.AcceleratedDownload.cs`
// (~2000 lines, read in full before this port - not re-derived from
// memory). Same core mechanics: split a large file into fixed-size
// chunks, fetch them over several parallel ranged HTTP connections
// split across two mirrors by measured speed, and "hedge" any chunk
// whose owner is running significantly behind by starting a
// redundant fetch on the other mirror - whichever side finishes
// first wins, the loser is discarded. A single writer thread commits
// completed chunks to disk strictly in order.
//
// Deliberately NOT ported from DeploymentTool: checkpoint/resume
// across process restarts (`.accel-checkpoint`). That exists there
// because it writes to flaky USB media in WinPE with real mid-
// transfer crash/reboot risk; Triquetra writes to a normal running
// Windows install and its existing non-accelerated path has never
// had resume either. An interrupted accelerated download just
// restarts from scratch next time - same as today's behavior.
//
// Also simplified from DeploymentTool: no explicit byte-compare of
// the overlapping range when owner and hedge both finish in a near-
// tie (DeploymentTool does this since a "both genuinely finished"
// race needs a tiebreaker before anyone can trust either buffer).
// Here, only one side's data ever reaches the writer for a given
// byte range - a hedge win takes the owner's own already-fetched
// PREFIX (bytes it truly finished before the hedge even started,
// never touched again afterward) plus the hedge's own TAIL fetch,
// written as two separate sequential writes - never splicing bytes
// between two buffers that could still be concurrently written to,
// which avoids the data race a splice-into-shared-buffer approach
// would have. Genuine mirror byte-level disagreement is still
// caught - just later, by the mandatory whole-file hash check at
// the end of every attempt, not per-chunk during hedging.
//
// Entry point mirrors Net.DownloadFile's shape: returns false (never
// throws for an ordinary ineligibility/failure reason) whenever the
// caller should just fall back to Net.DownloadFile instead - not
// eligible, mirrors disagree on identity, or every restart attempt
// failed.
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace Triquetra
{
    static class AcceleratedDownload
    {
        const long   AccelMinBytes                     = 100L * 1024 * 1024;
        const int    AccelChunkSize                     = 16 * 1024 * 1024;
        // internal, not private: Program.cs reads this to size the
        // process-wide ServicePointManager.DefaultConnectionLimit bump
        // at startup (see the long comment there for why).
        internal const int AccelTotalConnections         = 8;
        const int    AccelMaxRetriesPerChunk             = 2;
        const int    AccelMaxConsecutiveServerFailures   = 3;
        const int    AccelTimeoutMs                      = 8000;
        const int    AccelStallSeconds                   = 15;
        const int    AccelMaxRestartAttempts             = 3;
        const int    AccelHedgeMinElapsedMs              = 750;
        const long   AccelHedgeMinRemainingBytes         = 2L * 1024 * 1024;
        const double AccelHedgeEtaFactor                 = 1.75;
        const int    AccelMaxConcurrentHedges            = 1;
        const int    AccelMonitorPollMs                  = 250;

        // Measured once per app session (first accelerated download),
        // reused afterward so a 2-3-file install run doesn't re-probe
        // speed for every file.
        static bool   _speedProbed;
        static double _mainSpeedBps;
        static double _mirrorSpeedBps;
        static readonly object _speedLock = new object();

        public static bool TryDownload(string url, string destDir,
            Action<long, long, double> progressCb, out string destPath)
        {
            string result = null;
            bool ok;
            try { ok = TryDownloadCore(url, destDir, progressCb, out result); }
            catch (Exception ex)
            {
                Logger.Log("Accelerated download aborted (falling back): " + ex.Message);
                ok = false;
            }
            destPath = ok ? result : null;
            return ok;
        }

        static bool TryDownloadCore(string url, string destDir,
            Action<long, long, double> progressCb, out string destPath)
        {
            destPath = null;
            // AppConfig.BaseUrl/MirrorUrl are guaranteed to end in exactly
            // one "/" (normalized on assignment - see AppConfig.cs), so no
            // TrimEnd/re-append dance is needed here.
            string baseUrl = AppConfig.BaseUrl;
            string mirrorBase = AppConfig.MirrorUrl;
            if (string.IsNullOrEmpty(mirrorBase)) return false;
            if (!url.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase)) return false;
            string mirrorUrl = mirrorBase + url.Substring(baseUrl.Length);

            string fname = Uri.UnescapeDataString(url.Split('/').Last());
            string candidatePath = Path.Combine(destDir, fname);

            long     mainSize, mirrorSize;
            DateTime mainModified, mirrorModified;
            if (!Head(url, out mainSize, out mainModified)) return false;
            if (mainSize < AccelMinBytes) return false;
            if (!Head(mirrorUrl, out mirrorSize, out mirrorModified)) return false;
            if (mainSize != mirrorSize ||
                Math.Abs((mainModified - mirrorModified).TotalSeconds) > 2)
            {
                Logger.Log("Accelerated download: mirror disagrees on size/timestamp - skipping.");
                return false;
            }

            Net.RemoteHash mainHash;
            try { mainHash = Net.FetchRemoteHash(url); }
            catch { Logger.Log("Accelerated download: no hash sidecar on main - skipping."); return false; }
            string mirrorSidecar = mirrorUrl + (mainHash.Algorithm == "SHA256" ? ".sha256" : ".md5");
            string mirrorHash;
            try
            {
                mirrorHash = mainHash.Algorithm == "SHA256"
                    ? Net.FetchSha256(mirrorSidecar) : Net.FetchMd5(mirrorSidecar);
            }
            catch { Logger.Log("Accelerated download: mirror has no matching hash sidecar - skipping."); return false; }
            if (!mainHash.Hash.Equals(mirrorHash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("Accelerated download: mirrors disagree on file hash - skipping.");
                return false;
            }

            if (File.Exists(candidatePath) &&
                Net.FileHash(candidatePath, mainHash.Algorithm).Equals(mainHash.Hash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log(fname + ": cached copy OK (" + mainHash.Algorithm + "), skipping download.");
                destPath = candidatePath;
                return true;
            }

            EnsureSpeedProbed(baseUrl, mirrorBase);

            Logger.Log(string.Format(
                "Accelerated download: {0} ({1:F1} MB) across 2 mirrors, hedge budget {2}%.",
                fname, mainSize / 1048576.0, AppConfig.HedgeBudgetPercent));

            // Whether main/mirror is skipped (0 workers) THIS attempt - once
            // an attempt proves a source dead, later attempts don't blindly
            // re-include it and re-run into the same failure; but a fresh
            // HEAD recheck at the top of each attempt still gives it a
            // chance to come back, matching DeploymentTool's own per-restart
            // reachability recheck ("backupReachable=false ... continuing
            // single-mirror on main" / re-checked every attempt, not decided
            // once). Missing this entirely was a real regression: without
            // it, a mirror that goes bad partway through eligibility checks
            // gets blindly retried, workers-and-all, on all 3 restart
            // attempts, hitting the identical failure (and the identical
            // buffer-pool starvation from its dead chunks occupying the
            // strictly-ordered writer's head-of-line slots) every time.
            bool skipMirror = false;
            for (int attempt = 1; attempt <= AccelMaxRestartAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    // Main is the non-negotiable source (the plain fallback
                    // path only ever uses it too) - if it's gone, there is
                    // no single-source mode to fall back to here; abort the
                    // whole accelerated path same as the initial eligibility
                    // check would have.
                    long reheadSize; DateTime reheadModified;
                    if (!Head(url, out reheadSize, out reheadModified))
                    {
                        Logger.Log("Accelerated download: main unreachable on restart - aborting.");
                        return false;
                    }
                    if (skipMirror)
                    {
                        long mSize; DateTime mModified;
                        skipMirror = !Head(mirrorUrl, out mSize, out mModified) ||
                            mSize != reheadSize || Math.Abs((reheadModified - mModified).TotalSeconds) > 2;
                        Logger.Log(skipMirror
                            ? "Accelerated download: mirror still unavailable on restart - continuing single-source on main."
                            : "Accelerated download: mirror reachable again on restart - resuming dual-source.");
                    }
                }

                bool mainDied, mirrorDied;
                if (RunAttempt(url, mirrorUrl, candidatePath, mainSize, progressCb,
                        false, skipMirror, out mainDied, out mirrorDied))
                {
                    string actual = Net.FileHash(candidatePath, mainHash.Algorithm);
                    if (!actual.Equals(mainHash.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log("Accelerated download: final hash mismatch - aborting, no retry.");
                        try { File.Delete(candidatePath); } catch { }
                        return false;
                    }
                    Logger.Log(fname + ": accelerated download verified OK (" + mainHash.Algorithm + ").");
                    destPath = candidatePath;
                    return true;
                }
                if (mirrorDied) skipMirror = true;
                Logger.Log(string.Format("Accelerated download attempt {0}/{1} failed, retrying ...",
                    attempt, AccelMaxRestartAttempts));
            }
            return false;
        }

        // Matches DeploymentTool's own PickServer speed test (read in full
        // before porting, not re-derived): download the dedicated
        // "speed.test" file each mirror hosts, BOTH mirrors probed in
        // PARALLEL threads (not one after the other - sequential probing is
        // unfair/noisy, since the first request pays DNS/TLS/TCP-slow-start
        // costs the second one gets to skip via connection reuse; confirmed
        // live in this project's own testing: probing sequentially measured
        // a real ~3x difference between two mirrors that were, in that test,
        // literally the same server).
        static void EnsureSpeedProbed(string mainBase, string mirrorBase)
        {
            lock (_speedLock)
            {
                if (_speedProbed) return;
                Logger.Log("Testing mirror speeds ...");
                double[] speeds = new double[2];
                Thread[] threads = new Thread[2];
                string[] bases = new string[] { mainBase, mirrorBase };
                for (int i = 0; i < 2; i++)
                {
                    int idx = i;
                    threads[i] = new Thread(delegate() { speeds[idx] = ProbeSpeed(bases[idx]); });
                    threads[i].IsBackground = true;
                    threads[i].Start();
                }
                foreach (Thread t in threads) t.Join(25000);

                _mainSpeedBps   = speeds[0];
                _mirrorSpeedBps = speeds[1];
                _speedProbed = true;
                Logger.Log(string.Format("Mirror speed probe: main {0:F1} MB/s, mirror {1:F1} MB/s.",
                    _mainSpeedBps / 1048576.0, _mirrorSpeedBps / 1048576.0));
            }
        }

        // Downloads <mirrorBase>/speed.test (a dedicated test file each
        // mirror hosts - same file DeploymentTool's own PickServer uses,
        // and the same one this app's own pre-refactor mirror-racing code
        // used) in full, timed, to estimate real throughput.
        static double ProbeSpeed(string mirrorBase)
        {
            try
            {
                HttpWebRequest req = MakeRangedRequest(mirrorBase + "speed.test", -1, -1, 20000);
                req.ReadWriteTimeout = 20000;
                DateTime t0 = DateTime.UtcNow;
                long total = 0;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                {
                    byte[] buf = new byte[65536];
                    int r;
                    while ((r = s.Read(buf, 0, buf.Length)) > 0) total += r;
                }
                double secs = Math.Max(0.001, (DateTime.UtcNow - t0).TotalSeconds);
                return total / secs;
            }
            catch (Exception ex)
            {
                Logger.Log("Mirror speed probe failed for " + mirrorBase + ": " + ex.Message);
                return 0;
            }
        }

        // Splits AccelTotalConnections between the two mirrors
        // proportionally to measured speed - never starves either
        // mirror to 0, never hands one every connection.
        static void ComputeWeightedConnections(double mainBps, double otherBps,
            out int mainConns, out int otherConns)
        {
            if (mainBps <= 0 && otherBps <= 0)
            {
                mainConns = AccelTotalConnections / 2;
                otherConns = AccelTotalConnections - mainConns;
                return;
            }
            double total = mainBps + otherBps;
            mainConns = (int)Math.Round(AccelTotalConnections * (mainBps / total));
            mainConns = Math.Max(1, Math.Min(AccelTotalConnections - 1, mainConns));
            otherConns = AccelTotalConnections - mainConns;
        }

        class AccelSource
        {
            public readonly string Url;
            public readonly string Tag;
            volatile bool _dead;
            int _consecutiveFailures;
            double _rollingRateBps;

            public AccelSource(string url, string tag) { Url = url; Tag = tag; }

            public bool Dead { get { return _dead; } }
            public double RollingRateBps { get { return _rollingRateBps; } }

            public void UpdateRollingRate(double sampledBps)
            {
                _rollingRateBps = _rollingRateBps <= 0 ? sampledBps : _rollingRateBps * 0.6 + sampledBps * 0.4;
            }

            public void OnFailure()
            {
                if (Interlocked.Increment(ref _consecutiveFailures) >= AccelMaxConsecutiveServerFailures)
                    _dead = true;
            }

            public void OnSuccess() { Interlocked.Exchange(ref _consecutiveFailures, 0); }

            // Used only to seed a fresh attempt's source as already-dead
            // when the caller decided (via a fresh HEAD recheck) to skip it
            // for this attempt - never called as a result of this source's
            // own traffic.
            public void MarkDeadFromStart() { _dead = true; }
        }

        // 0 = owner fetching (no hedge yet), 1 = hedge also active, 2 = resolved
        class AccelChunk
        {
            public readonly long Index;
            public readonly long ChunkStart;
            public readonly int  ChunkLength;
            public AccelSource Owner;
            public byte[] OwnerBuffer;
            public long   OwnerBytesSoFar;
            public int    StartTicks;

            public int Phase;
            public AccelSource HedgeSource;
            public byte[] HedgeBuffer;
            public long   HedgeBytesSoFar;
            public long   HedgeRangeStart; // offset from ChunkStart where the hedge began

            // The currently in-flight request for each side, if any - lets
            // the winning side abort the loser immediately instead of
            // leaving it to run to its own natural completion/timeout.
            // Ported from DeploymentTool's AccelTryResolve, which does the
            // same on both win branches - missing here meant a losing
            // hedge/owner kept a real connection (and, pre-fix, one of only
            // 2 real per-mirror slots) tied up and kept pulling bytes nobody
            // needed for up to AccelTimeoutMs longer than necessary.
            public volatile HttpWebRequest OwnerRequest;
            public volatile HttpWebRequest HedgeRequest;

            // How many fetch attempts for this chunk's CURRENT life are
            // still alive (owner=1 always; +1 while a hedge is active).
            // Whichever side's failure brings this to 0 is the one that
            // actually requeues the chunk - a single side failing while the
            // other is still running must NOT requeue, since the survivor
            // might still succeed. Reset to 1 (and Phase reset to 0) only at
            // the point a fresh dequeue starts this chunk index's next life.
            // Ported from DeploymentTool's AccelChunkState.AliveAttempts -
            // missing here was a real bug reachable with hedging on (the
            // default config): an owner failing while its own hedge was
            // still active would requeue the chunk anyway (checked only
            // `Phase != 2`, not whether a hedge was still running), letting
            // a second worker dequeue the SAME index and start writing into
            // a reused AccelChunk's OwnerBuffer/OwnerBytesSoFar from scratch
            // WHILE the original, still-alive hedge could later "win" and
            // splice a PREFIX from that same OwnerBuffer that a live thread
            // was concurrently overwriting - a genuine data race, not just
            // wasted work. Symmetrically, a hedge failing while the owner
            // had ALREADY failed first (Phase left at 1, nobody in
            // WorkerLoop or RunHedge left to requeue it) left the chunk
            // stuck forever, relying entirely on the attempt-wide stall
            // watchdog to ever notice.
            public int AliveAttempts;

            // Set once resolved, tells the writer which buffer(s) to commit.
            public bool WonByHedge;

            public AccelChunk(long index, long chunkStart, int chunkLength)
            {
                Index = index; ChunkStart = chunkStart; ChunkLength = chunkLength;
                AliveAttempts = 1;
            }
        }

        class AccelPendingQueue
        {
            readonly SortedSet<long> _set = new SortedSet<long>();
            readonly object _lock = new object();
            public void Enqueue(long idx) { lock (_lock) { _set.Add(idx); } }
            public bool TryDequeueLowest(out long idx)
            {
                lock (_lock)
                {
                    if (_set.Count == 0) { idx = -1; return false; }
                    idx = _set.Min;
                    _set.Remove(idx);
                    return true;
                }
            }
        }

        class AccelContext
        {
            public AccelChunk[] Chunks;
            public AccelPendingQueue Pending;
            public ConcurrentDictionary<long, AccelChunk> Completed;
            public BlockingCollection<byte[]> BufferPool;
            public AutoResetEvent CompletedSignal;
            public AccelSource Main, Mirror;
            public long TotalBytes;
            public long TotalBytesIssued;   // Interlocked
            public long HedgeBytesIssued;   // Interlocked
            public long LastProgressTicks;  // Interlocked (Environment.TickCount)
            public volatile bool GiveUp;
            public Action<long, long, double> ProgressCb;
            public DateTime StartTime;
            public long Remaining; // chunks not yet in Completed (Interlocked)
        }

        static bool RunAttempt(string mainUrl, string mirrorUrl, string destPath,
            long totalBytes, Action<long, long, double> progressCb,
            bool skipMain, bool skipMirror, out bool mainDied, out bool mirrorDied)
        {
            int chunkCount = (int)((totalBytes + AccelChunkSize - 1) / AccelChunkSize);
            AccelContext ctx = new AccelContext();
            ctx.Chunks = new AccelChunk[chunkCount];
            ctx.Pending = new AccelPendingQueue();
            ctx.Completed = new ConcurrentDictionary<long, AccelChunk>();
            ctx.CompletedSignal = new AutoResetEvent(false);
            ctx.Main = new AccelSource(mainUrl, "main");
            ctx.Mirror = new AccelSource(mirrorUrl, "mirror");
            // A source proven dead in an EARLIER attempt of this same
            // TryDownload call (and not since reconfirmed reachable by the
            // caller's HEAD recheck - see TryDownloadCore's retry loop) is
            // marked dead here from the start, before a single worker spawns
            // for it - matches DeploymentTool's own per-restart-attempt
            // "backupReachable=false ... spawns zero workers for it, proceed
            // single-mirror" behavior. Without this, TryDownloadCore's 3
            // restart attempts each re-discover the same dead mirror from
            // scratch via the slow failed-chunk-timeout path, and - worse -
            // each attempt's workers on the dead side keep occupying the
            // low chunk indices the strictly-ordered writer needs first,
            // which can starve the whole (bounded) buffer pool before the
            // healthy side ever gets a chance at those indices. Confirmed
            // live: a mirror that stalls (but still answers HEAD) partway
            // through eligibility checks caused TryDownload to fail all 3
            // restart attempts even with the other mirror fully healthy.
            if (skipMain)   ctx.Main.MarkDeadFromStart();
            if (skipMirror) ctx.Mirror.MarkDeadFromStart();
            ctx.TotalBytes = totalBytes;
            ctx.LastProgressTicks = Environment.TickCount;
            ctx.ProgressCb = progressCb;
            ctx.StartTime = DateTime.UtcNow;

            for (int i = 0; i < chunkCount; i++)
            {
                long start = (long)i * AccelChunkSize;
                int  len   = (int)Math.Min(AccelChunkSize, totalBytes - start);
                ctx.Chunks[i] = new AccelChunk(i, start, len);
                ctx.Pending.Enqueue(i);
            }
            ctx.Remaining = chunkCount;

            int poolSize = (int)Math.Min(chunkCount, AccelTotalConnections) + AccelMaxConcurrentHedges;
            ctx.BufferPool = new BlockingCollection<byte[]>(poolSize);
            for (int i = 0; i < poolSize; i++) ctx.BufferPool.Add(new byte[AccelChunkSize]);

            // Program.cs raises ServicePointManager.DefaultConnectionLimit at
            // startup, but that only governs ServicePoint objects created
            // AFTER that line ran. By the time we get here, this process has
            // already contacted both mainUrl/mirrorUrl hosts (Head() and the
            // hash-sidecar fetches in TryDownloadCore, plus EnsureSpeedProbed)
            // - each of those already created (and cached, per-host) a
            // ServicePoint locked at whatever DefaultConnectionLimit was at
            // that moment. ServicePoint.ConnectionLimit is the one property
            // that can still raise an ALREADY-created ServicePoint's limit,
            // so set it directly on these two specific hosts too. Without
            // this, only 2 of the AccelTotalConnections requests per mirror
            // are ever actually dispatched; the rest sit queued inside
            // GetResponse() until they hit AccelTimeoutMs, which - since it
            // tends to happen to several queued requests on both mirrors at
            // once - reads from the outside exactly like a genuine "both
            // mirrors went silent" stall, even though neither mirror was
            // ever the real problem. Ported from DeploymentTool's own
            // AcceleratedDownload, which sets this for the same reason.
            int servicePointLimit = AccelTotalConnections + 4;
            ServicePointManager.FindServicePoint(new Uri(mainUrl)).ConnectionLimit = servicePointLimit;
            ServicePointManager.FindServicePoint(new Uri(mirrorUrl)).ConnectionLimit = servicePointLimit;

            int mainConns, mirrorConns;
            if (skipMain)        { mainConns = 0; mirrorConns = AccelTotalConnections; }
            else if (skipMirror) { mainConns = AccelTotalConnections; mirrorConns = 0; }
            else ComputeWeightedConnections(_mainSpeedBps, _mirrorSpeedBps, out mainConns, out mirrorConns);

            List<Thread> workers = new List<Thread>();
            for (int i = 0; i < mainConns; i++) workers.Add(StartWorker(ctx, ctx.Main));
            for (int i = 0; i < mirrorConns; i++) workers.Add(StartWorker(ctx, ctx.Mirror));

            bool[] monitorRunning = new bool[] { true };
            Thread monitor = new Thread(delegate() { MonitorLoop(ctx, monitorRunning); });
            monitor.IsBackground = true;
            monitor.Start();

            bool writeOk = WriterLoop(ctx, destPath);
            if (!writeOk) ctx.GiveUp = true;

            monitorRunning[0] = false;
            foreach (Thread w in workers) w.Join(5000);
            monitor.Join(2000);

            long hedgeBytes = Interlocked.Read(ref ctx.HedgeBytesIssued);
            long totalIssued = Interlocked.Read(ref ctx.TotalBytesIssued);
            Logger.Log(string.Format("Accelerated attempt: hedge bytes {0} of {1} issued ({2}%).",
                hedgeBytes, totalIssued,
                totalIssued > 0 ? (100.0 * hedgeBytes / totalIssued).ToString("F1") : "0"));

            // Report which sources this attempt itself found dead (not ones
            // that were already skipped coming in) so the caller can decide
            // whether to keep skipping them on the next restart attempt.
            mainDied = !skipMain && ctx.Main.Dead;
            mirrorDied = !skipMirror && ctx.Mirror.Dead;
            return writeOk;
        }

        static Thread StartWorker(AccelContext ctx, AccelSource source)
        {
            Thread t = new Thread(delegate() { WorkerLoop(ctx, source); });
            t.IsBackground = true;
            t.Start();
            return t;
        }

        static void WorkerLoop(AccelContext ctx, AccelSource source)
        {
            while (!ctx.GiveUp && !source.Dead)
            {
                // Buffer FIRST, then dequeue - not the other way around.
                // Ported ordering from DeploymentTool's AccelWorkerLoop
                // (its own comment: "Bounded wait, not bufferPool.Take()...").
                // Getting this backwards (dequeue first, then wait for a
                // buffer) was a real, separate regression from the
                // AliveAttempts work above: a worker that successfully
                // dequeues a low chunk index but then has to WAIT for a free
                // buffer holds that index checked-out-but-unserviced the
                // whole time - during which NO OTHER worker can claim it
                // even if one has a buffer free right now, since it's no
                // longer in Pending at all. Under the exact conditions that
                // matter most (a strictly-ordered writer stuck behind that
                // same low index, itself the reason the buffer pool is
                // starved), this reliably deadlocked: confirmed live, a
                // 21-chunk download with one dead mirror got stuck with 5+
                // chunks sitting completed-but-unwritten (holding their
                // buffers hostage) while the low chunks the writer actually
                // needed next sat dequeued-but-buffer-starved, going nowhere
                // until the whole attempt's stall watchdog gave up - three
                // times, once per restart, since nothing about this pattern
                // changes between attempts.
                byte[] buffer = null;
                while (!ctx.GiveUp && buffer == null) ctx.BufferPool.TryTake(out buffer, 200);
                if (buffer == null) return; // only happens on GiveUp - nothing was ever dequeued, nothing to requeue

                long idx;
                if (!ctx.Pending.TryDequeueLowest(out idx))
                {
                    ctx.BufferPool.Add(buffer);
                    // The queue is momentarily empty, but a chunk still
                    // checked out by a worker on the OTHER source can yet
                    // fail and land back in Pending - if every worker on
                    // THIS (live) source exits the instant the queue first
                    // runs dry, nobody is left to pick that chunk up once it
                    // does. Only give up once nothing is genuinely left
                    // unresolved, or the attempt is being torn down anyway.
                    // Ported from DeploymentTool's AccelWorkerLoop, which has
                    // the identical fix/comment - missing here was a real
                    // regression: confirmed live, a single permanently-dead
                    // mirror caused TryDownload to fail every one of its 3
                    // attempts even with the other mirror fully healthy,
                    // because that healthy mirror's workers drained whatever
                    // was initially available, found the queue empty, and
                    // exited for good before the dead mirror's chunks ever
                    // failed and came back around.
                    if (ctx.GiveUp || Interlocked.Read(ref ctx.Remaining) <= 0) return;
                    Thread.Sleep(50);
                    continue;
                }

                AccelChunk chunk = ctx.Chunks[idx];
                chunk.Owner = source;
                chunk.OwnerBuffer = buffer;
                chunk.OwnerBytesSoFar = 0;
                chunk.StartTicks = Environment.TickCount;

                bool ok = FetchRange(ctx, source, chunk.ChunkStart, buffer, 0, chunk.ChunkLength,
                    delegate(int r) { Interlocked.Add(ref chunk.OwnerBytesSoFar, r); },
                    delegate() { return chunk.Phase == 2; },
                    delegate(HttpWebRequest r) { chunk.OwnerRequest = r; });

                if (!ok)
                {
                    // FetchRange already called source.OnFailure() per failed
                    // HTTP try - not repeated here, that would double-count.
                    //
                    // OwnerBuffer must NOT be released here just because this
                    // side failed - if a hedge is (or was) active for this
                    // chunk's life, it may still win (or may already have
                    // won) using this exact buffer's PREFIX bytes (everything
                    // before HedgeRangeStart, fetched before the hedge ever
                    // started); the writer is the one that releases it in
                    // that case, once committed. Only once EVERY attempt on
                    // this life (owner, and any hedge) has given up - decided
                    // by AliveAttempts hitting 0 - is it genuinely abandoned
                    // and safe to recycle. This was unconditional before
                    // (`ctx.BufferPool.Add(buffer)` on every failure,
                    // regardless of hedge state) - a real, pre-existing bug
                    // independent of the Abort() work above: whenever a
                    // hedge outlived a failed/losing owner, the SAME OwnerBuffer
                    // object got added to BufferPool a second time once the
                    // writer later committed the hedge's win, so two unrelated
                    // in-flight fetches could end up sharing (and corrupting)
                    // one buffer.
                    int stillAlive = Interlocked.Decrement(ref chunk.AliveAttempts);
                    if (chunk.Phase != 2 && stillAlive <= 0 && !ctx.GiveUp)
                    {
                        ctx.BufferPool.Add(buffer);
                        chunk.Phase = 0;
                        chunk.AliveAttempts = 1;
                        // Clear Owner (and its now-pooled buffer) too - a
                        // chunk sitting in Pending has no owner at all until
                        // some worker actually dequeues it. Leaving the OLD
                        // Owner/OwnerBuffer/OwnerBytesSoFar/StartTicks in
                        // place was a real bug: ScanForHedges only checks
                        // `Phase != 0 || Owner == null` to decide whether a
                        // chunk looks hedge-worthy, so with Owner still
                        // pointing at the dead source, it could "hedge" a
                        // chunk nobody was actually fetching - using a stale
                        // OwnerBytesSoFar for the range split AND a buffer
                        // reference that was JUST returned to the pool above
                        // and can already be reassigned to a totally
                        // unrelated concurrent fetch. That bogus hedge always
                        // "wins" (nothing is really racing it) and commits
                        // whatever garbage now occupies that recycled buffer
                        // as the chunk's prefix - confirmed live as a new
                        // failure mode once chunks started actually getting
                        // recycled under real contention (this exact
                        // AliveAttempts-based recycling didn't exist before).
                        chunk.Owner = null;
                        chunk.OwnerBuffer = null;
                        chunk.OwnerBytesSoFar = 0;
                        ctx.Pending.Enqueue(idx);
                    }
                    if (source.Dead) return;
                    continue;
                }
                source.OnSuccess();

                // Claim the win. Phase may already be 1 (a hedge is active)
                // or 2 (hedge already won) by the time we get here.
                int prev = Interlocked.CompareExchange(ref chunk.Phase, 2, 0);
                if (prev == 1) prev = Interlocked.CompareExchange(ref chunk.Phase, 2, 1);
                if (prev == 0 || prev == 1)
                {
                    // Owner won - its buffer already holds the WHOLE chunk
                    // (it always fetches the full 0..ChunkLength range).
                    chunk.WonByHedge = false;
                    ctx.Completed[chunk.Index] = chunk;
                    Interlocked.Decrement(ref ctx.Remaining);
                    ctx.CompletedSignal.Set();
                    // A hedge may still be running this chunk's remaining
                    // range - cancel it now rather than leaving it to run to
                    // its own timeout. Guarded by FetchRange's shouldAbandon
                    // check above, so the abort's own exception won't be
                    // mistaken for a real failure on the hedge's mirror.
                    HttpWebRequest hedgeReq = chunk.HedgeRequest;
                    if (hedgeReq != null) { try { hedgeReq.Abort(); } catch { } }
                }
                else
                {
                    // prev == 2: the hedge already won this chunk. Do NOT
                    // release `buffer` (== chunk.OwnerBuffer) here - the
                    // hedge's win already registered this chunk in
                    // ctx.Completed, and since WonByHedge's write still needs
                    // OwnerBuffer for the chunk's PREFIX bytes, the WRITER is
                    // the one that releases it, exactly once, after
                    // committing. Releasing it here too was the double-
                    // release bug described above the owner's failure branch
                    // - same root cause, reached via "succeeded but lost"
                    // instead of "failed" now that AliveAttempts fixed the
                    // other half.
                }
            }
        }

        // Fetches [rangeStart, rangeEnd) of one chunk into buffer[rangeStart..],
        // retrying up to AccelMaxRetriesPerChunk times, resuming from wherever
        // the previous attempt left off. shouldAbandon is polled between
        // requests so a losing side (the chunk was already resolved by the
        // other fetch) stops promptly instead of running out its own retries.
        // onRequestStarted publishes the in-flight request so the OTHER side
        // can Abort() it immediately on winning (see WorkerLoop/RunHedge) -
        // without that, a losing side only ever notices shouldAbandon()
        // between synchronous calls, so a request already blocked inside
        // GetResponse()/Read() keeps a real connection tied up, pulling bytes
        // nobody needs, for up to AccelTimeoutMs longer than necessary.
        static bool FetchRange(AccelContext ctx, AccelSource source, long chunkStart,
            byte[] buffer, long rangeStart, long rangeEnd,
            Action<int> onBytesRead, Func<bool> shouldAbandon, Action<HttpWebRequest> onRequestStarted)
        {
            long pos = rangeStart;
            int tries = 0;
            while (pos < rangeEnd)
            {
                if (ctx.GiveUp || shouldAbandon()) return false;
                tries++;
                try
                {
                    HttpWebRequest req = MakeRangedRequest(source.Url,
                        chunkStart + pos, chunkStart + rangeEnd - 1, AccelTimeoutMs);
                    onRequestStarted(req);
                    DateTime t0 = DateTime.UtcNow;
                    long sampleBytes = 0;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (resp.StatusCode != HttpStatusCode.PartialContent)
                            throw new Exception("Server did not return a partial-content range response.");
                        using (Stream s = resp.GetResponseStream())
                        {
                            int r;
                            while (pos < rangeEnd &&
                                   (r = s.Read(buffer, (int)pos, (int)Math.Min(65536, rangeEnd - pos))) > 0)
                            {
                                pos += r;
                                sampleBytes += r;
                                onBytesRead(r);
                                Interlocked.Add(ref ctx.TotalBytesIssued, r);
                                Interlocked.Exchange(ref ctx.LastProgressTicks, Environment.TickCount);
                                if (shouldAbandon()) return false;
                            }
                        }
                    }
                    double secs = Math.Max(0.001, (DateTime.UtcNow - t0).TotalSeconds);
                    source.UpdateRollingRate(sampleBytes / secs);
                }
                catch (Exception ex)
                {
                    // Resolved out from under us: the other side won and
                    // aborted this request (Abort() is not instantaneous, so
                    // the exception can land well after the fact), or the
                    // whole attempt is tearing down. Not a real mirror
                    // failure - checked BEFORE counting anything, matching
                    // DeploymentTool's identical guard in AccelRunFetchSide
                    // (its own comment: "Resolved out from under us ... Not a
                    // real mirror failure: don't count it, don't retry, don't
                    // requeue"). Without this, introducing the Abort() calls
                    // below without also guarding here would make a losing
                    // hedge/owner's cancelled request look like a genuine
                    // failure and wrongly erode a perfectly healthy source's
                    // consecutive-failure count.
                    if (ctx.GiveUp || shouldAbandon()) return false;

                    // Counted per FAILED HTTP ATTEMPT, shared across every
                    // thread assigned to this source - matches DeploymentTool
                    // exactly (its own comment: "a truly dead mirror is
                    // detected in roughly one timeout period this way rather
                    // than needing several full per-chunk timeouts to
                    // accumulate"). This was called once per fully-exhausted
                    // chunk instead (at the two call sites below, after
                    // FetchRange gave up) when first ported - a real
                    // regression: with AccelMaxRetriesPerChunk=2, that meant
                    // needing 3 whole exhausted CHUNKS (each already 3 tries
                    // deep) before a permanently-dead source was ever marked
                    // Dead, ~9 tries / well over a minute, versus DeploymentTool's
                    // ~3 tries / one timeout period - and every one of those
                    // tries let this source's own worker immediately re-claim
                    // its own just-failed, re-enqueued chunk ahead of the
                    // other source's workers (which only poll the queue every
                    // 50ms), starving the healthy mirror of chunks it could
                    // have finished instantly. Confirmed live: a single
                    // permanently-stalled mirror, with a fully healthy other
                    // mirror, failed all 3 restart attempts before this fix.
                    source.OnFailure();
                    if (source.Dead || ctx.GiveUp) return false;
                    if (tries > AccelMaxRetriesPerChunk) return false;
                    Logger.Log(string.Format("[{0}] range fetch failed (try {1}): {2}",
                        source.Tag, tries, ex.Message));
                    // Brief backoff before the next try on the same source -
                    // matches DeploymentTool's AccelRunFetchSide exactly
                    // (Thread.Sleep(300 * attempt)), missing here. Small
                    // (300ms/600ms), so it doesn't meaningfully slow real
                    // recovery, but avoids hammering a struggling server with
                    // back-to-back reconnects.
                    Thread.Sleep(300 * tries);
                }
            }
            return true;
        }

        static void MonitorLoop(AccelContext ctx, bool[] running)
        {
            while (running[0])
            {
                Thread.Sleep(AccelMonitorPollMs);
                if (ctx.GiveUp) return;

                int elapsedMs = unchecked(Environment.TickCount - (int)Interlocked.Read(ref ctx.LastProgressTicks));
                if (elapsedMs >= AccelStallSeconds * 1000)
                {
                    Logger.Log("Accelerated download: stall watchdog - no bytes from either mirror in " +
                        AccelStallSeconds + "s.");
                    ctx.GiveUp = true;
                    ctx.CompletedSignal.Set();
                    return;
                }

                if (ctx.ProgressCb != null)
                {
                    long done = Interlocked.Read(ref ctx.TotalBytesIssued);
                    double secs = Math.Max(0.001, (DateTime.UtcNow - ctx.StartTime).TotalSeconds);
                    ctx.ProgressCb(Math.Min(done, ctx.TotalBytes), ctx.TotalBytes, done / secs / 1048576.0);
                }

                if (AppConfig.HedgeBudgetPercent > 0) ScanForHedges(ctx);
            }
        }

        static void ScanForHedges(AccelContext ctx)
        {
            int activeHedges = 0;
            foreach (AccelChunk c in ctx.Chunks) if (c.Phase == 1) activeHedges++;
            if (activeHedges >= AccelMaxConcurrentHedges) return;

            long hedgeSoFar = Interlocked.Read(ref ctx.HedgeBytesIssued);
            long totalSoFar = Interlocked.Read(ref ctx.TotalBytesIssued);

            foreach (AccelChunk chunk in ctx.Chunks)
            {
                if (chunk.Phase != 0 || chunk.Owner == null) continue;
                int elapsed = unchecked(Environment.TickCount - chunk.StartTicks);
                if (elapsed < AccelHedgeMinElapsedMs) continue;

                long ownerBytes = Interlocked.Read(ref chunk.OwnerBytesSoFar);
                long remaining = chunk.ChunkLength - ownerBytes;
                if (remaining < AccelHedgeMinRemainingBytes) continue;

                AccelSource other = chunk.Owner == ctx.Main ? ctx.Mirror : ctx.Main;
                if (other.Dead || other.RollingRateBps <= 0) continue;

                double ownerRate = chunk.Owner.RollingRateBps > 0 ? chunk.Owner.RollingRateBps : 1;
                double ownerEta = remaining / ownerRate;
                double otherEta = remaining / other.RollingRateBps;
                if (ownerEta < otherEta * AccelHedgeEtaFactor) continue;

                double projectedRatio = 100.0 * (hedgeSoFar + remaining) / Math.Max(1, totalSoFar + remaining);
                if (projectedRatio > AppConfig.HedgeBudgetPercent) continue;

                if (Interlocked.CompareExchange(ref chunk.Phase, 1, 0) != 0) continue;

                byte[] hedgeBuffer;
                if (!ctx.BufferPool.TryTake(out hedgeBuffer, 0))
                {
                    Interlocked.CompareExchange(ref chunk.Phase, 0, 1);
                    continue;
                }

                // A second attempt is now alive for this chunk - its own
                // failure must not requeue the chunk while the owner might
                // still win, and vice versa. See AccelChunk.AliveAttempts.
                Interlocked.Increment(ref chunk.AliveAttempts);

                chunk.HedgeSource = other;
                chunk.HedgeBuffer = hedgeBuffer;
                chunk.HedgeRangeStart = ownerBytes;
                Logger.Log(string.Format("Hedging chunk {0}: {1} looked {2:F1}x slower than {3}.",
                    chunk.Index, chunk.Owner.Tag, ownerEta / Math.Max(0.001, otherEta), other.Tag));

                AccelChunk capturedChunk = chunk;
                Thread hedgeThread = new Thread(delegate() { RunHedge(ctx, capturedChunk); });
                hedgeThread.IsBackground = true;
                hedgeThread.Start();
                return; // one new hedge per tick
            }
        }

        static void RunHedge(AccelContext ctx, AccelChunk chunk)
        {
            long hedgeLen = chunk.ChunkLength - chunk.HedgeRangeStart;
            // FetchRange computes the actual HTTP byte range as
            // chunkStart+pos .. chunkStart+rangeEnd-1, so the absolute file
            // offset where the TAIL begins (chunk.ChunkStart +
            // HedgeRangeStart) must be passed as chunkStart here - passing
            // plain chunk.ChunkStart (as if the hedge fetch started at the
            // beginning of the chunk, like the owner's own fetch does) made
            // the hedge silently re-fetch the chunk's PREFIX instead of its
            // tail. Confirmed live: a genuine hedge win spliced that
            // (correct) owner prefix with a (wrong-range) hedge "tail" that
            // was actually a copy of the file's first `hedgeLen` bytes of
            // the CHUNK, not bytes [HedgeRangeStart, ChunkLength) - final
            // hash mismatch, first differing byte landing exactly at
            // HedgeRangeStart every time. A correctness bug, not a timing
            // one - would reproduce on every genuine hedge win, not just
            // occasionally.
            bool ok = FetchRange(ctx, chunk.HedgeSource, chunk.ChunkStart + chunk.HedgeRangeStart, chunk.HedgeBuffer,
                0, hedgeLen,
                delegate(int r)
                {
                    Interlocked.Add(ref chunk.HedgeBytesSoFar, r);
                    Interlocked.Add(ref ctx.HedgeBytesIssued, r);
                },
                delegate() { return chunk.Phase == 2; },
                delegate(HttpWebRequest r) { chunk.HedgeRequest = r; });

            if (!ok)
            {
                // FetchRange already called HedgeSource.OnFailure() per
                // failed HTTP try - not repeated here.
                //
                // HedgeBuffer is always ours to release here: nothing else
                // ever touches it unless THIS hedge wins (it didn't, on this
                // path), so this can't double-release it.
                ctx.BufferPool.Add(chunk.HedgeBuffer);

                // OwnerBuffer is a different story - only release/recycle it
                // (and the chunk's Phase/AliveAttempts) if the owner has ALSO
                // already given up on this life with nobody winning. If the
                // owner is still running (or already won), leave it alone -
                // see the matching comment in WorkerLoop's failure branch for
                // why unconditionally releasing it here was a real,
                // pre-existing double-release bug once the owner had already
                // failed first and left it for exactly this hedge to finish.
                int stillAlive = Interlocked.Decrement(ref chunk.AliveAttempts);
                if (chunk.Phase != 2 && stillAlive <= 0 && !ctx.GiveUp)
                {
                    ctx.BufferPool.Add(chunk.OwnerBuffer);
                    chunk.Phase = 0;
                    chunk.AliveAttempts = 1;
                    // See the matching comment in WorkerLoop's failure branch
                    // - a chunk sitting in Pending must look owner-less, or
                    // ScanForHedges can bogusly "hedge" it later using this
                    // stale Owner/OwnerBytesSoFar and a buffer reference
                    // that's already back in the pool.
                    chunk.Owner = null;
                    chunk.OwnerBuffer = null;
                    chunk.OwnerBytesSoFar = 0;
                    ctx.Pending.Enqueue(chunk.Index);
                }
                return;
            }
            chunk.HedgeSource.OnSuccess();

            // Try to claim resolution. If the owner already resolved first
            // (Phase already 2), this hedge lost the race - discard.
            if (Interlocked.CompareExchange(ref chunk.Phase, 2, 1) != 1)
            {
                ctx.BufferPool.Add(chunk.HedgeBuffer);
                return;
            }

            // Won: the owner's buffer still holds a valid, never-since-
            // touched PREFIX (bytes 0..HedgeRangeStart, written before the
            // hedge ever started - the owner may still be fetching further
            // into its own buffer past that point, but nothing reads those
            // higher indices from the owner buffer once WonByHedge is true,
            // so no data race). The writer commits prefix + hedge tail as
            // two separate sequential writes - no cross-buffer copy needed.
            chunk.WonByHedge = true;
            ctx.Completed[chunk.Index] = chunk;
            Interlocked.Decrement(ref ctx.Remaining);
            ctx.CompletedSignal.Set();
            // The owner is still fetching bytes past HedgeRangeStart that
            // nobody will ever read now - cancel it immediately rather than
            // letting it run to its own timeout. Safe regardless of exactly
            // how far it's gotten: FetchRange's shouldAbandon guard (Phase==2,
            // now true) means the abort's own exception won't be counted
            // against the owner's mirror as a real failure.
            HttpWebRequest ownerReq = chunk.OwnerRequest;
            if (ownerReq != null) { try { ownerReq.Abort(); } catch { } }
        }

        static bool WriterLoop(AccelContext ctx, string destPath)
        {
            long nextIndex = 0;
            try
            {
                using (FileStream fs = File.Create(destPath))
                {
                    while (nextIndex < ctx.Chunks.Length)
                    {
                        if (ctx.GiveUp) return false;
                        AccelChunk chunk;
                        if (!ctx.Completed.TryGetValue(nextIndex, out chunk))
                        {
                            ctx.CompletedSignal.WaitOne(200);
                            continue;
                        }
                        if (chunk.WonByHedge)
                        {
                            fs.Write(chunk.OwnerBuffer, 0, (int)chunk.HedgeRangeStart);
                            fs.Write(chunk.HedgeBuffer, 0, chunk.ChunkLength - (int)chunk.HedgeRangeStart);
                            ctx.BufferPool.Add(chunk.HedgeBuffer);
                        }
                        else
                        {
                            fs.Write(chunk.OwnerBuffer, 0, chunk.ChunkLength);
                        }
                        ctx.BufferPool.Add(chunk.OwnerBuffer);
                        AccelChunk removed;
                        ctx.Completed.TryRemove(nextIndex, out removed);
                        nextIndex++;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("Accelerated download: writer failed: " + ex.Message);
                return false;
            }
        }

        static bool Head(string url, out long size, out DateTime modified)
        {
            size = -1; modified = DateTime.MinValue;
            try
            {
                HttpWebRequest req = MakeRangedRequest(url, -1, -1, 10000);
                req.Method = "HEAD";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    size = resp.ContentLength;
                    modified = resp.LastModified.ToUniversalTime();
                    return size > 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Accelerated download: HEAD failed for " + url + ": " + ex.Message);
                return false;
            }
        }

        // rangeStartByte/rangeEndByte both -1 => no Range header (used for HEAD).
        static HttpWebRequest MakeRangedRequest(string url, long rangeStartByte, long rangeEndByte, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Credentials     = new NetworkCredential(AppConfig.User, AppConfig.Password);
            req.PreAuthenticate = true;
            req.Timeout         = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.ServerCertificateValidationCallback =
                delegate(object s,
                    System.Security.Cryptography.X509Certificates.X509Certificate c,
                    System.Security.Cryptography.X509Certificates.X509Chain ch,
                    System.Net.Security.SslPolicyErrors e) { return true; };
            req.UserAgent = "TriquetraUpdater/2.0.4";
            if (rangeStartByte >= 0 && rangeEndByte >= 0)
                req.AddRange(rangeStartByte, rangeEndByte);
            return req;
        }
    }
}
