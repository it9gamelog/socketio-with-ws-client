using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Threading;
using System.Net;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net.Http;

namespace IT9GameLog.SocketIo
{
    public class PingEventArgs : EventArgs
    {
        public int SentAtTick { get; private set; }
        public int ReceivedAtTick { get; private set; }
        internal PingEventArgs(int sent, int received) : base()
        {
            SentAtTick = sent;
            ReceivedAtTick = received;
        }
    }

    public class IncomingEventEventArgs : EventArgs
    {
        static Regex eventRegex = new Regex("^(?:(/[^,]*),)?([0-9]{1,19})?(.*)$");

        /// <summary>
        /// Namespace
        /// </summary>
        public string Nsp { get; private set; }

        /// <summary>
        /// Id for ACK according to the socket.io protocol (Not handled by this)
        /// </summary>
        public long? Id { get; private set; }

        /// <summary>
        /// JSON Payload
        /// </summary>
        public string Payload { get; private set; }

        internal IncomingEventEventArgs(string wirePayload) : base()
        {
            var m = eventRegex.Match(wirePayload);
            if (m.Success)
            {
                Nsp = m.Groups[1].Success ? m.Groups[0].Value : "/";
                Id = m.Groups[2].Success ? (long?)Convert.ToInt64(m.Groups[2].Value, CultureInfo.InvariantCulture) : null;
                Payload = m.Groups[3].Value;
            }
            else
            {
                Payload = string.Empty;
            }
        }
    }

    /// <summary>
    /// A client for connecting to socket.io server and support the default protocol and very raw data exchange
    /// </summary>
    class SocketIoWithWsClient : IDisposable
    {
        private static Random random = new Random();
        private static string RandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            return new string(Enumerable.Repeat(chars, length)
              .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public delegate void PingEventHandler(object sender, PingEventArgs e);
        /// <summary>
        /// When a engine.io ping-pong is exchanged
        /// </summary>
        public event EventHandler<PingEventArgs> Ping;

        public delegate void IncomingEventEventHandler(object sender, IncomingEventEventArgs e);
        /// <summary>
        /// Incoming socket.io event. Only basic event is supported now, ack, binary event and binary ack is ignored.
        /// </summary>
        public event EventHandler<IncomingEventEventArgs> IncomingEvent;

        /// <summary>
        /// When the socket is dead after succesfully connected.
        /// 
        /// Note: connecting failure would not raise this event.
        /// </summary>
        public event EventHandler Disconnect;

        private ClientWebSocket innerWebSocket;

        private int state;
        private const int CREATED = 0;
        private const int CONNECTING = 1;
        private const int CONNECTED = 2;
        private const int DISPOSED = 3;

        private static byte[] PING_STRING = Encoding.UTF8.GetBytes("2");
        private static byte[] PONG_STRING = Encoding.UTF8.GetBytes("3");


        public TimeSpan PingInterval { get; set; }
        public TimeSpan PingTimeout { get; set; }
        public string SessionId { get; private set; }
        public int BufferSize { get; private set; }

        private readonly CancellationTokenSource cts;

        public SocketIoWithWsClient(int bufferSize = 1024*16)
        {
            cts = new CancellationTokenSource();
            PingInterval = TimeSpan.FromSeconds(1);
            PingTimeout = TimeSpan.FromSeconds(3);
            BufferSize = bufferSize;
            state = CREATED;
        }


        /// <summary>
        /// Connect to a socket.io specified by Uri. Ping settings indicated by the handshake will be used.
        /// </summary>
        /// <exception cref="WebSocketException">Every fault is wrapped in this exception</exception>
        /// <exception cref="OperationCanceledException">If cancellationToken is triggered</exception>
        /// <param name="uri">uri of socket.io server. If no path is specified, /socket.io/ will be used. Query could be put in Uri.Query</param>
        /// <param name="cancellationToken">Cancellation token for the task</param>
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            return ConnectAsync(uri, false, cancellationToken);
        }

