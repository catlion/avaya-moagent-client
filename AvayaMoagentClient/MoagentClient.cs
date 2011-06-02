﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenSSL;
using OpenSSL.Core;
using OpenSSL.X509;

namespace AvayaMoagentClient
{
  // State object for receiving data from remote device.
  public class StateObject
  {
    public Socket Stream { get; set; }
    public SslStream SecureStream { get; set; }
    public const int BufferSize = 256;
    public byte[] buffer = new byte[BufferSize];
    public StringBuilder sb = new StringBuilder();
  }

  public class MoagentClient
  {
    private int _invokeIdSequence = 1;
    private Socket _client;
    private SslStream _sslWrapper;
    private string _server;
    private int _port;
    private readonly bool _useSSL;
    private X509List _xList;
    private X509Chain _xChain;

    public delegate void MessageSentHandler(object sender, MessageSentEventArgs e);
    public delegate void MessageReceivedHandler(object sender, MessageReceivedEventArgs e);
    public delegate void DisconnectedHandler(object sender, EventArgs e);

    public event EventHandler ConnectComplete;
    public event MessageSentHandler MessageSent;
    public event MessageReceivedHandler MessageReceived;
    public event DisconnectedHandler Disconnected;

    public MoagentClient(string host, int port): this(host, port, false)
    {
    }
    
    public MoagentClient(string host, int port, bool useSSL)
    {
      _server = host;
      _port = port;
      _useSSL = useSSL;
      _client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

      if (_useSSL)
      {
        var certBio = BIO.File(@".\agentClientCert.p12", "r");
        X509Certificate clientCert = X509Certificate.FromPKCS12(certBio, string.Empty);
        var serverBio = BIO.File(@".\ProactiveContactCA.cer", "r");
        X509Certificate serverCert = X509Certificate.FromDER(serverBio);

        _xList = new X509List();
        _xList.Add(clientCert);
        _xChain = new X509Chain();
        _xChain.Add(serverCert);
      }
    }

    public bool Connected
    {
      get { return (_sslWrapper != null && _client != null && _client.Connected); }
    }

    public void StartConnectAsync()
    {
      var ip = IPAddress.Parse(_server);
      var remoteEp = new IPEndPoint(ip, _port);
      _client.BeginConnect(remoteEp, ConnectCallback, _client);
    }

    private void ConnectCallback(IAsyncResult ar)
    {
      var client = (Socket)ar.AsyncState;

      client.EndConnect(ar);

      if (_useSSL)
      {
        var stream = new NetworkStream(_client, FileAccess.ReadWrite, true);
        _sslWrapper = new SslStream(stream, false, ValidateRemoteCert, clientCertificateSelectionCallback);

        _sslWrapper.AuthenticateAsClient(_server, _xList, _xChain, SslProtocols.Default, SslStrength.All, false);

        SecureReceive(_sslWrapper);
      }
      else
      {
        Receive(client);
      }

      if (ConnectComplete != null)
        ConnectComplete(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
      if (_sslWrapper != null)
      {
        _sslWrapper.Close();
        _sslWrapper.Dispose();
        _sslWrapper = null;
      }

      if (_client.Connected)
        _client.Close();

      if (Disconnected != null)
        Disconnected(this, EventArgs.Empty);
    }

    private void Receive(Socket client)
    {
      try
      {
        var state = new StateObject();
        state.Stream = client;
        
        client.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0, new AsyncCallback(ReceiveCallback), state);
      }
      catch (Exception e)
      {
        Console.WriteLine(e.ToString());
      }
    }

    private void SecureReceive(SslStream client)
    {
      try
      {
        var state = new StateObject();
        state.SecureStream = client;

        client.BeginRead(state.buffer, 0, StateObject.BufferSize, new AsyncCallback(SecureReceiveCallback), state);
      }
      catch (Exception e)
      {
        Console.WriteLine(e.ToString());
      }
    }

