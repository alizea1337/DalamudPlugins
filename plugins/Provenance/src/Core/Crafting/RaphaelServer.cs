using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Provenance.Core.Crafting;

/// <summary>
/// The one long-running raphael-cli in serve mode (<see cref="ServeProtocol"/>) that re-plans the rest of a craft on Good
/// and Excellent steps. Prepared for one recipe at one set of stats and HQ ingredients (the request's
/// <see cref="RaphaelRequest.CacheKey"/>); asked for another, it ends the old process first, so there is never more than
/// one. It ends after a few minutes without use, and with the plugin; a job object kills it with the game should the game
/// end without unloading Provenance, and caps its memory (<see cref="MemoryLimit"/>). It uses half the logical cores, at
/// below-normal priority. Every wait is bounded: starting, preparing, each re-plan (the crafter gives up after
/// <see cref="Replanning.Wait"/>). A process that crashes or is slow to start gets one more try for its recipe.
/// </summary>
public sealed class RaphaelServer : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(2);
    /// <summary>A solve given up on is cancelled; when it hasn't answered this long after, the process goes.</summary>
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Ended after this long without a re-plan or a keep-alive (a pause, a stopped run).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    /// <summary>A live process isn't replaced by another recipe's this soon after it started (two crafters can't take turns every frame).</summary>
    private static readonly TimeSpan RestartGap = TimeSpan.FromSeconds(5);
    /// <summary>The one more try after a crash or a slow start waits this long.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    /// <summary>
    /// The most memory the serve process may commit (the job object's limit). Measured: 1.2 GB after preparing the
    /// heaviest case (a level-100 potion with specialist actions), growing to 2.0 GB over 150 crafts of re-planning, then
    /// flat. Past the limit it ends, and that recipe goes on with the cheap reaction (after one more try).
    /// </summary>
    public const long MemoryLimit = 3L << 30;

    private enum State { Off, Starting, Ready, Failed }

    private readonly Func<string?> executable;
    private readonly Action<string> log;
    /// <summary>Lines for the debug log and processes to stop, written down under the lock and dealt with outside it.</summary>
    private readonly ConcurrentQueue<string> logs = new();
    private readonly ConcurrentQueue<Process> toStop = new();
    private readonly ChildJob job = new();

    private readonly object gate = new();
    /// <summary>The request the process is for ("" for none). Read without the lock by <see cref="KeepAlive"/>.</summary>
    private volatile string key = "";
    /// <summary>Set when the failed process for <see cref="key"/> gets one more try (read without the lock).</summary>
    private volatile bool wantsRetry;
    private long retryAfterTicks;
    private long lastUsedTicks;
    private long noStartBeforeTicks;
    private Process? process;
    /// <summary>Bumped with every process, so lines of an old one are ignored.</summary>
    private int generation;
    private State state = State.Off;
    private string failure = "";
    /// <summary>The one more try was used for <see cref="key"/>.</summary>
    private bool retried;
    private TaskCompletionSource<string>? startupLine;
    /// <summary>The process running now said <see cref="ServeProtocol.Hello"/>.</summary>
    private bool helloSeen;
    private long nextId;
    private (long Id, TaskCompletionSource<ServeAnswer> Answer, DateTime Cancelled)? pending;
    /// <summary>The raphael-cli (path, size, time) found to have no serve mode: not tried again.</summary>
    private string unsupportedExe = "";
    private readonly Timer idleTimer;
    private bool disposed;

    /// <param name="executable">Where raphael-cli is (null: not found).</param>
    /// <param name="log">The debug log.</param>
    public RaphaelServer(Func<string?> executable, Action<string> log)
    {
        this.executable = executable;
        this.log = log;
        idleTimer = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>Whether the raphael-cli in use has the serve mode: null until it was started once.</summary>
    public bool? Supported { get; private set; }

    /// <summary>A serve process was prepared this session: re-planning works here.</summary>
    public bool Works { get; private set; }

    private static long Now => DateTime.UtcNow.Ticks;

    private void Touch() => Interlocked.Exchange(ref lastUsedTicks, Now);

    /// <summary>
    /// Keeps the process for <paramref name="requestKey"/> (a request's <see cref="RaphaelRequest.CacheKey"/>) from ending
    /// as unused; cheap enough for every frame (no lock). False when there's none for it, or a failed one is due its one
    /// more try: then <see cref="Prepare"/>.
    /// </summary>
    public bool KeepAlive(string requestKey)
    {
        if (requestKey.Length == 0 || requestKey != key || wantsRetry && Now >= Interlocked.Read(ref retryAfterTicks)) return false;
        Touch();
        return true;
    }

    /// <summary>
    /// Makes sure a serve process is prepared for this request (the rotation's own, as raphael-cli worked it out:
    /// <paramref name="expected"/> are its numbers). Returns at once; the work happens in the background. The same request
    /// again only counts as use; one that failed isn't tried again (but for one more try after a crash or a slow start).
    /// </summary>
    /// <param name="requestKey">The request's <see cref="RaphaelRequest.CacheKey"/>, when the caller has it.</param>
    public void Prepare(RaphaelRequest request, SimSettings expected, string? requestKey = null)
    {
        var newKey = requestKey ?? request.CacheKey;
        if (KeepAlive(newKey)) return;
        int gen;
        lock (gate)
        {
            if (disposed) return;
            if (newKey == key)
            {
                Touch();
                if (!(wantsRetry && state == State.Failed && Now >= Interlocked.Read(ref retryAfterTicks))) return;
                retried = true;
                wantsRetry = false;
                Note("serve: one more try for this recipe");
            }
            else
            {
                // A live process for another request isn't replaced this soon after it started.
                if (state is State.Starting or State.Ready && Now < Interlocked.Read(ref noStartBeforeTicks)) return;
                EndLocked("another recipe or other stats");
                retried = false;
                wantsRetry = false;
            }
            DetachLocked();
            key = newKey;
            state = State.Starting;
            failure = "";
            Touch();
            Interlocked.Exchange(ref noStartBeforeTicks, Now + RestartGap.Ticks);
            gen = generation;
        }
        Flush();
        _ = Task.Run(() => Start(gen, request, expected));
    }

    /// <summary>Why it can't re-plan for this request right now (null: it can).</summary>
    public string? NotReady(string requestKey)
    {
        lock (gate)
        {
            if (key != requestKey || state == State.Off) return "Raphael isn't prepared for this recipe";
            return state switch
            {
                State.Starting => "Raphael is still preparing",
                State.Failed => "Raphael's re-planning failed (" + failure + ")",
                _ => pending != null ? "Raphael is still busy with an earlier step" : null,
            };
        }
    }

    /// <summary>Sends a re-plan; null when it can't (not ready, busy). The answer comes back on the task, always within the bounds above.</summary>
    public (long Id, Task<ServeAnswer> Answer)? Solve(string requestKey, in SimState st, CraftCondition condition)
    {
        var line = "";
        try
        {
            lock (gate)
            {
                if (key != requestKey || state != State.Ready || pending != null || process is not { } p) return null;
                var id = ++nextId;
                var tcs = new TaskCompletionSource<ServeAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = (id, tcs, default);
                Touch();
                line = ServeProtocol.Solve(id, st, condition);
                try
                {
                    p.StandardInput.WriteLine(line);
                    p.StandardInput.Flush();
                }
                catch (Exception e)
                {
                    FailLocked("it couldn't be written to: " + e.Message, retry: true);
                    return null;
                }
                return (id, tcs.Task);
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>The crafter stopped waiting for this re-plan: it's cancelled (its late answer is dropped).</summary>
    public void GiveUp(long id)
    {
        lock (gate)
        {
            if (pending is not { } p || p.Id != id || p.Cancelled != default) return;
            pending = p with { Cancelled = DateTime.UtcNow };
            try
            {
                process?.StandardInput.WriteLine(ServeProtocol.Cancel(id));
                process?.StandardInput.Flush();
            }
            catch
            {
                // The reader notices a process that's gone.
            }
        }
    }

    /// <summary>Ends the process (recipe change, run end, stop, unload). Nothing happens when none runs.</summary>
    public void End(string why)
    {
        lock (gate) EndLocked(why);
        Flush();
    }

    /// <summary>Ends the process when it's the one for <paramref name="requestKey"/> (another crafter's stays).</summary>
    public void EndFor(string requestKey, string why)
    {
        lock (gate)
        {
            if (key == requestKey) EndLocked(why);
        }
        Flush();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            EndLocked("Provenance unloads");
        }
        Flush();
        idleTimer.Dispose();
        job.Dispose();
    }

    private void CheckIdle()
    {
        try
        {
            lock (gate)
            {
                if (state == State.Off) return;
                if (pending is { Cancelled: var c } && c != default && DateTime.UtcNow - c > CancelTimeout)
                {
                    FailLocked("a cancelled re-plan didn't end", retry: true);
                    return;
                }
                if (pending == null && Now - Interlocked.Read(ref lastUsedTicks) > IdleTimeout.Ticks) EndLocked("unused for a few minutes");
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>Writes the noted lines and stops the detached processes, outside the lock.</summary>
    private void Flush()
    {
        while (logs.TryDequeue(out var line)) log(line);
        while (toStop.TryDequeue(out var proc))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        // quit ends it at once; whatever is left goes a moment later.
                        try
                        {
                            proc.StandardInput.WriteLine(ServeProtocol.Quit);
                            proc.StandardInput.Flush();
                        }
                        catch
                        {
                            // The pipe broke: killed below.
                        }
                        if (!proc.WaitForExit(1000)) proc.Kill(true);
                    }
                }
                catch
                {
                    // Already gone.
                }
                finally
                {
                    proc.Dispose();
                }
            });
        }
    }

    private void Note(string line) => logs.Enqueue(line);

    private void EndLocked(string why)
    {
        if (state == State.Off && process == null && key.Length == 0) return;
        if (process != null) Note($"serve: ended ({why})");
        DetachLocked();
        state = State.Off;
        key = "";
        wantsRetry = false;
    }

    /// <summary>Stops the process and keeps the key, so the same request isn't tried again (but once, when <paramref name="retry"/>).</summary>
    private void FailLocked(string why, bool retry)
    {
        wantsRetry = retry && !retried;
        Interlocked.Exchange(ref retryAfterTicks, Now + RetryDelay.Ticks);
        Note($"serve: no re-planning for this recipe{(wantsRetry ? " for now" : "")}: {why}");
        DetachLocked();
        state = State.Failed;
        failure = why;
    }

    /// <summary>Lets go of the process (stopped outside the lock) and anything waiting on it.</summary>
    private void DetachLocked()
    {
        generation++;
        helloSeen = false;
        startupLine?.TrySetResult("");
        startupLine = null;
        if (pending is { } p) p.Answer.TrySetResult(new ServeAnswer(p.Id, ServeOutcome.Error, -1, Array.Empty<uint>(), "the solver ended"));
        pending = null;
        if (process is not { } proc) return;
        process = null;
        toStop.Enqueue(proc);
    }

    private static string ExeSignature(string exe)
    {
        try { return $"{exe}|{new FileInfo(exe).Length}|{File.GetLastWriteTimeUtc(exe).Ticks}"; }
        catch { return exe; }
    }

    /// <summary>A message without the executable's folder (it holds the Windows user name).</summary>
    private static string WithoutFolder(string message, string exe)
    {
        var folder = Path.GetDirectoryName(exe);
        return string.IsNullOrEmpty(folder) ? message : message.Replace(folder, "…", StringComparison.OrdinalIgnoreCase);
    }

    private async Task Start(int gen, RaphaelRequest request, SimSettings expected)
    {
        var exe = "";
        try
        {
            await StartOrFail(gen, request, expected, e => exe = e).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            lock (gate)
            {
                if (gen == generation) FailLocked("starting it failed: " + WithoutFolder(e.Message, exe), retry: true);
            }
        }
        finally
        {
            Flush();
        }
    }

    private async Task StartOrFail(int gen, RaphaelRequest request, SimSettings expected, Action<string> found)
    {
        // Finding and starting the process happens outside the lock: the crafter asks for the state every frame.
        var exe = executable();
        if (exe != null) found(exe);
        var signature = exe == null ? "" : ExeSignature(exe);
        lock (gate)
        {
            if (gen != generation) return;
            if (exe == null)
            {
                FailLocked("raphael-cli.exe wasn't found", retry: false);
                return;
            }
            if (signature == unsupportedExe)
            {
                state = State.Failed;
                failure = "this raphael-cli has no serve mode";
                return;
            }
        }
        var threads = Replanning.Threads(Environment.ProcessorCount);
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = ServeProtocol.Start(threads),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        // Its stderr comes to the debug log: no solver debug output there.
        proc.StartInfo.Environment.Remove("RUST_LOG");
        proc.Start();
        job.Add(proc, Note);
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch
        {
            // Not allowed here (Wine): normal priority, half the cores still.
        }
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (gen != generation)
            {
                // Ended or replaced while starting.
                toStop.Enqueue(proc);
                return;
            }
            process = proc;
            startupLine = first;
            proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                Note("serve stderr: " + e.Data.Trim());
                Flush();
            };
            proc.BeginErrorReadLine();
            _ = Task.Run(() => ReadLoop(gen, proc, signature));
            Note($"serve: {Path.GetFileName(exe)} started with {threads} threads; preparing {request.CacheKey}");
        }
        Flush();

        // The hello line, then the prepare's answer; both bounded.
        var hello = await WithTimeout(first.Task, StartTimeout).ConfigureAwait(false);
        lock (gate)
        {
            if (gen != generation) return;
            if (hello == null)
            {
                // Slow to start (a virus scan of a new raphael-cli, say): one more try a little later.
                FailLocked("raphael-cli didn't answer within five seconds of starting", retry: true);
                return;
            }
            if (hello != ServeProtocol.Hello)
            {
                // It answered something else: an older raphael-cli (or GatherBuddy Reborn's) without serve mode. Once, quietly.
                unsupportedExe = signature;
                Supported = false;
                FailLocked("this raphael-cli has no serve mode; reacting with the cheap edits only", retry: false);
                return;
            }
            Supported = true;
            first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            startupLine = first;
            proc.StandardInput.WriteLine(ServeProtocol.Prepare(request));
            proc.StandardInput.Flush();
        }
        var answer = await WithTimeout(first.Task, PrepareTimeout).ConfigureAwait(false);
        lock (gate)
        {
            if (gen != generation) return;
            startupLine = null;
            if (answer == null)
            {
                FailLocked("preparing took longer than two minutes", retry: false);
                return;
            }
            if (ServeProtocol.ParseReady(answer) is not { } ready)
            {
                FailLocked("preparing failed: " + (ServeProtocol.ParsePrepareError(answer) ?? answer), retry: false);
                return;
            }
            if (ready.Settings != expected)
            {
                FailLocked($"it prepared for other numbers ({ready.Settings?.ToString() ?? "unreadable"}) than the rotation's ({expected})", retry: false);
                return;
            }
            state = State.Ready;
            Works = true;
            Touch();
            Note($"serve: prepared in {ready.Ms} ms");
        }
    }

    private static async Task<string?> WithTimeout(Task<string> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == task ? task.Result : null;
    }

    private async Task ReadLoop(int gen, Process proc, string exeSignature)
    {
        try
        {
            while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                OnLine(gen, line);
        }
        catch
        {
            // The process went away (or its reader was disposed): same as the end of its output.
        }
        try
        {
            // How it ended: an older raphael-cli refuses "serve" with a usage error and a plain exit code; a crash (or the
            // memory limit) ends it with a Windows status code.
            int? exit = null;
            try
            {
                if (proc.WaitForExit(1000)) exit = proc.ExitCode;
            }
            catch
            {
                // Disposed: it was let go of already.
            }
            lock (gate)
            {
                if (gen != generation) return;
                if (!helloSeen && exit is >= 0 and <= 255)
                {
                    unsupportedExe = exeSignature;
                    Supported = false;
                    FailLocked($"this raphael-cli has no serve mode (it ended with {exit}); reacting with the cheap edits only", retry: false);
                    return;
                }
                FailLocked($"the process ended{(exit is { } x ? $" (exit code 0x{x:X8})" : "")}", retry: true);
            }
        }
        catch
        {
            // Nothing here may throw into an unobserved task.
        }
        finally
        {
            Flush();
        }
    }

    private void OnLine(int gen, string line)
    {
        lock (gate)
        {
            if (gen != generation) return;
            if (startupLine is { } s)
            {
                if (line.Trim() == ServeProtocol.Hello) helloSeen = true;
                s.TrySetResult(line.Trim());
                return;
            }
            if (ServeProtocol.ParseAnswer(line) is not { } answer)
            {
                Note("serve: unexpected line: " + line);
                return;
            }
            if (pending is not { } p || p.Id != answer.Id)
            {
                Note("serve: an answer nobody waits for: " + line);
                return;
            }
            pending = null;
            Touch();
            p.Answer.TrySetResult(answer);
        }
        Flush();
    }

    /// <summary>
    /// A Windows job object that kills the processes in it when its handle closes (with Provenance's unload, or with the game
    /// should it end some other way) and caps each one's memory at <see cref="MemoryLimit"/>. Where it can't be made (Wine),
    /// the process is still ended on unload.
    /// </summary>
    private sealed class ChildJob : IDisposable
    {
        private readonly object gate = new();
        private nint handle;
        private bool tried;
        private bool disposed;

        public void Add(Process p, Action<string> note)
        {
            lock (gate)
            {
                if (disposed) return;
                try
                {
                    if (!tried)
                    {
                        tried = true;
                        handle = Create();
                        if (handle == 0) note($"serve: no job object (error {Marshal.GetLastWin32Error()})");
                    }
                    if (handle != 0 && !AssignProcessToJobObject(handle, p.Handle))
                        note($"serve: not in the job object (error {Marshal.GetLastWin32Error()})");
                }
                catch (Exception e)
                {
                    note("serve: no job object: " + e.Message);
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                if (handle == 0) return;
                CloseHandle(handle);
                handle = 0;
            }
        }

        private static nint Create()
        {
            var job = CreateJobObject(0, null);
            if (job == 0) return 0;
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose | ProcessMemory },
                ProcessMemoryLimit = (nuint)MemoryLimit,
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (SetInformationJobObject(job, ExtendedLimitInformation, ptr, (uint)size)) return job;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            CloseHandle(job);
            return 0;
        }

        private const int ExtendedLimitInformation = 9;
        private const uint ProcessMemory = 0x100;
        private const uint KillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint CreateJobObject(nint attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint handle);
    }
}