        /// <summary>
        /// Connect to a socket.io specified by Uri
        /// </summary>
        /// <exception cref="WebSocketException">Every fault is wrapped in this exception</exception>
        /// <exception cref="OperationCanceledException">If cancellationToken is triggered</exception>
        /// <param name="uri">uri of socket.io server. If no path is specified, /socket.io/ will be used. Query could be put in Uri.Query</param>
        /// <param name="ignorePingSettings">If true, ignore the suggested ping settings in handshake</param>
        /// <param name="cancellationToken">Cancellation token for the task</param>
        public async Task ConnectAsync(Uri uri, bool ignorePingSettings, CancellationToken cancellationToken)
        {
            int oldState = Interlocked.CompareExchange(ref state, CONNECTING, CREATED);
            if (oldState == DISPOSED)
                throw new ObjectDisposedException(GetType().FullName);
            if (oldState != CREATED)
                throw new InvalidOperationException("Socket is already started");
            
            // You might want to add the following before calling connect
            //   if connecting to a modern TLS v1.2 server
            // ServicePointManager.Expect100Continue = true;
            // ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var ub = new UriBuilder(uri);

            // If not otherwise specified, set path to standard /socket.io/
            if (string.IsNullOrEmpty(uri.LocalPath) || uri.LocalPath == "/")
                ub.Path = "/socket.io/";

            //SessionId = "QBOi_q26z89cS9qgAAq-";
            // await ConnectHandshakeAsync(ub, ignorePingSettings, cancellationToken);

            await ConnectV4Async(ub, ignorePingSettings, cancellationToken);

            Trace.WriteLine("Session ID: " + SessionId);
            // await ConnectHandshakeFinalize(ub, cancellationToken);

            if (Interlocked.CompareExchange(ref state, CONNECTED, CONNECTING) != CONNECTING)
                throw new ObjectDisposedException(GetType().FullName);
        }

        private async Task ConnectV4Async(UriBuilder ub, bool ignorePingSettings, CancellationToken cancellationToken)
        {
            // engine.io specification: https://github.com/socketio/engine.io-protocol
            // Initiate a Engine IO v4, polling connection
            ub.Query = "EIO=4&transport=websocket" +
                (!string.IsNullOrEmpty(ub.Query) && ub.Query.StartsWith("?") ? "&" + ub.Query.Substring(1) : string.Empty);

            ub.Scheme = ub.Scheme == "https" ? "wss" : "ws";

            // Connect with standard WebSocket
            try
            {
                innerWebSocket = new ClientWebSocket();
                await innerWebSocket.ConnectAsync(ub.Uri, cancellationToken);
            }
            catch (WebSocketException ex)
            {
                throw new WebSocketException("Failed to connect the websocket", ex);
            }

            var buffer = new byte[512];
            var bufferSegment = new ArraySegment<byte>(buffer);
            // Get Handshake
            // "0{"sid":"lv_VI97HAXpY6yYWAAAC","pingInterval":25000,"pingTimeout":5000}"
            WebSocketReceiveResult r;
            try
            {
                r = await innerWebSocket.ReceiveAsync(bufferSegment, CancellationToken.None);
            }
            catch (WebSocketException ex)
            {
                throw new WebSocketException("Failed to receive handshake", ex);
            }
            do
            {
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var t = Encoding.UTF8.GetString(bufferSegment.Array, bufferSegment.Offset, r.Count);
                    if (t.StartsWith("0"))
                    {
                        JObject responseJson;
                        try
                        {
                            responseJson = JObject.Parse(t.Substring(1));
                        }
                        catch (JsonReaderException ex)
                        {
                            throw new WebSocketException(WebSocketError.HeaderError, "Handshake message should be an JSON", ex);
                        }

                        SessionId = (string)responseJson.SelectToken("sid");
                        if (string.IsNullOrWhiteSpace(SessionId))
                            throw new WebSocketException(WebSocketError.HeaderError, "Failed to extract sid in handshake");
                        if (!ignorePingSettings)
                        {
                            var tPingInterval = responseJson.SelectToken("pingInterval");
                            if (tPingInterval != null)
                                PingInterval = TimeSpan.FromMilliseconds((int)tPingInterval);
                            var tPingTimeout = responseJson.SelectToken("pingTimeout");
                            if (tPingTimeout != null)
                                PingTimeout = TimeSpan.FromMilliseconds((int)tPingTimeout);
                        }
                        break;
                    }
                }
                throw new WebSocketException(WebSocketError.Faulted, "Handshake expected but received something else");
            } while (false);

