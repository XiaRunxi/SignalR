﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Infrastructure;

namespace Microsoft.AspNet.SignalR.Messaging
{
    internal class ScaleoutStream
    {
        private TaskCompletionSource<object> _taskCompletionSource;
        private static Task _initializeDrainTask;
        private TaskQueue _queue;
        private StreamState _state;
        private Exception _error;

        private readonly int _size;
        private readonly ScaleoutQueueSetting _queueSetting;
        private readonly TraceSource _trace;
        private readonly string _tracePrefix;
        private readonly IPerformanceCounterManager _perfCounters;

        private readonly object _lockObj = new object();

        public ScaleoutStream(TraceSource trace, string tracePrefix, ScaleoutQueueSetting queueSetting, int size, IPerformanceCounterManager performanceCounters)
        {
            if (trace == null)
            {
                throw new ArgumentNullException("trace");
            }

            _trace = trace;
            _tracePrefix = tracePrefix;
            _size = size;
            _perfCounters = performanceCounters;

            _queueSetting = queueSetting;

            InitializeCore();
        }

        private bool UsingTaskQueue
        {
            get
            {
                if(_queueSetting == ScaleoutQueueSetting.Always)
                {
                    return true;
                }
                else if (_queueSetting == ScaleoutQueueSetting.InitialStateOnly && _state == StreamState.Initial)
                {
                    return true;
                }

                return false;
            }
        }

        public void Open()
        {
            lock (_lockObj)
            {
                if (ChangeState(StreamState.Open))
                {
                    _perfCounters.ScaleoutStreamCountOpen.Increment();
                    _perfCounters.ScaleoutStreamCountBuffering.Decrement();

                    _error = null;

                    if (UsingTaskQueue)
                    {
                        _taskCompletionSource.TrySetResult(null);
                    }
                }
            }
        }

        public Task Send(Func<object, Task> send, object state)
        {
            lock (_lockObj)
            {
                if (_error != null)
                {
                    throw _error;
                }

                // If the queue is closed then stop sending
                if (_state == StreamState.Closed)
                {
                    throw new InvalidOperationException(Resources.Error_StreamClosed);
                }

                var context = new SendContext(this, send, state);

                if (_initializeDrainTask != null)
                {
                    _initializeDrainTask.Wait();
                }

                if (UsingTaskQueue)
                {
                    Task task = _queue.Enqueue(Send, context);

                    if (task == null)
                    {
                        // The task is null if the queue is full
                        throw new InvalidOperationException(Resources.Error_TaskQueueFull);
                    }

                    // Always observe the task in case the user doesn't handle it
                    return task.Catch();
                }

                _perfCounters.ScaleoutSendQueueLength.Increment();
                return Send(context).Finally(counter =>
                {
                    ((IPerformanceCounter)counter).Decrement();
                },
                _perfCounters.ScaleoutSendQueueLength);
            }
        }

        public void SetError(Exception error)
        {
            Trace(TraceEventType.Error, "Error has happened with the following exception: {0}.", error);

            lock (_lockObj)
            {
                _perfCounters.ScaleoutErrorsTotal.Increment();
                _perfCounters.ScaleoutErrorsPerSec.Increment();

                Buffer();

                _error = error;
            }
        }

        public void Close()
        {
            Task task = TaskAsyncHelper.Empty;

            lock (_lockObj)
            {
                if (ChangeState(StreamState.Closed))
                {
                    _perfCounters.ScaleoutStreamCountOpen.RawValue = 0;
                    _perfCounters.ScaleoutStreamCountBuffering.RawValue = 0;

                    if (UsingTaskQueue)
                    {
                        // Ensure the queue is started
                        EnsureQueueStarted();

                        // Drain the queue to stop all sends
                        task = Drain(_queue);
                    }
                }
            }

            if (UsingTaskQueue)
            {
                // Block until the queue is drained so no new work can be done
                task.Wait();
            }
        }

