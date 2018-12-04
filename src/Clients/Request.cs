﻿using BeetleX.Buffers;
using BeetleX.Clients;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace BeetleX.FastHttpApi.Clients
{
    public class Request
    {
        public const string POST = "POST";

        public const string GET = "GET";

        public const string DELETE = "DELETE";

        public const string PUT = "PUT";

        public Request()
        {
            Method = GET;
            this.HttpProtocol = "HTTP/1.1";
        }

        public IClientBodyFormater Formater { get; set; }

        public Dictionary<string, string> QuestryString { get; set; }

        public Header Header { get; set; }

        public string Url { get; set; }

        public string Method { get; set; }

        public string HttpProtocol { get; set; }

        public Response Response { get; set; }

        public Object Body { get; set; }

        public Type BodyType { get; set; }

        public RequestStatus Status { get; set; }

        public IClient Client { get; set; }

        internal void Execute(PipeStream stream)
        {
            var buffer = HttpParse.GetByteBuffer();
            int offset = 0;
            offset += Encoding.ASCII.GetBytes(Method, 0, Method.Length, buffer, offset);
            buffer[offset] = HeaderTypeFactory._SPACE_BYTE;
            offset++;
            offset += Encoding.ASCII.GetBytes(Url, 0, Url.Length, buffer, offset);
            if (QuestryString != null && QuestryString.Count > 0)
            {
                int i = 0;
                foreach (var item in this.QuestryString)
                {
                    string key = item.Key;
                    string value = item.Value;
                    if (string.IsNullOrEmpty(value))
                        continue;
                    value = System.Net.WebUtility.UrlEncode(value);
                    if (i == 0)
                    {
                        buffer[offset] = HeaderTypeFactory._QMARK;
                        offset++;
                    }
                    else
                    {
                        buffer[offset] = HeaderTypeFactory._AND;
                        offset++;
                    }
                    offset += Encoding.ASCII.GetBytes(key, 0, key.Length, buffer, offset);
                    buffer[offset] = HeaderTypeFactory._EQ;
                    offset++;
                    offset += Encoding.ASCII.GetBytes(value, 0, value.Length, buffer, offset);
                    i++;
                }
            }
            buffer[offset] = HeaderTypeFactory._SPACE_BYTE;
            offset++;
            offset += Encoding.ASCII.GetBytes(HttpProtocol, 0, HttpProtocol.Length, buffer, offset);
            buffer[offset] = HeaderTypeFactory._LINE_R;
            offset++;
            buffer[offset] = HeaderTypeFactory._LINE_N;
            offset++;
            stream.Write(buffer, 0, offset);
            if (Header != null)
                Header.Write(stream);
            if (Method == POST || Method == PUT)
            {
                if (Body != null)
                {
                    stream.Write(HeaderTypeFactory.CONTENT_LENGTH_BYTES, 0, 16);
                    MemoryBlockCollection contentLength = stream.Allocate(10);
                    stream.Write(HeaderTypeFactory.TOW_LINE_BYTES, 0, 4);
                    int len = stream.CacheLength;
                    Formater.Serialization(Body, stream);
                    int count = stream.CacheLength - len;
                    contentLength.Full(count.ToString().PadRight(10), stream.Encoding);
                }
                else
                {
                    stream.Write(HeaderTypeFactory.NULL_CONTENT_LENGTH_BYTES, 0, HeaderTypeFactory.NULL_CONTENT_LENGTH_BYTES.Length);
                    stream.Write(HeaderTypeFactory.LINE_BYTES, 0, 2);
                }
            }
            else
            {
                stream.Write(HeaderTypeFactory.LINE_BYTES, 0, 2);
            }
        }

        public HttpHost HttpHost { get; set; }

        public async Task<Response> Execute()
        {
            IClient client = HttpHost.Pool.Pop();
            try
            {
                Client = client;
                if (client.Stream != null)
                {
                    if (client.Stream.ToPipeStream().Length > 0)
                    {
                        client.Stream.ToPipeStream().ReadFree((int)client.Stream.ToPipeStream().Length);
                    }
                }
                if (client is AsyncTcpClient)
                {
                    AsyncTcpClient asyncClient = (AsyncTcpClient)client;
                    var a = asyncClient.ReceiveMessage<Response>();
                    //asyncClient.Connect();
                    //if (asyncClient.Stream.ToPipeStream().CacheLength > 0)
                    //    throw new Exception("client request method has cache data");
                    if (!a.IsCompleted)
                    {
                        asyncClient.Send(this);
                        Status = RequestStatus.SendCompleted;
                        asyncClient.BeginReceive();
                    }
                    Response = await a;
                    if (Response.Exception != null)
                        Status = RequestStatus.Error;
                    else
                        Status = RequestStatus.Received;
                }
                else
                {
                    TcpClient syncClient = (TcpClient)client;
                    syncClient.SendMessage(this);
                    Status = RequestStatus.SendCompleted;
                    Response = syncClient.ReceiveMessage<Response>();
                    if (Response.Exception != null)
                        Status = RequestStatus.Error;
                    else
                        Status = RequestStatus.Received;
                }
                int code = int.Parse(Response.Code);
                if (Response.Length > 0)
                {
                    try
                    {
                        if (code < 400)
                            Response.Body = this.Formater.Deserialization(Response.Stream, this.BodyType, Response.Length);
                        else
                            Response.Body = Response.Stream.ReadString(Response.Length);
                    }
                    finally
                    {
                        Response.Stream.ReadFree(Response.Length);
                        if (Response.Chunked)
                            Response.Stream.Dispose();
                        Response.Stream = null;
                    }
                }
                if (!Response.KeepAlive)
                    client.DisConnect();
                if (code >= 400)
                    throw new System.Net.WebException($"{this.Method} {this.Url} {Response.Code} {Response.CodeMsg} {Response.Body}");
                HttpHost.Available = true;
                Status = RequestStatus.Completed;
            }
            catch (Exception e_)
            {

                HttpClientException clientException = new HttpClientException(this, HttpHost.Uri, e_.Message, e_);
                if (e_ is System.Net.Sockets.SocketException || e_ is ObjectDisposedException)
                {
                    clientException.SocketError = true;
                    HttpHost.Available = false;
                }
                else
                {
                    HttpHost.Available = true;
                }
                Response = new Response { Exception = clientException };
                Status = RequestStatus.Error;
            }
            finally
            {
                HttpHost.Pool.Push(client);
            }
            if (Response.Exception != null)
                HttpHost.AddError();
            else
                HttpHost.AddSuccess();
            return Response;
        }
    }

    public enum RequestStatus
    {
        None,
        SendCompleted,
        Received,
        Completed,
        Error
    }
}