            // Starting the loop
            new Thread(ReceiverLoop).Start(cancellationToken);
            new Thread(SenderLoop).Start(cancellationToken);
        }
        
        /// <summary>
        /// Send a SocketIo EVENT
        /// </summary>
        /// <param name="payload">Json payload in the following format ["eventName", ..data..]</param>
        public void SendEventPayload(string payload, string nsp = "/")
        {
            SendRawData(Encoding.UTF8.GetBytes(
                "42" +
                (!string.IsNullOrWhiteSpace(nsp) && nsp != "/" ? nsp + "," : string.Empty) + payload
                ));
        }


        // NOTE: If needed, replace the following implementations such as with DataFlow TPL
        ConcurrentQueue<byte[]> senderQueue = new ConcurrentQueue<byte[]>();

        /// <summary>
        /// Send a WebSocket packet
        /// </summary>
        /// <param name="data">packet. Could be null to wake-up the sender loop</param>
        public void SendRawData(byte[] data)
        {
            // Currently does not check for memory overflow
            senderQueue.Enqueue(data);
            lock (senderQueue)
            {
                Monitor.Pulse(senderQueue);
            }
        }

        private void WakeSenderLoop()
        {
            SendRawData(null);
        }

        /// <summary>
        /// For SenderLoop to extract one item for sending
        /// </summary>
        /// <returns>A packet to be send</returns>
        private byte[] ExtractSenderQueue()
        {
            byte[] data;
            lock (senderQueue)
            {
                while (!senderQueue.TryDequeue(out data))
                {
                    Monitor.Wait(senderQueue);
                }
            }

            return data;
        }

        /// <summary>
        /// SenderLoop should send a ping when this is 1, and reset to 0 when it is actually sent.
        /// </summary>
        int pingRequest = 0;

        /// <summary>
        /// The Environment.TickCount when ping is sent
        /// </summary>
        int pingRequestAt = 0;
        
        int pingRequestDeadline = 0;
        int pongTimeoutDeadline = 0;