    private void ReceiveCallback(IAsyncResult ar)
    {
      var content = String.Empty;

      try
      {
        // Retrieve the state object and the client socket 
        // from the asynchronous state object.
        var state = (StateObject)ar.AsyncState;
        var handler = state.Stream;
        Message lastMsg = null;

        if (handler.Connected)
        {
          // Read data from the remote device.
          int bytesRead = handler.EndReceive(ar);

          if (bytesRead > 0)
          {
            // There  might be more data, so store the data received so far.
            state.sb.Append(Encoding.ASCII.GetString(state.buffer, 0, bytesRead));

            // Check for end-of-file tag. If it is not there, read more data.
            content = state.sb.ToString();
            if (content.IndexOf((char)3) > -1)
            {
              var msgs = new List<string>();
              var msg = new StringBuilder();

              foreach (var ch in content)
              {
                if (ch != (char)3)
                  msg.Append(ch);
                else
                {
                  msg.Append(ch);
                  msgs.Add(msg.ToString());
                  msg.Length = 0;
                }
              }

              state.sb.Length = 0;
              state.sb.Append(msg.ToString());

              lastMsg = _LogMessagesReceived(msgs);
            }
          }

          if (!(lastMsg != null &&
                lastMsg.Type == Message.MessageType.Response &&
                lastMsg.Command.Trim() == "AGTLogoff"))
            handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0, new AsyncCallback(ReceiveCallback), state);
        }
      }
      catch (IOException e)
      {
        //something in the transport leyer has failed, such as the network connection died
        //TODO: log the exception details?
        Disconnect();
      }
      catch (ObjectDisposedException e)
      {
        //we've been disconnected
        //TODO: log the exception details?
        Disconnect();
      }
      catch (Exception e)
      {
        Debugger.Break();
      }
    }

    private void SecureReceiveCallback(IAsyncResult ar)
    {
      var content = String.Empty;

      try
      {
        // Retrieve the state object and the client socket 
        // from the asynchronous state object.
        var state = (StateObject)ar.AsyncState;
        var handler = state.SecureStream;
        Message lastMsg = null;

        if (handler.CanRead)
        {
          // Read data from the remote device.
          int bytesRead = handler.EndRead(ar);
          //int bytesRead = handler.EndReceive(ar);

          if (bytesRead > 0)
          {
            // There  might be more data, so store the data received so far.
            state.sb.Append(Encoding.ASCII.GetString(state.buffer, 0, bytesRead));

            // Check for end-of-file tag. If it is not there, read more data.
            content = state.sb.ToString();
            if (content.IndexOf((char)3) > -1)
            {
              var msgs = new List<string>();
              var msg = new StringBuilder();

              foreach (var ch in content)
              {
                if (ch != (char)3)
                  msg.Append(ch);
                else
                {
                  msg.Append(ch);
                  msgs.Add(msg.ToString());
                  msg.Length = 0;
                }
              }

              state.sb.Length = 0;
              state.sb.Append(msg.ToString());

              lastMsg = _LogMessagesReceived(msgs);
            }
          }

          if (!(lastMsg != null && 
                lastMsg.Type == Message.MessageType.Response && 
                lastMsg.Command.Trim() == "AGTLogoff"))
            handler.BeginRead(state.buffer, 0, StateObject.BufferSize, new AsyncCallback(SecureReceiveCallback), state);
        }
      }
      catch (IOException e)
      {
        //something in the transport leyer has failed, such as the network connection died
        //TODO: log the exception details?
        Disconnect();
      }
      catch (ObjectDisposedException e)
      {
        //we've been disconnected
        //TODO: log the exception details?
        Disconnect();
      }
      catch (Exception e)
      {
        Debugger.Break();
      }
    }

    private Message _LogMessagesReceived(List<string> msgs)
    {
      Message lastMsg = null;

      foreach (var msg in msgs)
      {
        lastMsg = Message.ParseMessage(msg);
        if (MessageReceived != null)
          MessageReceived(this, new MessageReceivedEventArgs() { Message = lastMsg });
      }

      return lastMsg;
    }

    public void Send(Message data)
    {
      data.InvokeId = (_invokeIdSequence++).ToString();
      if (MessageSent != null)
        MessageSent(this, new MessageSentEventArgs() { Message = data });

      Send(data.RawMessage);
    }

    private void Send(string data)
    {
      byte[] byteData = Encoding.ASCII.GetBytes(data);

      // Begin sending the data to the remote device.
      //TODO: Pick secure vs nonsecure send
      if (_useSSL)
      {
        _sslWrapper.BeginWrite(byteData, 0, byteData.Length, new AsyncCallback(SecureSendCallback), _sslWrapper);  
      }
      else
      {
        _client.BeginSend(byteData, 0, byteData.Length, 0, new AsyncCallback(SendCallback), _client);
      }
    }

