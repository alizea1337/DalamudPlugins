using System;
using System.ComponentModel;
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
/// end without unloading Provenance. It uses half the logical cores, at below-normal priority.
/// Every wait is bounded: starting, preparing, each re-plan (the crafter gives up after <see cref="Replanning.Wait"/>).
/// </summary>
public sealed class RaphaelServer : IDisposable
{
    private readonly Func<string?> executable;
    private readonly Action<string> log;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(2);
    /// <summary>A solve given up on is cancelled; when it hasn't answered this long after, the process goes.</summary>
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Ended after this long without a re-plan or a prepare for the same recipe (a pause, a stopped run).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    private enum State { Off, Starting, Ready, Failed }

    private readonly object gate = new();
    private Process? process;
    /// <summary>Bumped with every process, so lines of an old one are ignored.</summary>
    private int generation;
    private State state = State.Off;
    private string key = "";
    private string failure = "";
    private DateTime lastUsed;
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

    /// <summary>
    /// Makes sure a serve process is prepared for this request (the rotation's own, as raphael-cli worked it out:
    /// <paramref name="expected"/> are its numbers). Returns at once; the work happens in the background. The same request
    /// again only counts as use; a request that failed before isn't tried again.
    /// </summary>
    public void Prepare(RaphaelRequest request, SimSettings expected)
    {
        var newKey = request.CacheKey;
        int gen;
        lock (gate)
        {
            if (disposed) return;
            if (key == newKey && state != State.Off)
            {
                lastUsed = DateTime.UtcNow;
                return;
            }
            EndLocked("another recipe or other stats");
            key = newKey;
            state = State.Starting;
            failure = "";
            lastUsed = DateTime.UtcNow;
            gen = ++generation;
        }
        _ = Task.Run(() => Start(gen, request, expected));
    }

    /// <summary>Why it can't re-plan for this request right now (null: it can).</summary>
    public string? NotReady(RaphaelRequest request)
    {
        lock (gate)
        {
            if (key != request.CacheKey || state == State.Off) return "Raphael isn't prepared for this recipe";
            return state switch
            {
                State.Starting => "Raphael is still preparing",
                State.Failed => "Raphael's re-planning failed earlier (" + failure + ")",
                _ => pending != null ? "Raphael is still busy with an earlier step" : null,
            };
        }
    }

    /// <summary>Sends a re-plan; null when it can't (not ready, busy). The answer comes back on the task, always within the bounds above.</summary>
    public (long Id, Task<ServeAnswer> Answer)? Solve(RaphaelRequest request, in SimState st, CraftCondition condition)
    {
        lock (gate)
        {
            if (key != request.CacheKey || state != State.Ready || pending != null || process is not { } p) return null;
            var id = ++nextId;
            var tcs = new TaskCompletionSource<ServeAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = (id, tcs, default);
            lastUsed = DateTime.UtcNow;
            try
            {
                p.StandardInput.WriteLine(ServeProtocol.Solve(id, st, condition));
                p.StandardInput.Flush();
            }
            catch (Exception e)
            {
                pending = null;
                FailLocked("it couldn't be written to: " + e.Message);
                return null;
            }
            return (id, tcs.Task);
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
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            EndLocked("Provenance unloads");
        }
        idleTimer.Dispose();
        ChildJob.Close();
    }

    private void CheckIdle()
    {
        lock (gate)
        {
            if (state == State.Off) return;
            if (pending is { Cancelled: var c } && c != default && DateTime.UtcNow - c > CancelTimeout)
            {
                FailLocked("a cancelled re-plan didn't end");
                return;
            }
            if (pending == null && DateTime.UtcNow - lastUsed > IdleTimeout) EndLocked("unused for a few minutes");
        }
    }

    private void EndLocked(string why)
    {
        if (state == State.Off && process == null) return;
        if (process != null) log($"serve: ended ({why})");
        KillLocked();
        state = State.Off;
        key = "";
    }

    /// <summary>Stops the process and keeps the key, so the same request isn't tried again.</summary>
    private void FailLocked(string why)
    {
        log($"serve: no re-planning for this recipe: {why}");
        KillLocked();
        state = State.Failed;
        failure = why;
    }

