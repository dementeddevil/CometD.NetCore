using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using CometD.NetCore.Bayeux;
using CometD.NetCore.Bayeux.Client;

using Microsoft.Extensions.Logging;

namespace CometD.NetCore.Common
{
    /// <summary> <p>A channel scoped to a {@link ClientSession}.</p></summary>
    public abstract class AbstractSessionChannel : IClientSessionChannel
    {
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, object> _attributes = new ConcurrentDictionary<string, object>();
        private readonly List<IClientSessionChannelListener> _listeners = new List<IClientSessionChannelListener>();
        private readonly List<IMessageListener> _subscriptions = new List<IMessageListener>();

        private int _subscriptionCount;

        public long ReplayId { get; private set; }

        protected AbstractSessionChannel(ChannelId id, long replayId)
        {
            ReplayId = replayId;
            ChannelId = id;
        }

        protected AbstractSessionChannel(ChannelId id, long replayId, ILogger logger)
            : this(id, replayId)
        {
            _logger = logger;
        }

        public abstract IClientSession Session { get; }

        public void AddListener(IClientSessionChannelListener listener)
        {
            _listeners.Add(listener);
        }

        public abstract Task PublishAsync(object param1, CancellationToken cancellationToken = default);

        public abstract Task PublishAsync(object param1, string param2, CancellationToken cancellationToken = default);

        public void RemoveListener(IClientSessionChannelListener listener)
        {
            _listeners.Remove(listener);
        }

        public async Task SubscribeAsync(IMessageListener listener, CancellationToken cancellationToken = default)
        {
            _subscriptions.Add(listener);

            _subscriptionCount++;
            var count = _subscriptionCount;
            if (count == 1)
            {
                await SendSubscribeAsync(cancellationToken);
            }
        }

        public async Task UnsubscribeAsync(IMessageListener listener, CancellationToken cancellationToken = default)
        {
            _subscriptions.Remove(listener);

            _subscriptionCount--;
            if (_subscriptionCount < 0)
            {
                _subscriptionCount = 0;
            }

            var count = _subscriptionCount;
            if (count == 0)
            {
                await SendUnsubscribeAsync(cancellationToken);
            }
        }

        public async Task UnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            foreach (var listener in new List<IMessageListener>(_subscriptions))
            {
                await UnsubscribeAsync(listener, cancellationToken);
            }
        }

        public ICollection<string> AttributeNames => _attributes.Keys;

        public ChannelId ChannelId { get; }

        public bool DeepWild => ChannelId.DeepWild;

        public string Id => ChannelId.ToString();

        public bool Meta => ChannelId.IsMeta();

        public bool Service => ChannelId.IsService();

        public bool Wild => ChannelId.Wild;

        public object GetAttribute(string name)
        {
            _attributes.TryGetValue(name, out var obj);
            return obj;
        }

        public object RemoveAttribute(string name)
        {
            var old = GetAttribute(name);

            if (_attributes.TryRemove(name, out var va))
            {
                return va;
            }

            return old;
        }

        public void SetAttribute(string name, object val)
        {
            _attributes[name] = val;
        }

        public async Task NotifyMessageListenersAsync(IMessage message, CancellationToken cancellationToken)
        {
            foreach (var listener in _listeners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // TODO: Support parallelisation of notifications
                //  via NotificationStrategy class
                if (listener is IMessageListener)
                {
                    try
                    {
                        await ((IMessageListener)listener).OnMessage(this, message, cancellationToken);
                    }
                    catch (Exception e)
                    {
                        _logger?.LogError($"{e}");
                    }
                }
            }

            var list = new List<IMessageListener>(_subscriptions);
            foreach (IClientSessionChannelListener listener in list)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // TODO: Support parallelisation of notifications
                //  via NotificationStrategy class
                if (listener is IMessageListener)
                {
                    if (message.Data != null)
                    {
                        try
                        {
                            await ((IMessageListener)listener).OnMessage(this, message, cancellationToken);
                        }
                        catch (Exception e)
                        {
                            _logger?.LogError($"{e}");
                        }
                    }
                }
            }
        }

        public void ResetSubscriptions()
        {
            foreach (var listener in new List<IMessageListener>(_subscriptions))
            {
                _subscriptions.Remove(listener);
                _subscriptionCount--;
            }
        }

        public override string ToString()
        {
            return ChannelId.ToString();
        }

        protected abstract Task SendSubscribeAsync(CancellationToken cancellationToken = default);

        protected abstract Task SendUnsubscribeAsync(CancellationToken cancellationToken = default);
    }
}
