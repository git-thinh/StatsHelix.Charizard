﻿using ActuallyWorkingWebSockets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StatsHelix.Charizard
{
    public class HttpServer
    {
        private const int SocketBacklog = 1024;
        private const string ServerHeader = "Server: StatsHelix Charizard v1.0";

        public IPEndPoint Endpoint { get; }

        /// <summary>
        /// This event is raised when an unexpected exception occurs.
        /// Unexpected exceptions are network failures or HTTP protocol
        /// violations - basically anything that instantly kills a client connection.
        /// </summary>
        public event Action<Exception> UnexpectedException;

        /// <summary>
        /// Gets or sets the action exception handler.
        ///
        /// It handles exception thrown from controller actions.
        /// By default, it generates a 500 response.
        /// </summary>
        /// <value>The action exception handler.</value>
        public Func<Exception, HttpResponse> ActionExceptionHandler { get; set; } = DefaultActionExceptionHandler;

        /// <summary>
        /// An user-managed object to give context to the controllers.
        /// </summary>
        public object UserContext { get; set; }

        public static HttpResponse DefaultActionExceptionHandler(Exception e)
        {
#if DEBUG
            return HttpResponse.String("Internal server error: " + e, HttpStatus.InternalServerError);
#else
            return HttpResponse.String("Internal server error.", HttpStatus.InternalServerError);
#endif
        }

        private readonly RoutingManager RoutingManager;

        public HttpServer(IPEndPoint endpoint, params Assembly[] controllerAssemblies)
        {
            Endpoint = endpoint;
            RoutingManager = new RoutingManager(this, controllerAssemblies);
        }

        public async Task Run()
        {
            var socket = new Socket(Endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(Endpoint);
            socket.Listen(SocketBacklog);

            while (true)
                HandleClient(await AcceptAsync(socket));
        }

        private static Task<Socket> AcceptAsync(Socket socket)
        {
            return Task.Factory.FromAsync(socket.BeginAccept, socket.EndAccept, null);
        }

        private static readonly string[] StatusStrings = new[]
        {
            "HTTP/1.1 200 OK",
            "HTTP/1.1 204 No Content",
            "HTTP/1.1 205 Reset Content",
            "HTTP/1.1 206 Partial Content",
            "HTTP/1.1 301 Moved Permanently",
            "HTTP/1.1 302 Found",
            "HTTP/1.1 303 See other",
            "HTTP/1.1 304 Not Modified",
            "HTTP/1.1 400 Bad Request",
            "HTTP/1.1 403 Forbidden",
            "HTTP/1.1 404 Not Found",
            "HTTP/1.1 413 Request Entity Too Large",
            "HTTP/1.1 500 Internal Server Error",
        };
        private static readonly string[] ContentTypeStrings = new[]
        {
            "application/octet-stream",
            "application/json",
            "text/plain",
            "text/html",
        };

        private static Stream MakeSaneNetworkStream(Socket socket, out Stream readStream)
        {
            // This already flushes the underlying stream which is not what we want.
            // However, it doesn't matter anyways because flushing a NetworkStream is a NOP.
            // In fact, there is no way to control precise TCP packet behavior from C#.
            // The only solution that might work is to move buffering from kernel to userspace
            // by setting Socket.NoDelay and using a BufferedStream.
            //
            // The world is awful.
            socket.NoDelay = true;

            // It's important that we do NOT buffer reads though, they can (and should) go
            // straight through.
            readStream = new NetworkStream(socket, true);

            return new BufferedStream(readStream);
        }

        private async void HandleClient(Socket partner)
        {
            try
            {
                Stream readStream;
                using (var writeStream = MakeSaneNetworkStream(partner, out readStream))
                using (var reader = new HttpRequestReaderStream(readStream))
                using (var writer = new StreamWriter(writeStream, new UTF8Encoding(false), 4096) { NewLine = "\r\n", AutoFlush = false })
                {
                    var headers = new List<HttpHeader>();
                    while (true)
                    {
                        var questing = new StringSegment(await reader.ReadLineAsync());
                        if (questing.Empty)
                            break;

                        headers.Clear();
                        while (true)
                        {
                            var header = await reader.ReadLineAsync();
                            if (String.IsNullOrEmpty(header))
                                break;

                            var colon = header.IndexOf(':');
                            var name = header.Substring(0, colon);
                            var value = header.Substring(colon + 2); // skip colon + space

                            headers.Add(new HttpHeader { Name = name.ToLowerInvariant(), Value = value });
                        }

                        var space1 = questing.IndexOf(' ');
                        var space2 = questing.IndexOf(' ', space1 + 1); // if space1 is -1 this is fine as well

                        if ((space1 > 0) && (space2 > 0))
                        {
                            var method = questing.Substring(0, space1);
                            var path = questing.Substring(space1 + 1, space2 - space1 - 1);
                            var version = questing.Substring(space2 + 1);
                            if (version == "HTTP/1.1" && path[0] == '/')
                            {
                                path = path.Substring(1);

                                HttpMethod? prettyMethod = null;
                                bool hasBody = false;
                                if (method == "GET")
                                {
                                    prettyMethod = HttpMethod.Get;
                                }
                                else if (method == "POST")
                                {
                                    prettyMethod = HttpMethod.Post;
                                    hasBody = true;
                                }

                                if (prettyMethod.HasValue)
                                {
                                    var request = new HttpRequest(prettyMethod.Value, path, headers, Encoding.UTF8, this);

                                    bool isWebSocket = false;
                                    if (hasBody)
                                    {
                                        int bodyLength;
                                        if (!int.TryParse(request.GetHeader("content-length"), out bodyLength))
                                            throw new InvalidOperationException("Request has body but no content-length given!");

                                        // read body into byte array (not sure about this tho)
                                        var body = new byte[bodyLength];
                                        int read = 1337;
                                        for (int i = 0; (i < body.Length) && (read != 0); i += read)
                                            read = await reader.ReadAsync(body, i, body.Length - i);
                                        request.Body = body;
                                    }
                                    else if (prettyMethod == HttpMethod.Get)
                                    {
                                        var connection = request.GetHeader("connection")?.ToLowerInvariant();
                                        isWebSocket = connection == "upgrade" || connection == "websocket";
                                    }

                                    var response = await RoutingManager.DispatchRequest(request);

                                    if (response.WebSocketHandler != null)
                                    {
                                        if (!isWebSocket)
                                            throw new InvalidOperationException("WebSocket provided but not requested.");

                                        await HandleWebSocket(partner, readStream, writer, response.WebSocketHandler, request);
                                        return;
                                    }

                                    await writer.WriteLineAsync(StatusStrings[(int)response.Status]);
                                    await writer.WriteLineAsync(ServerHeader);

                                    if (response.ContentType != ContentType.Custom)
                                    {
                                        await writer.WriteAsync("Content-Type: ");
                                        await writer.WriteLineAsync(ContentTypeStrings[(int)response.ContentType]);
                                    }

                                    if (response.ExtraHeaders != null)
                                    {
                                        foreach (var header in response.ExtraHeaders)
                                        {
                                            await writer.WriteAsync(header.Name);
                                            await writer.WriteAsync(": ");
                                            await writer.WriteLineAsync(header.Value);
                                        }
                                    }

                                    await writer.WriteAsync("Content-Length: ");
                                    await writer.WriteLineAsync(response.Content.Count.ToString());

                                    await writer.WriteLineAsync();
                                    // This flushes the BufferedStream as well which is NOT what we want.
                                    // Solving this would require us to either reimplement StreamWriter or
                                    // to wrap the BufferedStream in another Stream (because it's sealed).
                                    // Worth it? I don't know.
                                    await writer.FlushAsync();

                                    await writeStream.WriteAsync(response.Content.Array, response.Content.Offset, response.Content.Count);
                                    await writeStream.FlushAsync();

                                    // All is well - we can loop (keepalive).
                                    continue;
                                }
                            }
                        }

                        // If we reach this, something is weird/wrong.
                        // "Bye, have a great day!"
                        await writer.WriteLineAsync(StatusStrings[(int)HttpStatus.BadRequest]);
                        await writer.WriteLineAsync(ServerHeader);
                        await writer.WriteLineAsync("Connection: close");
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                UnexpectedException?.Invoke(e);
            }
        }

        public async Task HandleWebSocket(Socket client, Stream netStream, StreamWriter writer, Func<WebSocketSession, Task> handler, HttpRequest request)
        {
            // calculate key
            // yes, the RFC defines a hardcoded value
            // and wow, this is surprisingly easy in C#
            var acceptKey = Convert.ToBase64String(System.Security.Cryptography.SHA1.Create().ComputeHash(
                Encoding.ASCII.GetBytes(request.GetHeader("sec-websocket-key") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            await writer.WriteLineAsync("HTTP/1.1 101 Switching Protocols");
            await writer.WriteLineAsync("Upgrade: websocket");
            await writer.WriteLineAsync("Connection: Upgrade");
            await writer.WriteLineAsync("Sec-WebSocket-Accept: " + acceptKey);
            await writer.WriteLineAsync();
            await writer.FlushAsync();

            // we no longer need the streamwriter, SWITCHING PROTOCOLS NOW
            // ---------------------------------------------------------------

            using (var session = new WebSocketSession(netStream, request.Path.ToString(), (IPEndPoint)client.RemoteEndPoint))
                await handler(session);
        }
    }
}
