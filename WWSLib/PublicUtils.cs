using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Security;
using System.Text;
using System;


namespace WWS
{
    /// <summary>
    /// A module containing public utility and extension methods
    /// </summary>
    public static class PublicUtils
    {
        /// <summary>
        /// Returns a secure string from the source string
        /// </summary>
        /// <param name="s">Source string</param>
        /// <returns>Secure string</returns>
        public static SecureString ToSecureString(this string s)
        {
            SecureString res = new SecureString();

            foreach (char c in s ?? "")
                res.AppendChar(c);

            return res;
        }

        /// <summary>
        /// ExecuteAsync's an async <see cref="Task"/> method (which has a void return value) synchronously
        /// </summary>
        /// <param name="task"><see cref="Task"/> method to execute</param>
        public static void RunSync(this Func<Task> task)
        {
            SynchronizationContext oldctx = SynchronizationContext.Current;
            ExclusiveSynchronizationContext synch = new ExclusiveSynchronizationContext();

            SynchronizationContext.SetSynchronizationContext(synch);

            synch.Post(async _ =>
            {
                try
                {
                    await task();
                }
                catch (Exception e)
                {
                    synch.InnerException = e;

                    throw;
                }
                finally
                {
                    synch.EndMessageLoop();
                }
            }, null);
            synch.BeginMessageLoop();

            SynchronizationContext.SetSynchronizationContext(oldctx);
        }

        /// <summary>
        /// ExecuteAsync's an async <see cref="Task{T}"/> method (which has the return type T) synchronously
        /// </summary>
        /// <typeparam name="T">Return Type</typeparam>
        /// <param name="task"><see cref="Task{T}"/> method to execute</param>
        /// <returns>Task return value</returns>
        public static T RunSync<T>(this Func<Task<T>> task)
        {
            SynchronizationContext oldctx = SynchronizationContext.Current;
            ExclusiveSynchronizationContext synch = new ExclusiveSynchronizationContext();
            T ret = default;

            SynchronizationContext.SetSynchronizationContext(synch);

            synch.Post(async _ =>
            {
                try
                {
                    ret = await task();
                }
                catch (Exception e)
                {
                    synch.InnerException = e;

                    throw;
                }
                finally
                {
                    synch.EndMessageLoop();
                }
            }, null);
            synch.BeginMessageLoop();

            SynchronizationContext.SetSynchronizationContext(oldctx);

            return ret;
        }

        /// <summary>
        /// Returns a complete string representation of the given exception including nested stack traces
        /// </summary>
        /// <param name="ex">Exception</param>
        /// <returns>Complete string representation</returns>
        public static string PrintException(this Exception ex)
        {
            StringBuilder sb = new StringBuilder();

            while (ex != null)
            {
                sb.Insert(0, $"[{ex.GetType().FullName}] {ex.Message}:\n{ex.StackTrace}\n");

                ex = ex.InnerException;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Runs the given task in an asynchronous context
        /// </summary>
        /// <param name="task">Task to be executed</param>
        /// <exception cref="ArgumentNullException">Thrown if the given task is `null`.</exception>
        public static void RunAsync(this Func<Task> task)
        {
            if (task is null)
                throw new ArgumentNullException(nameof(task));

            ThreadPool.QueueUserWorkItem(_ => Task.Run(task), null);
        }


        private sealed class ExclusiveSynchronizationContext
            : SynchronizationContext
        {
            private readonly Queue<Tuple<SendOrPostCallback, object>> items = new Queue<Tuple<SendOrPostCallback, object>>();
            private readonly AutoResetEvent workItemsWaiting = new AutoResetEvent(false);
            private bool done;

            public Exception InnerException { get; set; }


            public override void Send(SendOrPostCallback d, object state) => throw new NotSupportedException("Unable to send to the same thread");

            public override void Post(SendOrPostCallback d, object state)
            {
                lock (items)
                    items.Enqueue(Tuple.Create(d, state));

                workItemsWaiting.Set();
            }

            public void EndMessageLoop() => Post(_ => done = true, null);

            public void BeginMessageLoop()
            {
                while (!done)
                {
                    Tuple<SendOrPostCallback, object> task = null;

                    lock (items)
                        if (items.Count > 0)
                            task = items.Dequeue();

                    if (task != null)
                    {
                        task.Item1(task.Item2);

                        if (InnerException is Exception e) // the method threw an exeption
                            throw new AggregateException("AsyncHelpers.Run method threw an exception.", e);
                    }
                    else
                        workItemsWaiting.WaitOne();
                }
            }

            public override SynchronizationContext CreateCopy() => this;
        }
    }
}