    private void KillLocked()
    {
        generation++;
        helloSeen = false;
        startupLine?.TrySetResult("");
        startupLine = null;
        if (pending is { } p) p.Answer.TrySetResult(new ServeAnswer(p.Id, ServeOutcome.Error, -1, Array.Empty<uint>(), "the solver ended"));
        pending = null;
        if (process is not { } proc) return;
        process = null;
        try
        {
            if (!proc.HasExited)
            {
                proc.StandardInput.WriteLine(ServeProtocol.Quit);
                proc.StandardInput.Flush();
            }
        }
        catch
        {
            // Gone already, or the pipe broke: killed below.
        }
        // quit ends it at once; kill whatever is left a moment later, off this thread.
        _ = Task.Run(() =>
        {
            try
            {
                if (!proc.WaitForExit(1000)) proc.Kill(true);
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

    private static string ExeSignature(string exe)
    {
        try { return $"{exe}|{new FileInfo(exe).Length}|{File.GetLastWriteTimeUtc(exe).Ticks}"; }
        catch { return exe; }
    }

    private async Task Start(int gen, RaphaelRequest request, SimSettings expected)
    {
        // Finding and starting the process happens outside the lock: the crafter asks for the state every frame.
        lock (gate)
        {
            if (gen != generation) return;
        }
        var exe = executable();
        lock (gate)
        {
            if (gen != generation) return;
            if (exe == null)
            {
                FailLocked("raphael-cli.exe wasn't found");
                return;
            }
            if (ExeSignature(exe) == unsupportedExe)
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
        try
        {
            proc.Start();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            proc.Dispose();
            lock (gate)
            {
                if (gen == generation) FailLocked("it didn't start: " + e.Message);
            }
            return;
        }
        ChildJob.Add(proc, log);
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
                try { proc.Kill(true); }
                catch
                {
                    // Gone already.
                }
                proc.Dispose();
                return;
            }
            process = proc;
            startupLine = first;
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) log("serve stderr: " + e.Data.Trim());
            };
            proc.BeginErrorReadLine();
            var signature = ExeSignature(exe);
            _ = Task.Run(() => ReadLoop(gen, proc, signature));
        }
        log($"serve: started with {threads} threads; preparing {request.CacheKey}");

        // The hello line, then the prepare's answer; both bounded.
        var hello = await WithTimeout(first.Task, StartTimeout).ConfigureAwait(false);
        lock (gate)
        {
            if (gen != generation) return;
            if (hello == null)
            {
                // Slow to start (a virus scan of a new raphael-cli, say): this recipe goes without; the next one tries again.
                FailLocked("raphael-cli didn't answer within five seconds of starting");
                return;
            }
            if (hello != ServeProtocol.Hello)
            {
                // An older raphael-cli (or GatherBuddy Reborn's): no serve mode. Once, quietly.
                unsupportedExe = ExeSignature(exe);
                Supported = false;
                FailLocked("this raphael-cli has no serve mode; reacting with the cheap edits only");
                return;
            }
            Supported = true;
            first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            startupLine = first;
            try
            {
                proc.StandardInput.WriteLine(ServeProtocol.Prepare(request));
                proc.StandardInput.Flush();
            }
            catch (Exception e)
            {
                FailLocked("it couldn't be written to: " + e.Message);
                return;
            }
        }
        var answer = await WithTimeout(first.Task, PrepareTimeout).ConfigureAwait(false);
        lock (gate)
        {
            if (gen != generation) return;
            startupLine = null;
            if (answer == null)
            {
                FailLocked("preparing took longer than two minutes");
                return;
            }
            if (ServeProtocol.ParseReady(answer) is not { } ready)
            {
                FailLocked("preparing failed: " + (ServeProtocol.ParsePrepareError(answer) ?? answer));
                return;
            }
            if (ready.Settings != expected)
            {
                FailLocked($"it prepared for other numbers ({ready.Settings?.ToString() ?? "unreadable"}) than the rotation's ({expected})");
                return;
            }
            state = State.Ready;
            lastUsed = DateTime.UtcNow;
            log($"serve: prepared in {ready.Ms} ms");
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
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away: same as the end of its output.
        }
        lock (gate)
        {
            if (gen != generation) return;
            if (!helloSeen)
            {
                // An older raphael-cli (or GatherBuddy Reborn's) ends at once on "serve": no serve mode. Said once.
                unsupportedExe = exeSignature;
                Supported = false;
                FailLocked("this raphael-cli has no serve mode; reacting with the cheap edits only");
                return;
            }
            FailLocked("the process ended");
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
                log("serve: unexpected line: " + line);
                return;
            }
            if (pending is not { } p || p.Id != answer.Id)
            {
                log("serve: an answer nobody waits for: " + line);
                return;
            }
            pending = null;
            lastUsed = DateTime.UtcNow;
            p.Answer.TrySetResult(answer);
        }
    }

    /// <summary>
    /// A Windows job object that kills the processes in it when its last handle closes: with Provenance's unload, or with
    /// the game should it end some other way. Where it can't be made (Wine), the process is still ended on unload.
    /// </summary>
    private static class ChildJob
    {
        private static nint handle;
        private static bool tried;

        public static void Add(Process p, Action<string> log)
        {
            try
            {
                if (!tried)
                {
                    tried = true;
                    handle = Create();
                }
                if (handle != 0 && !AssignProcessToJobObject(handle, p.Handle))
                    log($"serve: not in the job object (error {Marshal.GetLastWin32Error()})");
            }
            catch (Exception e)
            {
                log("serve: no job object: " + e.Message);
            }
        }

        public static void Close()
        {
            if (handle == 0) return;
            CloseHandle(handle);
            handle = 0;
            tried = false;
        }

        private static nint Create()
        {
            var job = CreateJobObject(0, null);
            if (job == 0) return 0;
            var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose } };
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