        private static Task Send(object state)
        {
            var context = (SendContext)state;

            context.InvokeSend().Then(tcs =>
            {
                // Complete the task if the send is successful
                tcs.TrySetResult(null);
            },
            context.TaskCompletionSource)
            .Catch((ex, obj) =>
            {
                var ctx = (SendContext)obj;

                ctx.Stream.Trace(TraceEventType.Error, "Send failed: {0}", ex);

                lock (ctx.Stream._lockObj)
                {
                    // Set the queue into buffering state
                    ctx.Stream.SetError(ex.InnerException);

                    // Otherwise just set this task as failed
                    ctx.TaskCompletionSource.TrySetUnwrappedException(ex);
                }
            },
            context);

            return context.TaskCompletionSource.Task;
        }

        private void Buffer()
        {
            lock (_lockObj)
            {
                if (ChangeState(StreamState.Buffering))
                {
                    _perfCounters.ScaleoutStreamCountOpen.Decrement();
                    _perfCounters.ScaleoutStreamCountBuffering.Increment();

                    InitializeCore();
                }
            }
        }

        private void InitializeCore()
        {
            if (UsingTaskQueue)
            {
                Task task = DrainQueue();
                _queue = new TaskQueue(task, _size);
                _queue.QueueSizeCounter = _perfCounters.ScaleoutSendQueueLength;
            }
        }

        private Task DrainQueue()
        {
            // If the tcs is null or complete then create a new one
            if (_taskCompletionSource == null ||
                _taskCompletionSource.Task.IsCompleted)
            {
                _taskCompletionSource = new TaskCompletionSource<object>();
            }

            if (_queue != null)
            {
                // Drain the queue when the new queue is open
                return _taskCompletionSource.Task.Then(q => Drain(q), _queue);
            }

            // Nothing to drain
            return _taskCompletionSource.Task;
        }

        private void EnsureQueueStarted()
        {
            if (_taskCompletionSource != null)
            {
                _taskCompletionSource.TrySetResult(null);
            }
        }

        private bool ChangeState(StreamState newState)
        {
            // Do nothing if the state is closed
            if (_state == StreamState.Closed)
            {
                return false;
            }

            if (_state != newState)
            {
                Trace(TraceEventType.Information, "Changed state from {0} to {1}", _state, newState);

                var oldState = _state;

                _state = newState;

                // Draining the initialize queue before switching to open state
                if (oldState == StreamState.Initial && newState == StreamState.Open)
                {
                    // Ensure the queue is started
                    EnsureQueueStarted();

                    _initializeDrainTask = Drain(_queue);
                }

                return true;
            }

            return false;
        }

        private static Task Drain(TaskQueue queue)
        {
            if (queue == null)
            {
                return TaskAsyncHelper.Empty;
            }

            var tcs = new TaskCompletionSource<object>();

            queue.Drain().Catch().ContinueWith(task =>
            {
                tcs.SetResult(null);
            });

            return tcs.Task;
        }

        private void Trace(TraceEventType traceEventType, string value, params object[] args)
        {
            _trace.TraceEvent(traceEventType, 0, _tracePrefix + " - " + value, args);
        }

        private class SendContext
        {
            private readonly Func<object, Task> _send;
            private readonly object _state;

            public readonly ScaleoutStream Stream;
            public readonly TaskCompletionSource<object> TaskCompletionSource;

            public SendContext(ScaleoutStream stream, Func<object, Task> send, object state)
            {
                Stream = stream;
                TaskCompletionSource = new TaskCompletionSource<object>();
                _send = send;
                _state = state;
            }

            [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "The exception flows to the caller")]
            public Task InvokeSend()
            {
                try
                {
                    return _send(_state);
                }
                catch (Exception ex)
                {
                    return TaskAsyncHelper.FromError(ex);
                }
            }
        }

        private enum StreamState
        {
            Initial,
            Open,
            Buffering,
            Closed
        }
    }
}