    private void SendCallback(IAsyncResult ar)
    {
      try
      {
        // Retrieve the socket from the state object.
        var client = (Socket)ar.AsyncState;
        client.EndSend(ar);
        //TODO: Tell somebody?
      }
      catch (IOException e)
      {
        //something in the transport leyer has failed, such as the network connection died
        //TODO: log the exception details?
        Disconnect();
      }
      catch (ObjectDisposedException e)
      {
        //we've been disconnected
        //TODO: log the exception details?
        Disconnect();
      }
      catch (Exception e)
      {
        Console.WriteLine(e.ToString());
      }
    }

    private void SecureSendCallback(IAsyncResult ar)
    {
      try
      {
        // Retrieve the socket from the state object.
        var client = (SslStream)ar.AsyncState;
        client.EndWrite(ar);
        //TODO: Tell somebody?
      }
      catch (IOException e)
      {
        //something in the transport leyer has failed, such as the network connection died
        //TODO: log the exception details?
        Disconnect();
      }
      catch (ObjectDisposedException e)
      {
        //we've been disconnected
        //TODO: log the exception details?
        Disconnect();
      }
      catch (Exception e)
      {
        Console.WriteLine(e.ToString());
      }
    }

    bool ValidateRemoteCert(object obj, X509Certificate cert, X509Chain chain, int depth, VerifyResult result)
    {
      bool ret = false;

      switch (result)
      {
        case VerifyResult.X509_V_ERR_CERT_UNTRUSTED:
        case VerifyResult.X509_V_ERR_UNABLE_TO_GET_ISSUER_CERT:
        case VerifyResult.X509_V_ERR_UNABLE_TO_GET_ISSUER_CERT_LOCALLY:
        case VerifyResult.X509_V_ERR_UNABLE_TO_VERIFY_LEAF_SIGNATURE:
          {
            // Check the chain to see if there is a match for the cert
            ret = CheckCert(cert, chain);
            if (!ret && depth != 0)
            {
              ret = true;  // We want to keep checking until we get to depth 0
            }
          }
          break;
        case VerifyResult.X509_V_ERR_ERROR_IN_CERT_NOT_BEFORE_FIELD:
        case VerifyResult.X509_V_ERR_CERT_NOT_YET_VALID:
          {
            Console.WriteLine("Certificate is not valid yet");
            ret = false;
          }
          break;
        case VerifyResult.X509_V_ERR_CERT_HAS_EXPIRED:
        case VerifyResult.X509_V_ERR_ERROR_IN_CERT_NOT_AFTER_FIELD:
          {
            Console.WriteLine("Certificate is expired");
            ret = false;
          }
          break;
        case VerifyResult.X509_V_ERR_DEPTH_ZERO_SELF_SIGNED_CERT:
          {
            // we received a self signed cert - check to see if it's in our store
            ret = CheckCert(cert, chain);
          }
          break;
        case VerifyResult.X509_V_ERR_SELF_SIGNED_CERT_IN_CHAIN:
          {
            // A self signed certificate was encountered in the chain
            // Check the chain to see if there is a match for the cert
            ret = CheckCert(cert, chain);
            if (!ret && depth != 0)
            {
              ret = true;  // We want to keep checking until we get to depth 0
            }
          }
          break;
        case VerifyResult.X509_V_OK:
          {
            ret = true;
          }
          break;
      }
      return ret;
    }

    protected X509Certificate clientCertificateSelectionCallback(object sender, string targetHost, X509List localCerts, X509Certificate remoteCert, string[] acceptableIssuers)
    {
      X509Certificate retCert = null;

      // check target host?

      for (int i = 0; i < acceptableIssuers.GetLength(0); i++)
      {
        X509Name name = new X509Name(acceptableIssuers[i]);

        foreach (X509Certificate cert in localCerts)
        {
          if (cert.Issuer.CompareTo(name) == 0)
          {
            retCert = cert;
            break;
          }
          cert.Dispose();
        }
        name.Dispose();
      }
      return retCert;
    }

    bool CheckCert(X509Certificate cert, X509Chain chain)
    {
      if (cert == null || chain == null)
      {
        return false;
      }

      foreach (X509Certificate certificate in chain)
      {
        if (cert == certificate)
        {
          return true;
        }
      }

      return false;
    }
  }
}
