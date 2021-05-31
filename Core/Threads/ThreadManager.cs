using ExileCore.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExileCore.Threads
{
    public class ThreadManager
    {
        public ConcurrentDictionary<string, ThreadUnit> Threads { get; }

        public ThreadManager()
        {
            Threads = new ConcurrentDictionary<string, ThreadUnit>();
        }

        public bool AddOrUpdateJob(Job job)
        {
            return AddOrUpdateJob(job.Name, job);
        }

        public bool AddOrUpdateJob(string name, Job job)
        {
            if (!Threads.ContainsKey(name))
            {
                var threadUnit = new ThreadUnit(name);
                Threads.AddOrUpdate(name, threadUnit, (key, oldValue) => threadUnit);
            }
            if (Threads[name].Job != null
                && !Threads[name].Job.IsCompleted
                && Threads[name].Job.IsStarted)
            {
                return false;
            }

            Threads[name].Job = job;
            return true;
        }

        public void AbortLongRunningThreads()
        {
            foreach (var thread in Threads)
            {
                var job = thread.Value?.Job;
                if (job == null) continue;
                if (job.ElapsedMs < job.TimeoutMs) continue;

                thread.Value.Abort();
                DebugWindow.LogError($"ThreadManager -> Thread aborted: {thread.Key}, timeout: {job.TimeoutMs}ms, elapsed: {job.ElapsedMs}ms");
                if (!Threads.TryRemove(thread.Key, out _))
                {
                    DebugWindow.LogError($"ThreadManager -> Unable to remove aborted Thread: {thread.Key}");
                }
            }
        }
    }
}
