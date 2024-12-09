using System.Threading;
using System.Threading.Tasks;

namespace CometD.NetCore.Bayeux.Client
{
    /// <summary> A listener for messages on a {@link ClientSessionChannel}.</summary>
    public interface IMessageListener : IClientSessionChannelListener
    {
        /// <summary> Callback invoked when a message is received on the given {@code channel}.</summary>
        /// <param name="channel">the channel that received the message.</param>
        /// <param name="message">the message received.</param>
        /// <param name="cancellationToken"></param>
        Task OnMessage(IClientSessionChannel channel, IMessage message, CancellationToken cancellationToken);
    }
}
