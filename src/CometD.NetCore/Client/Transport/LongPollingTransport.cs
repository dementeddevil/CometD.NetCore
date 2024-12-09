using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using CometD.NetCore.Bayeux;
using CometD.NetCore.Common;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

namespace CometD.NetCore.Client.Transport
{
    public class LongPollingTransport : HttpClientTransport
    {
        private static ILogger _logger;

        private readonly List<TransportExchange> _exchanges = new List<TransportExchange>();
        private readonly HashSet<TransportExchange> _transmissions = new HashSet<TransportExchange>();
        private readonly object _lockObject = new object();
        private readonly IHttpClientFactory _httpClientFactory;
        private bool _appendMessageType;

        public LongPollingTransport(
            IHttpClientFactory httpClientFactory,
            IDictionary<string, object> options,
            IDictionary<string, string> headers)
            : base("long-polling", options, headers)
        {
            _httpClientFactory = httpClientFactory;
        }

        public LongPollingTransport(
            IHttpClientFactory httpClientFactory,
            IDictionary<string, object> options,
            IDictionary<string, string> headers,
            ILogger logger)
            : this(httpClientFactory, options, headers)
        {
            _logger = logger;
        }

        public override bool Accept(string version)
        {
            return true;
        }

        public override void Init()
        {
            base.Init();

            // _aborted = false;
            var uriRegex = new Regex("(^https?://(([^:/\\?#]+)(:(\\d+))?))?([^\\?#]*)(.*)?");
            var uriMatch = uriRegex.Match(Url);
            if (uriMatch.Success)
            {
                var afterPath = uriMatch.Groups[7].ToString();
                _appendMessageType = afterPath == null || afterPath.Trim().Length == 0;
            }
        }

        public override void Abort()
        {
            // _aborted = true;
            lock (_lockObject)
            {
                foreach (var exchange in _exchanges)
                {
                    exchange.Abort();
                }

                _exchanges.Clear();
            }
        }

        public override void Reset()
        {
        }

        private async Task PerformNextRequestAsync(CancellationToken cancellationToken = default)
        {
            var ok = false;
            TransportExchange nextRequest = null;

            lock (_lockObject)
            {
                if (_exchanges.Count > 0)
                {
                    ok = true;
                    nextRequest = _exchanges[0];
                    _exchanges.Remove(nextRequest);
                    _transmissions.Add(nextRequest);
                }
            }

            if (ok && nextRequest != null)
            {
                // Get HTTP client and pull message from exchange
                var httpClient = _httpClientFactory.CreateClient("cometd");
                try
                {
                    // Notify hook we are sending the next batch of messages
                    await nextRequest.Listener.OnSendingAsync(ObjectConverter.ToListOfIMessage(nextRequest.Messages), cancellationToken);

                    // Initiate the next request across the HTTP client
                    nextRequest.AbortAfter(nextRequest.RequestTimeout);
                    nextRequest.IsSending = true;
                    using (var httpResponse = await httpClient.SendAsync(nextRequest.Request, nextRequest.CancellationToken))
                    {
                        nextRequest.IsSending = false;

                        try
                        {
                            _logger?.LogDebug("Received message(s).");
#if NET7_0_OR_GREATER
                            var stringContent = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
#else
                            var stringContent = await httpResponse.Content.ReadAsStringAsync();
#endif
                            nextRequest.Messages = DictionaryMessage.ParseMessages(stringContent);
                        }
                        catch (Exception e)
                        {
                            _logger?.LogError(e, $"Failed to parse the messages json: {e}");
                        }
                    }

                    await nextRequest.Listener.OnMessagesAsync(nextRequest.Messages, cancellationToken);
                }
                catch (Exception e)
                {
                    nextRequest.IsSending = false;
                    await nextRequest.Listener.OnExceptionAsync(e, ObjectConverter.ToListOfIMessage(nextRequest.Messages), cancellationToken);
                }
                finally
                {
                    await nextRequest.DisposeAsync();
                }
            }
        }

        public async Task AddRequest(TransportExchange request, CancellationToken cancellationToken = default)
        {
            lock (_lockObject)
            {
                _exchanges.Add(request);
            }

            await PerformNextRequestAsync(cancellationToken);
        }

        public async Task RemoveRequest(TransportExchange request, CancellationToken cancellationToken = default)
        {
            lock (_lockObject)
            {
                _exchanges.Remove(request);
                _transmissions.Remove(request);
            }

            await PerformNextRequestAsync(cancellationToken);
        }

        public override async Task SendAsync(ITransportListener listener, IList<IMutableMessage> messages, int requestTimeout, CancellationToken cancellationToken = default)
        {
            _logger?.LogDebug($"send({messages.Count} message(s)");

            var url = Url;

            if (_appendMessageType &&
                messages.Count == 1 &&
                messages[0].Meta)
            {
                var type = messages[0].Channel.Substring(ChannelFields.META.Length);
                if (url.EndsWith("/"))
                {
                    url = url.Substring(0, url.Length - 1);
                }

                url += type;
            }

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            foreach (var header in GetHeaderCollection())
            {
                httpRequest.Headers.Add(header.Key, header.Value);
            }

            var content = JsonConvert.SerializeObject(ObjectConverter.ToListOfDictionary(messages));
            httpRequest.Content = new StringContent(content, Encoding.UTF8, "application/json");

            _logger?.LogDebug($"Send: {content}");

            // Determine request timeout for this request
            if (requestTimeout < DEFAULT_TIMEOUT)
            {
                requestTimeout += GetOption(MAX_NETWORK_DELAY_OPTION, 5000);
            }

            var exchange = new TransportExchange(this, listener, messages, httpRequest, requestTimeout);

            await AddRequest(exchange, cancellationToken);
        }

        public override bool IsSending
        {
            get
            {
                lock (_lockObject)
                {
                    if (_exchanges.Count > 0)
                    {
                        return true;
                    }

                    foreach (var transmission in _transmissions)
                    {
                        if (transmission.IsSending)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }
        }

        public class TransportExchange
        {
            private readonly LongPollingTransport _parent;
            private readonly CancellationTokenSource _cancellationTokenSource =
                new CancellationTokenSource();

            public TransportExchange(
                LongPollingTransport parent,
                ITransportListener listener,
                IList<IMutableMessage> messages,
                HttpRequestMessage request,
                int requestTimeout)
            {
                _parent = parent;
                Listener = listener;
                Messages = messages;
                Request = request;
                RequestTimeout = requestTimeout;
                IsSending = false;
            }

            public int RequestTimeout { get; }

            public HttpRequestMessage Request { get; }

            public ITransportListener Listener { get; }

            public IList<IMutableMessage> Messages { get; internal set; }

            public bool IsSending { get; internal set; }

            public CancellationToken CancellationToken => _cancellationTokenSource.Token;

            public async Task DisposeAsync()
            {
                _cancellationTokenSource.Dispose();
                await _parent.RemoveRequest(this, CancellationToken.None);
            }

            public void Abort()
            {
                _cancellationTokenSource.Cancel();
            }

            public void AbortAfter(int timeout)
            {
                _cancellationTokenSource.CancelAfter(timeout);
            }
        }
    }
}