        private async void SenderLoop(object obj)
        {
            byte[] data;
            while (innerWebSocket.State == WebSocketState.Open)
            {
                data = ExtractSenderQueue();
                if (innerWebSocket.State != WebSocketState.Open)
                    break;
                try
                {
                    if (Interlocked.Exchange(ref pingRequest, 0) == 1)
                    {
                        pingRequestAt = Environment.TickCount;
                        await innerWebSocket.SendAsync(new ArraySegment<byte>(PING_STRING),
                            WebSocketMessageType.Text, true, CancellationToken.None);
                    }

                    if (data == null) continue;
                    await innerWebSocket.SendAsync(new ArraySegment<byte>(data),
                                WebSocketMessageType.Text, true, CancellationToken.None);

                    if (Interlocked.Exchange(ref pingRequest, 0) == 1)
                    {
                        pingRequestAt = Environment.TickCount;
                        await innerWebSocket.SendAsync(new ArraySegment<byte>(PING_STRING),
                            WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                catch (WebSocketException)
                {
                    // WebSocket is dead, quitting sliently. 
                    // Raise disconnect event in Receiver Loop
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // WebSocket is dead, quitting sliently. 
                    // Raise disconnect event in Receiver Loop
                    break;
                }
            }
        }
        
        private async void ReceiverLoop(object obj)
        {
            WebSocketReceiveResult r;
            var buffer = new byte[BufferSize];
            var bufferSegment = new ArraySegment<byte>(buffer);

            pingRequestDeadline = Environment.TickCount + (int)PingInterval.TotalMilliseconds;
            pongTimeoutDeadline = Environment.TickCount + (int)PingTimeout.TotalMilliseconds;
            Task<WebSocketReceiveResult> tReceive = null;
            Task tPing = null;
            while (innerWebSocket.State == WebSocketState.Open)
            {
                try
                {
                    if (tReceive == null)
                        tReceive = innerWebSocket.ReceiveAsync(bufferSegment, CancellationToken.None);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                if (tPing == null)
                    tPing = Task.Delay(Math.Max(0, Math.Min(pingRequestDeadline - Environment.TickCount, pongTimeoutDeadline - Environment.TickCount)));
                var t = await Task.WhenAny(tReceive, tPing);

                if (t == tReceive)
                {
                    try
                    {
                        r = await tReceive;
                        tReceive = null;
                    }
                    catch (WebSocketException)
                    {
                        // Disconnection?
                        break;
                    }

                    switch (r.MessageType)
                    {
                        case WebSocketMessageType.Text:
                            if (r.Count > 0)
                                // Defalut engine.io protocol
                                switch (buffer[0])
                                {
                                    case (byte)'2': // Server Ping
                                        await innerWebSocket.SendAsync(new ArraySegment<byte>(PONG_STRING),
                                            WebSocketMessageType.Text, true, CancellationToken.None);
                                        break;

                                    case (byte)'3': // Server Pong
                                        pongTimeoutDeadline = Environment.TickCount + (int)PingTimeout.TotalMilliseconds;
                                        Ping?.Invoke(this, new PingEventArgs(pingRequestAt, Environment.TickCount));
                                        break;

                                    case (byte)'4': // Message
                                        if (r.Count > 1)
                                            // Defalut socket.io protocol
                                            switch (buffer[1])
                                            {
                                                case (byte)'0': // Connect
                                                case (byte)'1': // Disconnect
                                                    // Ignored
                                                    break;
                                                case (byte)'2': // Event
                                                    try
                                                    {
                                                        var text = Encoding.UTF8.GetString(bufferSegment.Array, bufferSegment.Offset + 2, r.Count - 2);
                                                        IncomingEvent?.Invoke(this, new IncomingEventEventArgs(text));
                                                    }
                                                    catch (ArgumentException) { }
                                                    break;
                                                case (byte)'3': // Ack
                                                case (byte)'4': // Error
                                                case (byte)'5': // Binary_Event
                                                case (byte)'6': // Binary_Ack
                                                    // Ignored
                                                    break;
                                            }
                                        break;
                                }
                            break;
                        case WebSocketMessageType.Binary:
                        case WebSocketMessageType.Close:
                        default:
                            // Nothing to handle
                            break;
                    }
                }
                else
                {
                    if (Environment.TickCount - pingRequestDeadline >= 0)
                    {
                        if (Interlocked.CompareExchange(ref pingRequest, 1, 0) == 0)
                        {
                            pingRequest = 1;
                            WakeSenderLoop();
                            pingRequestDeadline = Environment.TickCount + (int)PingInterval.TotalMilliseconds;
                            pongTimeoutDeadline = Environment.TickCount + (int)PingTimeout.TotalMilliseconds;
                        }
                        tPing = null;
                    }
                    if (Environment.TickCount - pongTimeoutDeadline >= 0)
                    {
                        // Ping timeout
                        try
                        {
                            await innerWebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Ping timeout", CancellationToken.None);
                        }
                        catch (WebSocketException) { }
                        catch (ObjectDisposedException) { }
                        break;
                    }
                }
            }

            Disconnect?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            int oldState = Interlocked.Exchange(ref state, DISPOSED);
            if (oldState == DISPOSED)
            {
                // No cleanup required.
                return;
            }
            if (innerWebSocket != null)
            {
                innerWebSocket.Dispose();
                WakeSenderLoop();
            }
        }
    }
}
