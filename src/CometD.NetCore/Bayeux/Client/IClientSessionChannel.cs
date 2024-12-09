using System.Threading;
using System.Threading.Tasks;

namespace CometD.NetCore.Bayeux.Client
{
    /// <summary> <p>A client side channel representation.</p>
    /// <p>A {@link ClientSessionChannel} is scoped to a particular {@link ClientSession}
    /// that is obtained by a call to {@link ClientSession#getChannel(String)}.</p>
    /// <p>Typical usage examples are:</p>
    /// <pre>
    /// clientSession.getChannel("/foo/bar").subscribe(mySubscriptionListener);
    /// clientSession.getChannel("/foo/bar").publish("Hello");
    /// clientSession.getChannel("/meta/*").addListener(myMetaChannelListener);
    /// </pre>
    ///
    /// </summary>
    public interface IClientSessionChannel : IChannel
    {
        /// <summary>
        /// The Session Instance.
        /// </summary>
        /// <returns> the client session associated with this channel.
        /// </returns>
        IClientSession Session { get; }

        /// <summary>
        /// Adds Client Session Channel Listener.
        /// </summary>
        /// <param name="listener">the listener to add.
        /// </param>
        void AddListener(IClientSessionChannelListener listener);

        /// <summary> Equivalent to {@link #publish(Object, Object) publish(data, null)}.</summary>
        /// <param name="data">the data to publish.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        Task PublishAsync(object data, CancellationToken cancellationToken = default);

        /// <summary> Publishes the given {@code data} to this channel,
        /// optionally specifying the {@code messageId} to set on the
        /// publish message.
        /// </summary>
        /// <param name="data">the data to publish.
        /// </param>
        /// <param name="messageId">the message id to set on the message, or null to let the
        /// implementation choose the message id.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <seealso cref="IMessage.getId()">
        /// </seealso>
        Task PublishAsync(object data, string messageId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes Client Session Channel Listener.
        /// </summary>
        /// <param name="listener">the listener to remove.
        /// </param>
        void RemoveListener(IClientSessionChannelListener listener);

        /// <summary>
        /// Subscribes the listener.
        /// </summary>
        /// <param name="listener">The listener.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task SubscribeAsync(IMessageListener listener, CancellationToken cancellationToken = default);

        /// <summary>
        /// Unsubscribes the specified listener.
        /// </summary>
        /// <param name="listener">The listener.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task UnsubscribeAsync(IMessageListener listener, CancellationToken cancellationToken = default);

        /// <summary>
        /// Unsubscribes all listeners.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task UnsubscribeAsync(CancellationToken cancellationToken = default);
    }

    /// <summary> <p>Represents a listener on a {@link ClientSessionChannel}.</p>
    /// <p>Sub-interfaces specify the exact semantic of the listener.</p>
    /// </summary>
    public interface IClientSessionChannelListener : IBayeuxListener
    {
    }
}
