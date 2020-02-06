﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BASRemote.Exceptions;
using BASRemote.Helpers;
using BASRemote.Objects;
using BASRemote.Services;

namespace BASRemote
{
    /// <inheritdoc cref="IBasRemoteClient" />
    public sealed class BasRemoteClient : IBasRemoteClient
    {
        /// <summary>
        ///     Dictionary of generic requests handlers.
        /// </summary>
        private readonly ConcurrentDictionary<int, object> _genericRequests = new ConcurrentDictionary<int, object>();

        /// <summary>
        ///     Dictionary of default requests handlers.
        /// </summary>
        private readonly ConcurrentDictionary<int, object> _defaultRequests = new ConcurrentDictionary<int, object>();

        /// <summary>
        /// 
        /// </summary>
        private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>();

        /// <summary>
        ///     Engine service provider object.
        /// </summary>
        private EngineService _engine;

        /// <summary>
        ///     Socket service provider object.
        /// </summary>
        private SocketService _socket;

        /// <summary>
        ///     Create an instance of <see cref="BasRemoteClient" /> class.
        /// </summary>
        /// <param name="options">
        ///     Remote control options.
        /// </param>
        public BasRemoteClient(Options options)
        {
            _engine = new EngineService(options);
            _socket = new SocketService(options);

            _engine.OnDownloadStarted += () => OnEngineDownloadStarted?.Invoke();
            _engine.OnExtractStarted += () => OnEngineExtractStarted?.Invoke();

            _engine.OnDownloadEnded += () => OnEngineDownloadEnded?.Invoke();
            _engine.OnExtractEnded += () => OnEngineExtractEnded?.Invoke();

            _socket.OnMessageReceived += message =>
            {
                OnMessageReceived?.Invoke(message.Type, message.Data);

                if (message.Type == "message")
                {
                    _completion.TrySetException(new AuthenticationException((string) message.Data["text"]));
                }

                if (message.Type == "thread_start")
                {
                    _completion.TrySetResult(true);
                }

                if (message.Type == "initialize")
                {
                    _socket.Send("accept_resources", new Params
                    {
                        {"-bas-empty-script-", true}
                    });
                }
                else if (message.Async && message.Id != 0)
                {
                    if (_genericRequests.TryRemove(message.Id, out var genericFunction))
                    {
                        (genericFunction as dynamic)(message.Data);
                    }

                    if (_defaultRequests.TryRemove(message.Id, out var defaultFunction))
                    {
                        (defaultFunction as dynamic)();
                    }
                }
            };

            _socket.OnMessageSent += message => OnMessageSent?.Invoke(message);
        }

        /// <inheritdoc />
        public event Action<string, dynamic> OnMessageReceived;

        /// <inheritdoc />
        public event Action<Message> OnMessageSent;

        /// <inheritdoc />
        public event Action OnEngineDownloadStarted;

        /// <inheritdoc />
        public event Action OnEngineExtractStarted;

        /// <inheritdoc />
        public event Action OnEngineDownloadEnded;

        /// <inheritdoc />
        public event Action OnEngineExtractEnded;

        /// <inheritdoc />
        public async Task Start()
        {
            await _engine.InitializeAsync().ConfigureAwait(false);

            var port = Rand.NextInt(10000, 20000);

            await _engine.StartServiceAsync(port)
                .ConfigureAwait(false);
            await _socket.StartServiceAsync(port)
                .ConfigureAwait(false);

            await StartClient().ConfigureAwait(false);
        }

        private async Task StartClient()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                using (cts.Token.Register(() => _completion.TrySetCanceled()))
                {
                    await _completion.Task.ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public IBasFunction RunFunctionSync(string functionName, Params functionParams, Action<dynamic> onResult,
            Action<Exception> onError)
        {
            EnsureClientStarted();

            return new BasFunction(this).RunFunctionSync(functionName, functionParams, onResult, onError);
        }

        /// <inheritdoc />
        public async Task<TResult> RunFunction<TResult>(string functionName, Params functionParams)
        {
            var tcs = new TaskCompletionSource<TResult>();

            RunFunctionSync(functionName, functionParams,
                result => tcs.TrySetResult((TResult)result),
                exception => tcs.TrySetException(exception));

            return await tcs.Task.ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<dynamic> RunFunction(string functionName, Params functionParams)
        {
            return await RunFunction<dynamic>(functionName, functionParams)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<TResult> SendAndWaitAsync<TResult>(string type, Params data = null)
        {
            var tcs = new TaskCompletionSource<TResult>();
            SendAsync<TResult>(type, data, result => tcs.TrySetResult(result));
            return await tcs.Task.ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<dynamic> SendAndWaitAsync(string type, Params data = null)
        {
            return await SendAndWaitAsync<dynamic>(type, data)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void SendAsync<TResult>(string type, Params data, Action<TResult> onResult)
        {
            EnsureClientStarted();

            var message = new Message(data ?? Params.Empty, type, true);
            _genericRequests.TryAdd(message.Id, onResult);
            _socket.Send(message);
        }

        /// <inheritdoc />
        public void SendAsync(string type, Params data, Action<dynamic> onResult)
        {
            SendAsync<dynamic>(type, data, onResult);
        }

        /// <inheritdoc />
        public void SendAsync(string type, Params data, Action onResult)
        {
            EnsureClientStarted();

            var message = new Message(data ?? Params.Empty, type, true);
            _defaultRequests.TryAdd(message.Id, onResult);
            _socket.Send(message);
        }

        /// <inheritdoc />
        public void SendAsync(string type, Params data)
        {
            SendAsync(type, data, () => { });
        }

        /// <inheritdoc />
        public void SendAsync<TResult>(string type, Action<TResult> onResult)
        {
            EnsureClientStarted();

            var message = new Message(Params.Empty, type, true);
            _genericRequests.TryAdd(message.Id, onResult);
            _socket.Send(message);
        }

        /// <inheritdoc />
        public void SendAsync(string type, Action<dynamic> onResult)
        {
            SendAsync<dynamic>(type, onResult);
        }

        /// <inheritdoc />
        public void SendAsync(string type, Action onResult)
        {
            EnsureClientStarted();

            var message = new Message(Params.Empty, type, true);
            _defaultRequests.TryAdd(message.Id, onResult);
            _socket.Send(message);
        }

        /// <inheritdoc />
        public void SendAsync(string type)
        {
            SendAsync(type, () => { });
        }

        /// <inheritdoc />
        public int Send(string type, Params data = null, bool async = false)
        {
            EnsureClientStarted();

            var message = new Message(data ?? Params.Empty, type, async);
            _socket.Send(message);
            return message.Id;
        }

        private void EnsureClientStarted()
        {
            if (!_completion.Task.IsCompleted)
            {
                throw new ClientNotStartedException();
            }
        }

        /// <inheritdoc />
        public IBasThread CreateThread()
        {
            return new BasThread(this);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _engine?.Dispose();
            _socket?.Dispose();
            _engine = null;
            _socket = null;
        }
    }
}