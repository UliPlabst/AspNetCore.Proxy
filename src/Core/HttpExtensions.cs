using System;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AspNetCore.Proxy
{
#pragma warning disable CS1591
    public static class PublicExtensions
    {
        public static async Task ExecuteProxy(this HttpContext context, string uri, ProxyOptions options = null)
        {
            await context.ExecuteProxyOperationAsync(uri, options);
        }
    }
#pragma warning restore CS1591

    internal static class HttpExtensions
    {
        internal static async Task ExecuteHttpProxyOperationAsync(this HttpContext context, string uri, ProxyOptions options = null)
        {
            // If `true`, this proxy call has been intercepted.
            if(options?.Intercept != null && await options.Intercept(context))
                return;

            var proxiedRequest = context.CreateProxiedHttpRequest(uri, options?.ShouldAddForwardedHeaders ?? true);

            if(options?.BeforeSend != null)
                await options.BeforeSend(context, proxiedRequest).ConfigureAwait(false);
                var proxiedResponse = await context
                .SendProxiedHttpRequestAsync(proxiedRequest, options?.HttpClientName ?? Helpers.HttpProxyClientName)
                .ConfigureAwait(false);

            if(options?.AfterReceive != null)
                await options.AfterReceive(context, proxiedResponse).ConfigureAwait(false);
            await context.WriteProxiedHttpResponseAsync(proxiedResponse).ConfigureAwait(false);
        }

        private static HttpRequestMessage CreateProxiedHttpRequest(this HttpContext context, string uriString, bool shouldAddForwardedHeaders)
        {
            var uri = new Uri(uriString);
            var request = context.Request;

            var requestMessage = new HttpRequestMessage();
            var requestMethod = request.Method;

            // Write to request content, when necessary.
            if (!HttpMethods.IsGet(requestMethod) &&
                !HttpMethods.IsHead(requestMethod) &&
                !HttpMethods.IsDelete(requestMethod) &&
                !HttpMethods.IsTrace(requestMethod))
            {
                var streamContent = new StreamContent(request.Body);
                requestMessage.Content = streamContent;
            }

            // Copy the request headers.
            foreach (var header in context.Request.Headers)
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());

            // Add forwarded headers.
            if(shouldAddForwardedHeaders)
                AddForwardedHeadersToHttpRequest(context, requestMessage);

            // Set destination and method.
            requestMessage.Headers.Host = uri.Authority;
            requestMessage.RequestUri = uri;
            requestMessage.Method = new HttpMethod(request.Method);

            return requestMessage;
        }

        private static Task<HttpResponseMessage> SendProxiedHttpRequestAsync(this HttpContext context, HttpRequestMessage message, string httpClientName)
        {
            return context.RequestServices
                .GetService<IHttpClientFactory>()
                .CreateClient(httpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }

        private static Task WriteProxiedHttpResponseAsync(this HttpContext context, HttpResponseMessage responseMessage)
        {
            var response = context.Response;

            response.StatusCode = (int)responseMessage.StatusCode;
            foreach (var header in responseMessage.Headers)
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in responseMessage.Content.Headers)
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }

            response.Headers.Remove("transfer-encoding");

            return responseMessage.Content.CopyToAsync(response.Body);
        }

        private static void AddForwardedHeadersToHttpRequest(HttpContext context, HttpRequestMessage requestMessage)
        {
            var request = context.Request;
            var connection = context.Connection;

            var host = request.Host.ToString();
            var protocol = request.Scheme;

            var localIp = connection.LocalIpAddress?.ToString();
            var isLocalIpV6 = connection.LocalIpAddress?.AddressFamily == AddressFamily.InterNetworkV6;

            var remoteIp = context.Connection.RemoteIpAddress?.ToString();
            var isRemoteIpV6 = connection.RemoteIpAddress?.AddressFamily == AddressFamily.InterNetworkV6;

            if(remoteIp != null)
                requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-For", remoteIp);
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Proto", protocol);
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);

            // Fix IPv6 IPs for the `Forwarded` header.
            var forwardedHeader = new StringBuilder($"proto={protocol};host={host};");

            if(localIp != null)
            {
                if(isLocalIpV6)
                    localIp = $"\"[{localIp}]\"";

                forwardedHeader.Append($"by={localIp};");
            }

            if(remoteIp != null)
            {
                if(isRemoteIpV6)
                    remoteIp = $"\"[{remoteIp}]\"";

                forwardedHeader.Append($"for={remoteIp};");
            }

            requestMessage.Headers.TryAddWithoutValidation("Forwarded", forwardedHeader.ToString());
        }
    }
}