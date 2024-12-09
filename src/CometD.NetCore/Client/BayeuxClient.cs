using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using CometD.NetCore.Bayeux;
using CometD.NetCore.Bayeux.Client;
using CometD.NetCore.Client.Transport;
using CometD.NetCore.Common;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json.Linq;

namespace CometD.NetCore.Client
{
    public class BayeuxClient : AbstractClientSession, IBayeux, IDisposable
    {
        private readonly object _stateUpdateInProgressLock = new object();

        private readonly ILogger<BayeuxClient> _logger;

        private readonly TransportRegistry _transportRegistry = new TransportRegistry();
        private readonly Dictionary<string, object> _options = new Dictionary<string, object>();
        private readonly Queue<IMutableMessage> _messageQueue = new Queue<IMutableMessage>();
        private readonly ITransportListener _handshakeListener;
        private readonly ITransportListener _connectListener;
        private readonly ITransportListener _disconnectListener;
        private readonly ITransportListener _publishListener;
        private readonly AutoResetEvent _stateChanged = new AutoResetEvent(false);

        private BayeuxClientState _bayeuxClientState;
        private int _stateUpdateInProgress;

        public const string BACKOFF_INCREMENT_OPTION = "backoffIncrement";
        public const string MAX_BACKOFF_OPTION = "maxBackoff";
        public const string BAYEUX_VERSION = "1.0";

        public BayeuxClient(string url, params ClientTransport[] transports)
        {
            _handshakeListener = new HandshakeTransportListener(this);
            _connectListener = new ConnectTransportListener(this);
            _disconnectListener = new DisconnectTransportListener(this);
            _publishListener = new PublishTransportListener(this);

            if (transports == null)
            {
                throw new ArgumentNullException(nameof(transports));
            }

            if (transports.Length == 0)
            {
                throw new ArgumentException("No transports provided", nameof(transports));
            }

            if (transports.Any(t => t == null))
            {
                throw new ArgumentException("One of the transports was null", nameof(transports));
            }

            foreach (var t in transports)
            {
                _transportRegistry.Add(t);
            }

            foreach (var transportName in _transportRegistry.KnownTransports)
            {
                var clientTransport = _transportRegistry.GetTransport(transportName);
                if (clientTransport is HttpClientTransport httpTransport)
                {
                    httpTransport.Url = url;
                }
            }

            _bayeuxClientState = new DisconnectedState(this, null);
        }

        public BayeuxClient(string url, ILogger<BayeuxClient> logger, params ClientTransport[] transports)
            : this(url, transports)
        {
            _logger = logger;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stateChanged?.Dispose();
            }
        }

        public override Task HandshakeAsync(CancellationToken cancellationToken = default)
        {
            return HandshakeAsync(null, cancellationToken);
        }

        public override async Task HandshakeAsync(IDictionary<string, object> handshakeFields, CancellationToken cancellationToken = default)
        {
            Initialize();

            var allowedTransports = AllowedTransports;

            // Pick the first transport for the handshake, it will renegotiate if not right
            var initialTransport = _transportRegistry.GetTransport(allowedTransports[0]);
            initialTransport.Init();

            _logger?.LogDebug($"Using initial transport {initialTransport.Name}"
                             + $" from {Print.List(allowedTransports)}");

            await UpdateBayeuxClientStateAsync(_ => new HandshakingState(this, handshakeFields, initialTransport), cancellationToken);
        }

        public override bool Connected => IsConnected(_bayeuxClientState);

        public override bool Handshook => IsHandshook(_bayeuxClientState);

        public override string Id => _bayeuxClientState.ClientId;

