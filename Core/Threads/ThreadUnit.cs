using ExileCore.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExileCore.Threads
{
    public class ThreadUnit
    {
        public DebugInformation PerformanceTimer { get; }
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _waitEvent;
        private Job _job;

        public ThreadUnit(string name)
        {
            PerformanceTimer = new DebugInformation(name);
            _waitEvent = new ManualResetEventSlim(true, 1000);
            _thread = new Thread(DoWork)
            {
                Name = name, 
                IsBackground = true
            };
            _thread.Start();
        }
        public Job Job
        {
            get => _job;
            set
            {
                _job = value;
                if (!_job.IsCompleted) _waitEvent.Set();
            }
        }

        private void DoWork()
        {
            while(true)
            {
                if (Job == null || Job.IsCompleted)
                {
                    _waitEvent.Reset();
                    _waitEvent.Wait();
                    continue;
                }
                PerformanceTimer.TickAction(Job.Run);
            }
        }

        public void Abort()
        {
            try
            {
                Job.IsFailed = true;
                Job.IsCompleted = true;
            }
            finally
            {
                _thread.Abort();
            }
        }
    }
}
