using System;
using System.Threading;

namespace Oxygen.Utilities
{
    internal static class OxygenParallel
    {
        private const int MinBatchSize = 64;

        private static int _count;
        private static Thread[]? _threads;
        private static Slice[]? _slices;
        private static SemaphoreSlim? _start;
        private static ManualResetEventSlim? _done;
        private static volatile int _remaining;
        private static Exception? _fault;

        private struct Slice
        {
            public int From, To;
            public Action<int, int>? Body;
        }

        internal static void Initialize()
        {
            if (_threads is not null)
                return;

            _count = Math.Max(1, Environment.ProcessorCount - 1);
            _slices = new Slice[_count];

            if (_count == 1)
            {
                _threads = Array.Empty<Thread>();
                return;
            }

            _start = new SemaphoreSlim(0, _count - 1);
            _done = new ManualResetEventSlim(false);
            _threads = new Thread[_count - 1];

            for (int i = 0; i < _threads.Length; i++)
            {
                int idx = i;
                _threads[i] = new Thread(() => WorkerLoop(idx))
                {
                    Name = $"Oxygen-{idx}",
                    IsBackground = true
                };
                _threads[i].Start();
            }
        }

        internal static void Shutdown()
        {
            if (_threads is null)
                return;

            if (_threads.Length > 0)
            {
                for (int i = 0; i < _threads.Length; i++)
                    _slices![i] = new Slice { Body = null };

                _start!.Release(_threads.Length);

                foreach (var t in _threads)
                    t.Join(2000);

                _start.Dispose();
                _done!.Dispose();
            }

            _threads = null;
        }

        public static void For(int from, int to, Action<int, int> body)
        {
            int total = to - from;
            if (total <= 0)
                return;

            if (_threads is null || _threads.Length == 0 || total < MinBatchSize)
            {
                body(from, to);
                return;
            }

            _fault = null;
            _done!.Reset();
            Volatile.Write(ref _remaining, _count);

            int cursor = from;
            int baseSize = total / _count;
            int remainder = total % _count;

            for (int i = 0; i < _count - 1; i++)
            {
                int pTo = cursor + baseSize + (i < remainder ? 1 : 0);
                _slices![i] = new Slice { From = cursor, To = pTo, Body = body };
                cursor = pTo;
            }

            _start!.Release(_count - 1);

            try { body(cursor, to); }
            catch (Exception ex) { Interlocked.CompareExchange(ref _fault, ex, null); }
            finally
            {
                if (Interlocked.Decrement(ref _remaining) == 0)
                    _done.Set();
            }

            _done.Wait();

            if (_fault is not null)
                throw new AggregateException("Parallel worker error.", _fault);
        }

        private static void WorkerLoop(int idx)
        {
            while (true)
            {
                _start!.Wait();
                var s = _slices![idx];
                if (s.Body is null)
                    return;

                OxygenWorker.Active = true;
                try { s.Body(s.From, s.To); }
                catch (Exception ex) { Interlocked.CompareExchange(ref _fault, ex, null); }
                finally
                {
                    OxygenWorker.Active = false;
                    if (Interlocked.Decrement(ref _remaining) == 0)
                        _done!.Set();
                }
            }
        }
    }
}
