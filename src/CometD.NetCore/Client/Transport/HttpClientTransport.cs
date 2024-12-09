using System.Collections.Generic;

namespace CometD.NetCore.Client.Transport
{
    public abstract class HttpClientTransport : ClientTransport
    {
        private readonly IDictionary<string, string> _headers = new Dictionary<string, string>();

        protected HttpClientTransport(string name, IDictionary<string, object> options, IDictionary<string, string> headers)
            : base(name, options)
        {
            AddHeaders(headers);
        }

        public string Url
        {
            get; set;
        }

        protected internal void AddHeaders(IDictionary<string, string> headers)
        {
            foreach (var header in headers)
            {
                _headers.Add(header.Key, header.Value);
            }
        }

        protected IDictionary<string, string> GetHeaderCollection()
        {
            return _headers;
        }
    }
}
