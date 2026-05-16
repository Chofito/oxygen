using System;
using System.Threading;

namespace Oxygen.Utilities
{
    /// <summary>
    /// Lightweight parallel-for utility using the ThreadPool directly.
    /// Improvements over Nitrate's FasterParallel:
    /// - Worker exceptions are captured and re-thrown as AggregateException
    ///   rather than silently releasing the CountdownEvent.
    /// - The last partition always runs on the calling thread to avoid
    ///   an unnecessary ThreadPool dispatch.
    /// </summary>
    internal static class OxygenParallel
    {
        /// <summary>
        /// Executes <paramref name="body"/> for each partition of
        /// [<paramref name="from"/>, <paramref name="to"/>) in parallel.
        /// The body receives the inclusive start and exclusive end of its partition.
        /// </summary>
        /// <exception cref="AggregateException">
        /// Thrown (after all partitions complete) if any worker threw.
        /// The first captured exception is wrapped as the inner exception.
        /// </exception>
        public static void For(int from, int to, Action<int, int> body)
        {
            int total = to - from;
            if (total <= 0)
                return;

            // Reserve one logical core for the main thread.
            int workers    = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, total));
            int baseSize   = total / workers;
            int remainder  = total % workers;

            using var countdown = new CountdownEvent(workers);
            Exception? firstException = null;

            int cursor = from;
            for (int i = 0; i < workers; i++)
            {
                int partFrom = cursor;
                int partTo   = partFrom + baseSize + (i < remainder ? 1 : 0);
                cursor = partTo;

                if (i == workers - 1)
                {
                    // Last partition: run on calling thread to avoid extra dispatch.
                    try   { body(partFrom, partTo); }
                    catch (Exception ex) { Interlocked.CompareExchange(ref firstException, ex, null); }
                    finally { countdown.Signal(); }
                }
                else
                {
                    int capturedFrom = partFrom;
                    int capturedTo   = partTo;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try   { body(capturedFrom, capturedTo); }
                        catch (Exception ex) { Interlocked.CompareExchange(ref firstException, ex, null); }
                        finally { countdown.Signal(); }
                    });
                }
            }

            countdown.Wait();

            if (firstException is not null)
                throw new AggregateException(
                    "One or more parallel workers encountered an error.", firstException);
        }
    }
}
