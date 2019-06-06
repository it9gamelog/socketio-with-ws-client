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

            await ConnectHandshakeAsync(ub, ignorePingSettings, cancellationToken);
            await ConnectHandshakeFinalize(ub, cancellationToken);

            if (Interlocked.CompareExchange(ref state, CONNECTED, CONNECTING) != CONNECTING)
                throw new ObjectDisposedException(GetType().FullName);
        }

        private async Task ConnectHandshakeAsync(UriBuilder ub, bool ignorePingSettings, CancellationToken cancellationToken)
        {
            // Initiate a Engine IO v3, polling connection
            ub.Query = "EIO=3&transport=polling&t=" + RandomString(10) + 
                (!string.IsNullOrEmpty(ub.Query) && ub.Query.StartsWith("?") ? "&" + ub.Query.Substring(1) : string.Empty);
            
            byte[] buf;
            int mark, len = 0;
            using (var client = new HttpClient())
            {
                var handshake = await client.GetAsync(ub.Uri, cancellationToken);
                if (!handshake.IsSuccessStatusCode)
                    throw new WebSocketException(WebSocketError.HeaderError, "Handshake response gives failure");
                buf = await handshake.Content.ReadAsByteArrayAsync();
            }

            // Response is formatted according to https://github.com/socketio/engine.io-protocol, XHR2 section
            // <0 for string data, 1 for binary data><Any number of numbers between 0 and 9><The number 255><packet1 (first type, then data)>[...]
            // Example: \x00 \x04 \x00 \x02 \xff 0 "Json Data"
            //   where the first \x00 indicates string data
            //   \x04 \x00 \x02 means the following packet is 402 bytes long
            //   \xff marks the beginning of the packet
            //   Packet type 0 means "Open" command 
            if (buf.Length < 2 || buf[0] != 0x00)
                throw new WebSocketException(WebSocketError.HeaderError, "Handshake response is not a standard engine.io string data");

            mark = Array.IndexOf(buf, (byte)0xff, 0, buf.Length);
            if (mark < 0)
                throw new WebSocketException(WebSocketError.HeaderError, "Handshake response is malformed");
            var lengthSegment = new ArraySegment<byte>(buf, 1, mark - 1);
            foreach (var i in lengthSegment)
            {
                len = len * 10 + i;
            }

            if (mark + len + 1 > buf.Length || len < 1)
                throw new WebSocketException(WebSocketError.HeaderError, "Handshake length should not be longer than the data received");
            if (len < 1)
                throw new WebSocketException(WebSocketError.HeaderError, "Handshake length is too short");
            if (buf[mark + 1] != '0')
                throw new WebSocketException(WebSocketError.HeaderError, "Handshake should be an OPEN message");

            string responseText;
            try
            {
                responseText = Encoding.UTF8.GetString(buf, mark + 2, len - 1);
            }
            catch (ArgumentException ex)
            {
                throw new WebSocketException(WebSocketError.HeaderError, "Handshake message should be UTF-8 valid", ex);
            }
            JObject responseJson;
            try
            {
                responseJson = JObject.Parse(responseText);
            }
            catch (JsonReaderException ex)
            {
                throw new WebSocketException(WebSocketError.HeaderError, "Handshake message should be an JSON", ex);
            }

            try
            {
                /* open message: https://github.com/socketio/engine.io-protocol
                    { "sid": "RANDOM", "upgrades": ["websocket"], "pingInterval": 1000, "pingTimeout": 2000 }                    
                */
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
                var tUpgrades = responseJson["upgrades"];
                if (!(tUpgrades is JArray))
                    throw new WebSocketException(WebSocketError.HeaderError, "Failed to extract upgrades in handshake");
                bool found = false;
                foreach (var e in tUpgrades)
                {
                    if ((string)e == "websocket")
                        found = true;
                }
                if (!found)
                    throw new WebSocketException(WebSocketError.UnsupportedVersion,
                        "Handshake said WebSocket protocol is not supported, yet we don't support other protocol");
            }
            catch (FormatException ex)
            {
                throw new WebSocketException(WebSocketError.HeaderError, "Failed to parse handshake message", ex);
            }
        }

        private async Task ConnectHandshakeFinalize(UriBuilder ub, CancellationToken cancellationToken)
        {
            // The following exchanges follows the Encoding/example in
            // https://github.com/socketio/engine.io-protocol/blob/master/README.md

            ub.Query = "EIO=3&transport=websocket&sid=" + SessionId;
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
            
            // Send "2probe"
            var buffer = new byte[512];
            var bufferSegment = new ArraySegment<byte>(buffer);
            try
            {
                // engine.io<Ping(2)>
                await innerWebSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("2probe")),
                    WebSocketMessageType.Text, true, cancellationToken);
            }
            catch (WebSocketException ex)
            {
                throw new WebSocketException("Failed to send probe", ex);
            }
            
            // Get "3probe"
            WebSocketReceiveResult r;
            try
            {
                r = await innerWebSocket.ReceiveAsync(bufferSegment, CancellationToken.None);
            }
            catch (WebSocketException ex)
            {
                throw new WebSocketException("Failed to receive probe or send upgrade command", ex);
            }
            do
            {
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var t = Encoding.UTF8.GetString(bufferSegment.Array, bufferSegment.Offset, r.Count);
                    if (t.StartsWith("3")) // Server should send "3probe", yet anything starts with "3" is ok
                    {
                        // Got engine.io<Pong(3)>
                        // Sending engine.io<Upgrade(5)>
                        await innerWebSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("5")),
                            WebSocketMessageType.Text, true, cancellationToken);
                        break;
                    }
                }
                throw new WebSocketException(WebSocketError.Faulted, "Probe expected but received something else");
            } while (false);

            // Fetch server responses to 5
            try
            {
                r = await innerWebSocket.ReceiveAsync(bufferSegment, CancellationToken.None);
            }
            catch (WebSocketException ex)
            {
                throw new WebSocketException("Failed to receive Message+Connect response", ex);
            }
            do
            {
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var t = Encoding.UTF8.GetString(bufferSegment.Array, bufferSegment.Offset, r.Count);
                    if (t.StartsWith("40")) // Server should send "40" yet anything starts with it is ok. 
                    {
                        // engine.io<Message(4)> + socket.io<Connect(0)>
                        // OK!
                        break;
                    }
                    else if (t.StartsWith("44") && t.Length >= 2)
                    {
                        // engine.io<Message(4)> + socket.io<Error(4)>
                        //
                        // If there is any application level error, 
                        // such as unauthorized access, invalid parameters
                        // socket.io will usually report the error only at this stage
                        throw new WebSocketException(WebSocketError.Faulted, "Fail to connect: " + t.Substring(2));

                    }
                    else if (t.StartsWith("41"))
                    {
                        // engine.io<Message(4)> + socket.io<Disconnect(1)>
                        throw new WebSocketException(WebSocketError.Faulted, "Server refuse to connect: " + t.Substring(2));

                    }
                    else if (t.StartsWith("4"))
                    {
                        // engine.io<Message(4)> + anything else
                        throw new WebSocketException(WebSocketError.Faulted, "Fail to connect: " + t);
                    }
                }
                throw new WebSocketException(WebSocketError.Faulted, "Message(4)+Connect(0) expected but received something else");
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
                        await innerWebSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("2")),
                            WebSocketMessageType.Text, true, CancellationToken.None);
                    }

                    if (data == null) continue;
                    await innerWebSocket.SendAsync(new ArraySegment<byte>(data),
                                WebSocketMessageType.Text, true, CancellationToken.None);

                    if (Interlocked.Exchange(ref pingRequest, 0) == 1)
                    {
                        pingRequestAt = Environment.TickCount;
                        await innerWebSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("2")),
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
