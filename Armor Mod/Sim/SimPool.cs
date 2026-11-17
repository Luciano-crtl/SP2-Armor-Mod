using System;
using System.Threading;

namespace ArmorFragments.Sim
{
    // A small pool of worker threads for the impact simulation. It has no Unity in it (so the lab app can use it too).
    //
    // For(count, chunk, body) cuts a loop into chunks and hands them out one at a time: every thread that is free
    // (the calling one included) takes the next chunk, so a thread that the game keeps busy simply ends up with fewer
    // chunks and the free ones with more: the work goes where there is room, without anybody having to decide.
    // The threads run below normal priority, so the game's own threads win whenever they need a core.
    //
    // MaxWorkers can be lowered while it runs (the mod does it when the frame rate drops): workers above it sit out.
    internal sealed class SimPool : IDisposable
    {
        private sealed class Job
        {
            public Action<int, int, int> Body;   // (start, end, chunk index)
            public int Count, Chunk, Chunks;
            public int Next;                     // the next chunk to take
            public int Remaining;                // chunks not finished yet
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public Exception Error;
        }

        private readonly Thread[] threads;
        private readonly object gate = new object();
        private Job current;
        private int generation;
        private bool stopping;

        public volatile int MaxWorkers;

        public int Workers
        {
            get { return threads.Length; }
        }

        public SimPool(int workers)
        {
            workers = Math.Max(0, workers);
            threads = new Thread[workers];
            MaxWorkers = workers;
            for (int i = 0; i < workers; i++)
            {
                int index = i;
                threads[i] = new Thread(() => Run(index))
                {
                    IsBackground = true,
                    Name = "ArmorFragments sim " + i,
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                };
                threads[i].Start();
            }
        }

        // Runs body(start, end, chunkIndex) over [0, count) in chunks of 'chunk' items, on every thread that is free.
        public void For(int count, int chunk, Action<int, int, int> body)
        {
            if (count <= 0) return;
            chunk = Math.Max(chunk, 1);
            int chunks = (count + chunk - 1) / chunk;

            if (threads.Length == 0 || MaxWorkers <= 0 || chunks <= 1)
            {
                for (int c = 0; c < chunks; c++) body(c * chunk, Math.Min(count, (c + 1) * chunk), c);
                return;
            }

            Job job = new Job { Body = body, Count = count, Chunk = chunk, Chunks = chunks, Remaining = chunks };
            lock (gate)
            {
                current = job;
                generation++;
                Monitor.PulseAll(gate);
            }
            Work(job);                 // the caller helps
            job.Done.Wait();
            if (job.Error != null) throw new InvalidOperationException("a simulation worker failed", job.Error);
        }

        private static void Work(Job job)
        {
            while (true)
            {
                int c = Interlocked.Increment(ref job.Next) - 1;
                if (c >= job.Chunks) return;
                try
                {
                    job.Body(c * job.Chunk, Math.Min(job.Count, (c + 1) * job.Chunk), c);
                }
                catch (Exception e)
                {
                    job.Error = e;
                }
                if (Interlocked.Decrement(ref job.Remaining) == 0) job.Done.Set();
            }
        }

        private void Run(int index)
        {
            int seen = 0;
            while (true)
            {
                Job job;
                lock (gate)
                {
                    while (generation == seen && !stopping) Monitor.Wait(gate);
                    if (stopping) return;
                    seen = generation;
                    job = current;
                }
                if (index < MaxWorkers && job != null) Work(job);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                stopping = true;
                Monitor.PulseAll(gate);
            }
        }
    }
}
