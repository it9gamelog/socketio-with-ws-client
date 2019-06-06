Name
===========

socketio-with-ws-client - A simplified, bare minimum C# socket.io client implementation

Synopsis
========

```csharp
var c = new SocketIoWithWsClient();
c.IncomingEvent += (sender, e) => {
  Console.WriteLine(e.payload);
};
c.Ping += (sender, e) => {
  Console.WriteLine("Ping");
};
c.Disconnect += (sender, e) => {
  Console.WriteLine("Disconnected");
};

// For making TLS v1.2 connection which is used by HTTPS
ServicePointManager.Expect100Continue = true;
ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

await c.ConnectAsync(new Uri("https://example.com/?custom=query_string"));
c.SendEventPayload("[\"Sample EventName\",\"\\\"The quick brown fox jumps over the lazy dog\\\"\"]");
```

Usage
=====

* Requires `Newtonsoft.Json`. Tested on .NET v4.5.2.
* Copy the file and use it as is. No NuGet package. Modify if needed.
* Only support upgrade to websocket protocol.

Limitation
==========

* Only socket.io `Event` is supported. `Ack`, `Error`, `Binary_Event`, `Binary_Ack` are ignored. <br>
  Implement them in `ReceiverLoop()` if needed.

Background Story
================

See https://medium.com/@it9gamelog/socket-io-a-c-client-library-be9bdc1eb6ab