        public override async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            await UpdateBayeuxClientStateAsync(
                    oldState =>
                    {
                        if (IsConnected(oldState))
                        {
                            return new DisconnectingState(this, oldState.Transport, oldState.ClientId);
                        }
                        else
                        {
                            return new DisconnectedState(this, oldState.Transport);
                        }
                    },
                    cancellationToken);
        }

        protected override AbstractSessionChannel NewChannel(ChannelId channelId, long replayId)
        {
            return new BayeuxClientChannel(this, channelId, replayId);
        }

        protected override ChannelId NewChannelId(string channelId)
        {
            // Save some parsing by checking if there is already one
            Channels.TryGetValue(channelId, out var channel);
            return channel == null ? new ChannelId(channelId) : channel.ChannelId;
        }

        protected override async Task SendBatchAsync(CancellationToken cancellationToken = default)
        {
            var bayeuxClientState = _bayeuxClientState;
            if (IsHandshaking(bayeuxClientState))
            {
                return;
            }

            var messages = TakeMessages();
            if (messages.Count > 0)
            {
                await SendMessagesAsync(messages, cancellationToken);
            }
        }

        /// <inheritdoc/>
        public ICollection<string> KnownTransportNames => _transportRegistry.KnownTransports;

        /// <inheritdoc/>
        public ITransport GetTransport(string transport)
        {
            return _transportRegistry.GetTransport(transport);
        }

        /// <inheritdoc/>
        public IList<string> AllowedTransports => _transportRegistry.AllowedTransports;

        /// <inheritdoc/>
        public object GetOption(string qualifiedName)
        {
            _options.TryGetValue(qualifiedName, out var obj);
            return obj;
        }

        /// <inheritdoc/>
        public void SetOption(string qualifiedName, object val)
        {
            _options[qualifiedName] = val;
        }

        /// <inheritdoc/>
        public ICollection<string> OptionNames => _options.Keys;

        /// <inheritdoc/>
        public IDictionary<string, object> Options => _options;

        public int BackoffIncrement { get; private set; }

        public int MaxBackoff { get; private set; }

        public bool Disconnected => IsDisconnected(_bayeuxClientState);

        public void OnSending(IList<IMessage> messages)
        {
        }

        public void OnMessages(IList<IMutableMessage> messages)
        {
        }

        public virtual void OnFailure(Exception e, IList<IMessage> messages)
        {
            _logger?.LogError($"{e}");
        }

        protected async Task FailMessagesAsync(Exception x, IList<IMessage> messages, CancellationToken cancellationToken = default)
        {
            foreach (var message in messages)
            {
                var failed = NewMessage();
                failed.Id = message.Id;
                failed.Successful = false;
                failed.Channel = message.Channel;
                failed["message"] = messages;
                if (x != null)
                {
                    failed["exception"] = x;
                }

                await ReceiveAsync(failed, cancellationToken);
            }
        }

        public Task<State> HandshakeAsync(int waitMs, CancellationToken cancellationToken = default)
        {
            return HandshakeAsync(null, waitMs, cancellationToken);
        }

        public async Task<State> HandshakeAsync(IDictionary<string, object> template, int waitMs, CancellationToken cancellationToken = default)
        {
            await HandshakeAsync(template, cancellationToken);
            ICollection<State> states = new List<State>
            {
                State.CONNECTING,
                State.DISCONNECTED
            };
            return WaitFor(waitMs, states);
        }

        protected async Task<bool> SendHandshakeAsync(CancellationToken cancellationToken = default)
        {
            var bayeuxClientState = _bayeuxClientState;

            if (IsHandshaking(bayeuxClientState))
            {
                var message = NewMessage();
                if (bayeuxClientState.HandshakeFields != null)
                {
                    foreach (var kvp in bayeuxClientState.HandshakeFields)
                    {
                        message.Add(kvp.Key, kvp.Value);
                    }
                }

                message.Channel = ChannelFields.META_HANDSHAKE;
                message[MessageFields.SUPPORTED_CONNECTION_TYPES_FIELD] = AllowedTransports;
                message[MessageFields.VERSION_FIELD] = BAYEUX_VERSION;
                if (message.Id == null)
                {
                    message.Id = NewMessageId();
                }

                _logger?.LogDebug(
                    "Handshaking with extra fields {0}, transport {1}",
                    Print.Dictionary(bayeuxClientState.HandshakeFields),
                    Print.Dictionary(bayeuxClientState.Transport as IDictionary<string, object>));

                await bayeuxClientState.SendAsync(_handshakeListener, message, cancellationToken: cancellationToken);
                return true;
            }

            return false;
        }

        private bool IsHandshaking(BayeuxClientState bayeuxClientState)
        {
            return bayeuxClientState.TypeValue == State.HANDSHAKING
                || bayeuxClientState.TypeValue == State.REHANDSHAKING;
        }

        private bool IsHandshook(BayeuxClientState bayeuxClientState)
        {
            return bayeuxClientState.TypeValue == State.CONNECTING
                || bayeuxClientState.TypeValue == State.CONNECTED
                || bayeuxClientState.TypeValue == State.UNCONNECTED;
        }

        protected async Task ProcessHandshakeAsync(IMutableMessage handshake, CancellationToken cancellationToken)
        {
            if (handshake.Successful)
            {
                var serverTransportObject = handshake[MessageFields.SUPPORTED_CONNECTION_TYPES_FIELD] as JArray;
                var serverTransports = serverTransportObject as IEnumerable<object>;

                var negotiatedTransports = _transportRegistry.Negotiate(serverTransports, BAYEUX_VERSION);
                var newTransport = negotiatedTransports.Count == 0 ? null : negotiatedTransports[0];
                if (newTransport == null)
                {
                    await UpdateBayeuxClientStateAsync(
                        oldState => new DisconnectedState(this, oldState.Transport),
                        (ct) => ReceiveAsync(handshake, ct),
                        cancellationToken);

                    // Signal the failure
                    handshake.Successful = false;
                    handshake[MessageFields.ERROR_FIELD] =
                            $"405:c{_transportRegistry.AllowedTransports},s{serverTransports}:no transport";

                    // TODO: also update the advice with reconnect=none for listeners ?
                }
                else
                {
                    await UpdateBayeuxClientStateAsync(
                        oldState =>
                        {
                            if (newTransport != oldState.Transport)
                            {
                                oldState.Transport.Reset();
                                newTransport.Init();
                            }

                            var action = GetAdviceAction(handshake.Advice, MessageFields.RECONNECT_RETRY_VALUE);
                            if (MessageFields.RECONNECT_RETRY_VALUE.Equals(action))
                            {
                                return new ConnectingState(
                                    this,
                                    oldState.HandshakeFields,
                                    handshake.Advice,
                                    newTransport,
                                    handshake.ClientId);
                            }
                            else if (MessageFields.RECONNECT_NONE_VALUE.Equals(action))
                            {
                                return new DisconnectedState(this, oldState.Transport);
                            }

                            return null;
                        },
                        (ct) => ReceiveAsync(handshake, ct),
                        cancellationToken);
                }
            }
            else
            {
                await UpdateBayeuxClientStateAsync(
                    oldState =>
                    {
                        var action = GetAdviceAction(handshake.Advice, MessageFields.RECONNECT_HANDSHAKE_VALUE);
                        if (MessageFields.RECONNECT_HANDSHAKE_VALUE.Equals(action)
                            || MessageFields.RECONNECT_RETRY_VALUE.Equals(action))
                        {
                            return new RehandshakingState(this, oldState.HandshakeFields, oldState.Transport, oldState.NextBackoff());
                        }
                        else if (MessageFields.RECONNECT_NONE_VALUE.Equals(action))
                        {
                            return new DisconnectedState(this, oldState.Transport);
                        }

                        return null;
                    },
                    (ct) => ReceiveAsync(handshake, ct),
                    cancellationToken);
            }
        }

        protected async Task ScheduleHandshakeAsync(int interval, int backoff, CancellationToken cancellationToken = default)
        {
            await ScheduleActionAsync(ct => SendHandshakeAsync(ct), interval, backoff, cancellationToken);
        }

        protected State CurrentState => _bayeuxClientState.TypeValue;

        public State WaitFor(int waitMs, ICollection<State> states)
        {
            var stop = DateTime.Now.AddMilliseconds(waitMs);
            var duration = waitMs;

            var s = CurrentState;
            if (states.Contains(s))
            {
                return s;
            }

            while (_stateChanged.WaitOne(duration))
            {
                if (_stateUpdateInProgress == 0)
                {
                    s = CurrentState;
                    if (states.Contains(s))
                    {
                        return s;
                    }
                }

                duration = (int)(stop - DateTime.Now).TotalMilliseconds;
                if (duration <= 0)
                {
                    break;
                }
            }

            s = CurrentState;
            if (states.Contains(s))
            {
                return s;
            }

            return State.INVALID;
        }

        protected async Task<bool> SendConnectAsync(int clientTimeout, CancellationToken cancellationToken = default)
        {
            var bayeuxClientState = _bayeuxClientState;
            if (IsHandshook(bayeuxClientState))
            {
                var message = NewMessage();
                message.Channel = ChannelFields.META_CONNECT;
                message[MessageFields.CONNECTION_TYPE_FIELD] = bayeuxClientState.Transport.Name;
                if (bayeuxClientState.TypeValue == State.CONNECTING || bayeuxClientState.TypeValue == State.UNCONNECTED)
                {
                    // First connect after handshake or after failure, add advice
                    message.GetAdvice(true)[MessageFields.TIMEOUT_FIELD] = 0;
                }

                await bayeuxClientState.SendAsync(_connectListener, message, clientTimeout, cancellationToken);
                return true;
            }

            return false;
        }

        protected async Task<bool> SendMessagesAsync(IList<IMutableMessage> messages, CancellationToken cancellationToken = default)
        {
            var bayeuxClientState = _bayeuxClientState;
            if (bayeuxClientState.TypeValue == State.CONNECTING || IsConnected(bayeuxClientState))
            {
                await bayeuxClientState.SendAsync(_publishListener, messages, cancellationToken: cancellationToken);
                return true;
            }
            else
            {
                await FailMessagesAsync(null, ObjectConverter.ToListOfIMessage(messages), cancellationToken);
                return false;
            }
        }

        private int PendingMessages
        {
            get
            {
                var value = _messageQueue.Count;

                var state = _bayeuxClientState;
                if (state.Transport is ClientTransport clientTransport)
                {
                    value += clientTransport.IsSending ? 1 : 0;
                }

                return value;
            }
        }

        /// <summary>
        /// Wait for send queue to be emptied.
        /// </summary>
        /// <param name="timeoutMS"></param>
        /// <returns>true if queue is empty, false if timed out.</returns>
        public bool WaitForEmptySendQueue(int timeoutMS)
        {
            if (PendingMessages == 0)
            {
                return true;
            }

            var start = DateTime.Now;

            while ((DateTime.Now - start).TotalMilliseconds < timeoutMS)
            {
                if (PendingMessages == 0)
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return false;
        }

        public async Task AbortAsync(CancellationToken cancellationToken = default)
        {
            await UpdateBayeuxClientStateAsync(oldState => new AbortedState(this, oldState.Transport), cancellationToken);
        }

        private IList<IMutableMessage> TakeMessages()
        {
            IList<IMutableMessage> queue = new List<IMutableMessage>(_messageQueue);
            _messageQueue.Clear();
            return queue;
        }

        private bool IsConnected(BayeuxClientState bayeuxClientState)
        {
            return bayeuxClientState.TypeValue == State.CONNECTED;
        }

        private bool IsDisconnected(BayeuxClientState bayeuxClientState)
        {
            return bayeuxClientState.TypeValue == State.DISCONNECTING || bayeuxClientState.TypeValue == State.DISCONNECTED;
        }

        protected async Task ProcessConnectAsync(IMutableMessage connect, CancellationToken cancellationToken = default)
        {
            await UpdateBayeuxClientStateAsync(
                    oldState =>
                    {
                        var advice = connect.Advice;
                        if (advice == null)
                        {
                            advice = oldState.Advice;
                        }

                        var action = GetAdviceAction(advice, MessageFields.RECONNECT_RETRY_VALUE);
                        if (connect.Successful)
                        {
                            if (MessageFields.RECONNECT_RETRY_VALUE.Equals(action))
                            {
                                return new ConnectedState(this, oldState.HandshakeFields, advice, oldState.Transport, oldState.ClientId);
                            }
                            else if (MessageFields.RECONNECT_NONE_VALUE.Equals(action))
                            {
                                // This case happens when the connect reply arrives after a disconnect
                                // We do not go into a disconnected state to allow normal processing of the disconnect reply
                                return new DisconnectingState(this, oldState.Transport, oldState.ClientId);
                            }
                        }
                        else
                        {
                            if (MessageFields.RECONNECT_HANDSHAKE_VALUE.Equals(action))
                            {
                                return new RehandshakingState(this, oldState.HandshakeFields, oldState.Transport, 0);
                            }
                            else if (MessageFields.RECONNECT_RETRY_VALUE.Equals(action))
                            {
                                return new UnconnectedState(this, oldState.HandshakeFields, advice, oldState.Transport, oldState.ClientId, oldState.NextBackoff());
                            }
                            else if (MessageFields.RECONNECT_NONE_VALUE.Equals(action))
                            {
                                return new DisconnectedState(this, oldState.Transport);
                            }
                        }

                        return null;
                    },
                    (ct) => ReceiveAsync(connect, ct),
                    cancellationToken);
        }

        protected async Task ProcessDisconnectAsync(IMutableMessage disconnect, CancellationToken cancellationToken = default)
        {
            await UpdateBayeuxClientStateAsync(
                    oldState => new DisconnectedState(this, oldState.Transport),
                    (ct) => ReceiveAsync(disconnect, ct),
                    cancellationToken);
        }

        protected async Task ProcessMessageAsync(IMutableMessage message, CancellationToken cancellationToken = default)
        {
            await ReceiveAsync(message, cancellationToken);
        }

        private string GetAdviceAction(IDictionary<string, object> advice, string defaultResult)
        {
            var action = defaultResult;
            if (advice?.ContainsKey(MessageFields.RECONNECT_FIELD) == true)
            {
                action = (string)advice[MessageFields.RECONNECT_FIELD];
            }

            return action;
        }

        protected async Task ScheduleConnectAsync(int interval, int backoff, int clientTimeout = ClientTransport.DEFAULT_TIMEOUT, CancellationToken cancellationToken = default)
        {
            await ScheduleActionAsync(ct => SendConnectAsync(clientTimeout, ct), interval, backoff, cancellationToken);
        }

        private async Task ScheduleActionAsync(Func<CancellationToken, Task> action, int interval, int backoff, CancellationToken cancellationToken = default)
        {
            await Task.Delay(interval + backoff, cancellationToken);
            await action(cancellationToken);
        }

        protected void Initialize()
        {
            BackoffIncrement = ObjectConverter.ToInt32(GetOption(BACKOFF_INCREMENT_OPTION), 1000);
            MaxBackoff = ObjectConverter.ToInt32(GetOption(MAX_BACKOFF_OPTION), 30000);
        }

        protected async Task TerminateAsync(CancellationToken cancellationToken = default)
        {
            var messages = TakeMessages();
            await FailMessagesAsync(null, ObjectConverter.ToListOfIMessage(messages), cancellationToken);
        }

        protected IMutableMessage NewMessage()
        {
            return new DictionaryMessage();
        }

        protected async Task EnqueueSendAsync(IMutableMessage message, CancellationToken cancellationToken = default)
        {
            if (CanSend())
            {
                IList<IMutableMessage> messages = new List<IMutableMessage>
                {
                    message
                };

                var sent = await SendMessagesAsync(messages, cancellationToken);
                _logger?.LogDebug("{0} message {1}", sent ? "Sent" : "Failed", message);
            }
            else
            {
                _messageQueue.Enqueue(message);
                _logger?.LogDebug($"Enqueued message {message} (batching: {Batching})");
            }
        }

        private bool CanSend()
        {
            return !IsDisconnected(_bayeuxClientState) && !Batching && !IsHandshaking(_bayeuxClientState);
        }

        private Task UpdateBayeuxClientStateAsync(
            Func<BayeuxClientState, BayeuxClientState> create,
            CancellationToken cancellationToken = default)
        {
            return UpdateBayeuxClientStateAsync(create, null, cancellationToken);
        }

        private async Task UpdateBayeuxClientStateAsync(
            Func<BayeuxClientState, BayeuxClientState> create,
            Func<CancellationToken, Task> postCreate,
            CancellationToken cancellationToken = default)
        {
            lock (_stateUpdateInProgressLock)
            {
                ++_stateUpdateInProgress;
            }

            var oldState = _bayeuxClientState;

            var newState = create(oldState);
            if (newState == null)
            {
                throw new SystemException();
            }

            if (!oldState.IsUpdateableTo(newState))
            {
                _logger?.LogDebug($"State not updateable : {oldState} -> {newState}");
                return;
            }

            _bayeuxClientState = newState;

            if (postCreate != null)
            {
                await postCreate(cancellationToken);
            }

            if (oldState.Type != newState.Type)
            {
                newState.Enter(oldState.Type);
            }

            await newState.ExecuteAsync(cancellationToken);

            // Notify threads waiting in waitFor()
            lock (_stateUpdateInProgressLock)
            {
                --_stateUpdateInProgress;

                if (_stateUpdateInProgress == 0)
                {
                    _stateChanged.Set();
                }
            }
        }

        public enum State
        {
            INVALID,
            UNCONNECTED,
            HANDSHAKING,
            REHANDSHAKING,
            CONNECTING,
            CONNECTED,
            DISCONNECTING,
            DISCONNECTED
        }

        private class PublishTransportListener : ITransportListener
        {
            protected BayeuxClient _bayeuxClient;

            public PublishTransportListener(BayeuxClient bayeuxClient)
            {
                _bayeuxClient = bayeuxClient;
            }

            public async Task OnSendingAsync(IList<IMessage> messages, CancellationToken cancellationToken = default)
            {
                _bayeuxClient.OnSending(messages);
            }

            public async Task OnMessagesAsync(IList<IMutableMessage> messages, CancellationToken cancellationToken = default)
            {
                _bayeuxClient.OnMessages(messages);
                foreach (var message in messages)
                {
                    await ProcessMessageAsync(message, cancellationToken);
                }
            }

            public async Task OnConnectExceptionAsync(Exception x, IList<IMessage> messages, CancellationToken cancellationToken = default)
            {
                await OnFailureAsync(x, messages, cancellationToken);
            }

            public async Task OnExceptionAsync(Exception x, IList<IMessage> messages, CancellationToken cancellationToken = default)
            {
                await OnFailureAsync(x, messages, cancellationToken);
            }

            public async Task OnExpireAsync(IList<IMessage> messages, CancellationToken cancellationToken = default)
            {
                await OnFailureAsync(new TimeoutException("expired"), messages, cancellationToken);
            }

            public async Task OnProtocolErrorAsync(string info, IList<IMessage> messages, CancellationToken cancellationToken = default)
            {
                await OnFailureAsync(new ProtocolViolationException(info), messages, cancellationToken);
            }

            protected virtual async Task ProcessMessageAsync(IMutableMessage message, CancellationToken cancellationToken = default)
            {
                await _bayeuxClient.ProcessMessageAsync(message, cancellationToken);
            }

            protected virtual async Task OnFailureAsync(Exception x, IList<IMessage> messages, CancellationToken cancellationToken = default)
            {
                _bayeuxClient.OnFailure(x, messages);
                await _bayeuxClient.FailMessagesAsync(x, messages, cancellationToken);
            }
        }

        private class HandshakeTransportListener : PublishTransportListener
        {
            public HandshakeTransportListener(BayeuxClient bayeuxClient)
                : base(bayeuxClient)
            {
            }

            protected override async Task OnFailureAsync(Exception x, IList<IMessage> messages, CancellationToken cancellationToken = default)
            {
                await _bayeuxClient.UpdateBayeuxClientStateAsync(
                    oldState => new RehandshakingState(_bayeuxClient, oldState.HandshakeFields, oldState.Transport, oldState.NextBackoff()),
                    cancellationToken);
                await base.OnFailureAsync(x, messages, cancellationToken);
            }

            protected override async Task ProcessMessageAsync(IMutableMessage message, CancellationToken cancellationToken = default)
            {
                if (ChannelFields.META_HANDSHAKE.Equals(message.Channel))
                {
                    await _bayeuxClient.ProcessHandshakeAsync(message, cancellationToken);
                }
                else
                {
                    await base.ProcessMessageAsync(message, cancellationToken);
                }
            }
        }

        private class ConnectTransportListener : PublishTransportListener
        {
            public ConnectTransportListener(BayeuxClient bayeuxClient)
                : base(bayeuxClient)
            {
            }

            protected override async Task OnFailureAsync(Exception x, IList<IMessage> messages, CancellationToken cancellationToken = default)
            {
                await _bayeuxClient.UpdateBayeuxClientStateAsync(
                        oldState => new UnconnectedState(_bayeuxClient, oldState.HandshakeFields, oldState.Advice, oldState.Transport, oldState.ClientId, oldState.NextBackoff()),
                        cancellationToken);
                await base.OnFailureAsync(x, messages, cancellationToken);
            }

            protected override async Task ProcessMessageAsync(IMutableMessage message, CancellationToken cancellationToken = default)
            {
                if (ChannelFields.META_CONNECT.Equals(message.Channel))
                {
                    await _bayeuxClient.ProcessConnectAsync(message, cancellationToken);
                }
                else
                {
                    await base.ProcessMessageAsync(message, cancellationToken);
                }
            }
        }

        private class DisconnectTransportListener : PublishTransportListener
        {
            public DisconnectTransportListener(BayeuxClient bayeuxClient)
                : base(bayeuxClient)
            {
            }

            protected override async Task OnFailureAsync(Exception x, IList<IMessage> messages, CancellationToken cancellationToken = default)
            {
                await _bayeuxClient.UpdateBayeuxClientStateAsync(
                        oldState => new DisconnectedState(_bayeuxClient, oldState.Transport),
                        cancellationToken);
                await base.OnFailureAsync(x, messages, cancellationToken);
            }

            protected override async Task ProcessMessageAsync(IMutableMessage message, CancellationToken cancellationToken = default)
            {
                if (ChannelFields.META_DISCONNECT.Equals(message.Channel))
                {
                    await _bayeuxClient.ProcessDisconnectAsync(message, cancellationToken);
                }
                else
                {
                    await base.ProcessMessageAsync(message, cancellationToken);
                }
            }
        }

        public class BayeuxClientChannel : AbstractSessionChannel
        {
            protected BayeuxClient _bayeuxClient;

            public BayeuxClientChannel(BayeuxClient bayeuxClient, ChannelId channelId, long replayId)
                : base(channelId, replayId)
            {
                _bayeuxClient = bayeuxClient;
            }

            public override IClientSession Session => this as IClientSession;

            protected override async Task SendSubscribeAsync(CancellationToken cancellationToken = default)
            {
                var message = _bayeuxClient.NewMessage();
                message.Channel = ChannelFields.META_SUBSCRIBE;
                message[MessageFields.SUBSCRIPTION_FIELD] = Id;
                message.ReplayId = ReplayId;
                await _bayeuxClient.EnqueueSendAsync(message, cancellationToken);
            }

            protected override async Task SendUnsubscribeAsync(CancellationToken cancellationToken = default)
            {
                var message = _bayeuxClient.NewMessage();
                message.Channel = ChannelFields.META_UNSUBSCRIBE;
                message[MessageFields.SUBSCRIPTION_FIELD] = Id;
                message.ReplayId = ReplayId;
                await _bayeuxClient.EnqueueSendAsync(message, cancellationToken);
            }

            public override Task PublishAsync(object data, CancellationToken cancellationToken = default)
            {
                return PublishAsync(data, null, cancellationToken);
            }

            public override async Task PublishAsync(object data, string messageId, CancellationToken cancellationToken = default)
            {
                var message = _bayeuxClient.NewMessage();
                message.Channel = Id;
                message.Data = data;
                if (messageId != null)
                {
                    message.Id = messageId;
                }

                await _bayeuxClient.EnqueueSendAsync(message, cancellationToken);
            }
        }

        public abstract class BayeuxClientState
        {
            public State TypeValue;
            public IDictionary<string, object> HandshakeFields;
            public IDictionary<string, object> Advice;
            public ClientTransport Transport;
            public string ClientId;
            public int Backoff;
            protected BayeuxClient _bayeuxClient;

            protected BayeuxClientState(
                BayeuxClient bayeuxClient,
                State type,
                IDictionary<string, object> handshakeFields,
                IDictionary<string, object> advice,
                ClientTransport transport,
                string clientId,
                int backoff)
            {
                _bayeuxClient = bayeuxClient;
                TypeValue = type;
                HandshakeFields = handshakeFields;
                Advice = advice;
                Transport = transport;
                ClientId = clientId;
                Backoff = backoff;
            }

            public int Interval
            {
                get
                {
                    int result = 0;
                    if (Advice?.ContainsKey(MessageFields.INTERVAL_FIELD) == true)
                    {
                        result = ObjectConverter.ToInt32(Advice[MessageFields.INTERVAL_FIELD], result);
                    }

                    return result;
                }
            }

            public Task SendAsync(ITransportListener listener, IMutableMessage message, int clientTimeout = ClientTransport.DEFAULT_TIMEOUT, CancellationToken cancellationToken = default)
            {
                IList<IMutableMessage> messages = new List<IMutableMessage>
                {
                    message
                };
                return SendAsync(listener, messages, clientTimeout, cancellationToken);
            }

            public async Task SendAsync(ITransportListener listener, IList<IMutableMessage> messages, int clientTimeout = ClientTransport.DEFAULT_TIMEOUT, CancellationToken cancellationToken = default)
            {
                foreach (var message in messages)
                {
                    if (message.Id == null)
                    {
                        message.Id = _bayeuxClient.NewMessageId();
                    }

                    if (ClientId != null)
                    {
                        message.ClientId = ClientId;
                    }

                    if (!_bayeuxClient.ExtendSend(message))
                    {
                        messages.Remove(message);
                    }
                }

                if (messages.Count > 0)
                {
                    await Transport.SendAsync(listener, messages, clientTimeout, cancellationToken);
                }
            }

            public int NextBackoff()
            {
                return Math.Min(Backoff + _bayeuxClient.BackoffIncrement, _bayeuxClient.MaxBackoff);
            }

            public abstract bool IsUpdateableTo(BayeuxClientState newState);

            public virtual void Enter(State oldState)
            {
            }

            public abstract Task ExecuteAsync(CancellationToken cancellationToken = default);

            public State Type => TypeValue;

            public override string ToString()
            {
                return TypeValue.ToString();
            }
        }

        private class DisconnectedState : BayeuxClientState
        {
            public DisconnectedState(BayeuxClient bayeuxClient, ClientTransport transport)
                : base(bayeuxClient, State.DISCONNECTED, null, null, transport, null, 0)
            {
            }

            public override bool IsUpdateableTo(BayeuxClientState newState)
            {
                return newState.TypeValue == State.HANDSHAKING;
            }

            public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
            {
                Transport.Reset();
                await _bayeuxClient.TerminateAsync(cancellationToken);
            }
        }

        private class AbortedState : DisconnectedState
        {
            public AbortedState(BayeuxClient bayeuxClient, ClientTransport transport)
                : base(bayeuxClient, transport)
            {
            }

            public override Task ExecuteAsync(CancellationToken cancellationToken = default)
            {
                Transport.Abort();
                return base.ExecuteAsync(cancellationToken);
            }
        }

        private class HandshakingState : BayeuxClientState
        {
            public HandshakingState(BayeuxClient bayeuxClient, IDictionary<string, object> handshakeFields, ClientTransport transport)
                : base(bayeuxClient, State.HANDSHAKING, handshakeFields, null, transport, null, 0)
            {
            }

            public override bool IsUpdateableTo(BayeuxClientState newState)
            {
                return newState.TypeValue == State.REHANDSHAKING ||
                    newState.TypeValue == State.CONNECTING ||
                    newState.TypeValue == State.DISCONNECTED;
            }

            public override void Enter(State oldState)
            {
                // Always reset the subscriptions when a handshake has been requested.
                _bayeuxClient.ResetSubscriptions();
            }

            public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
            {
                // The state could change between now and when sendHandshake() runs;
                // in this case the handshake message will not be sent and will not
                // be failed, because most probably the client has been disconnected.
                await _bayeuxClient.SendHandshakeAsync(cancellationToken);
            }
        }

        private class RehandshakingState : BayeuxClientState
        {
            public RehandshakingState(
                BayeuxClient bayeuxClient,
                IDictionary<string, object> handshakeFields,
                ClientTransport transport,
                int backoff)
                : base(bayeuxClient, State.REHANDSHAKING, handshakeFields, null, transport, null, backoff)
            {
            }

            public override bool IsUpdateableTo(BayeuxClientState newState)
            {
                return newState.TypeValue == State.CONNECTING ||
                    newState.TypeValue == State.REHANDSHAKING ||
                    newState.TypeValue == State.DISCONNECTED;
            }

            public override void Enter(State oldState)
            {
                // Reset the subscriptions if this is not a failure from a requested handshake.
                // Subscriptions may be queued after requested handshakes.
                if (oldState != State.HANDSHAKING)
                {
                    // Reset subscriptions if not queued after initial handshake
                    _bayeuxClient.ResetSubscriptions();
                }
            }

            public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
            {
                await _bayeuxClient.ScheduleHandshakeAsync(Interval, Backoff, cancellationToken);
            }
        }

        private class ConnectingState : BayeuxClientState
        {
            public ConnectingState(
                BayeuxClient bayeuxClient,
                IDictionary<string, object> handshakeFields,
                IDictionary<string, object> advice,
                ClientTransport transport,
                string clientId)
                : base(bayeuxClient, State.CONNECTING, handshakeFields, advice, transport, clientId, 0)
            {
            }

            public override bool IsUpdateableTo(BayeuxClientState newState)
            {
                return newState.TypeValue == State.CONNECTED ||
                    newState.TypeValue == State.UNCONNECTED ||
                    newState.TypeValue == State.REHANDSHAKING ||
                    newState.TypeValue == State.DISCONNECTING ||
                    newState.TypeValue == State.DISCONNECTED;
            }

            public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
            {
                // Send the messages that may have queued up before the handshake completed
                await _bayeuxClient.SendBatchAsync(cancellationToken);
                await _bayeuxClient.ScheduleConnectAsync(Interval, Backoff, cancellationToken: cancellationToken);
            }
        }

        private class ConnectedState : BayeuxClientState
        {
            public ConnectedState(
                BayeuxClient bayeuxClient,
                IDictionary<string, object> handshakeFields,
                IDictionary<string, object> advice,
                ClientTransport transport,
                string clientId)
                : base(bayeuxClient, State.CONNECTED, handshakeFields, advice, transport, clientId, 0)
            {
            }

            public override bool IsUpdateableTo(BayeuxClientState newState)
            {
                return newState.TypeValue == State.CONNECTED ||
                    newState.TypeValue == State.UNCONNECTED ||
                    newState.TypeValue == State.REHANDSHAKING ||
                    newState.TypeValue == State.DISCONNECTING ||
                    newState.TypeValue == State.DISCONNECTED;
            }

            public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
            {
                var adviceTimeoutField = _bayeuxClient?._bayeuxClientState?.Advice[MessageFields.TIMEOUT_FIELD];

                if (!(adviceTimeoutField is null))
                {
                    int adviceTimeoutValue = Convert.ToInt32(adviceTimeoutField);
                    if (adviceTimeoutValue != 0)
                    {
                        await _bayeuxClient?.ScheduleConnectAsync(Interval, Backoff, adviceTimeoutValue, cancellationToken);
                        return;
                    }
                }

                await _bayeuxClient?.ScheduleConnectAsync(Interval, Backoff, cancellationToken: cancellationToken);
            }
        }

        private class UnconnectedState : BayeuxClientState
        {
            public UnconnectedState(
                BayeuxClient bayeuxClient,
                IDictionary<string, object> handshakeFields,
                IDictionary<string, object> advice,
                ClientTransport transport,
                string clientId,
                int backoff)
                : base(bayeuxClient, State.UNCONNECTED, handshakeFields, advice, transport, clientId, backoff)
            {
            }

            public override bool IsUpdateableTo(BayeuxClientState newState)
            {
                return newState.TypeValue == State.CONNECTED ||
                    newState.TypeValue == State.UNCONNECTED ||
                    newState.TypeValue == State.REHANDSHAKING ||
                    newState.TypeValue == State.DISCONNECTED;
            }

            public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
            {
                await _bayeuxClient.ScheduleConnectAsync(Interval, Backoff, cancellationToken: cancellationToken);
            }
        }

        private class DisconnectingState : BayeuxClientState
        {
            public DisconnectingState(BayeuxClient bayeuxClient, ClientTransport transport, string clientId)
                : base(bayeuxClient, State.DISCONNECTING, null, null, transport, clientId, 0)
            {
            }

            public override bool IsUpdateableTo(BayeuxClientState newState)
            {
                return newState.TypeValue == State.DISCONNECTED;
            }

            public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
            {
                var message = _bayeuxClient.NewMessage();
                message.Channel = ChannelFields.META_DISCONNECT;
                await SendAsync(_bayeuxClient._disconnectListener, message, cancellationToken: cancellationToken);
            }
        }
    }
}
