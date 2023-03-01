﻿using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ConcurrentLinkedList;
using PgWireAdo.utils;
using PgWireAdo.wire;
using PgWireAdo.wire.client;
using PgWireAdo.wire.server;

namespace PgWireAdo.ado;

public class PgwConnection : DbConnection
{
    private PgwConnectionString _options;
    private string _connectionString;
    private ConnectionState _state = ConnectionState.Closed;
    private Socket _client;
    private PgwByteBuffer _byteBuffer;

    public PgwByteBuffer Stream { get { return _byteBuffer; } }

    public PgwConnection(string connectionString)
    {
        _connectionString = connectionString;
    }

    public PgwConnection()
    {
        
    }

    private ConcurrentLinkedList<DataMessage> inputQueue = new();
    private Thread _queueThread;

    public ConcurrentLinkedList<DataMessage> InputQueue { get { return inputQueue; } }



    public override void Open()
    {
        
        _tcpClient = new TcpClient(_options.DataSource, _options.Port);
        _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        _byteBuffer = new PgwByteBuffer(_tcpClient, this);
        

        _queueThread = new Thread(() =>
        {
            
            ReadDataFromStream(_byteBuffer);
        });
        _queueThread.Start();
        _byteBuffer.Write(new SSLNegotation());
        var response = _byteBuffer.WaitFor<SSLResponse>();
        
         var parameters = new Dictionary<String, String>();
         parameters.Add("database", Database);
         var startup = new StartupMessage(parameters);
         _byteBuffer.Write(startup);
         _state = ConnectionState.Open;
    }


    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        Open();
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        _running = false;
        if (_state != ConnectionState.Closed)
        {
            _state = ConnectionState.Closed;
            if(_client!=null) { 
                _client.Dispose();
            }

            if (_tcpClient != null)
            {
                _tcpClient.Dispose();
            }
            
        }

        base.Dispose(disposing);
    }

    public override string ConnectionString {
        get { return _connectionString; }
        set
        {
            if (value == null)
            {
                value = string.Empty;
            }

            _options = new PgwConnectionString(value);
            _connectionString = value;
        }
    }
    public override string Database => _options.Database;
    public override ConnectionState State => _state;
    public override string DataSource => _options.DataSource;
    public override string ServerVersion => _options.ServerVersion;
    


    public override void Close()
    {
        _state = ConnectionState.Closed;
        
        _byteBuffer.Write(new TerminateMessage());
        _state = ConnectionState.Closed;
        if (_client != null)
        {
            _client.Dispose();
            _client = null;
        }

        if (_tcpClient != null)
        {
            _tcpClient.Dispose();
            _tcpClient = null;
        }

    }


    protected override DbCommand CreateDbCommand()
    {
        return new PgwCommand(this);
    }

    public override ValueTask DisposeAsync()
    {
        return new ValueTask(Task.Run(Dispose));
    }

    #region TOIMPLEMENT

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        var result= new PgwTransaction(this, isolationLevel);
        _byteBuffer.Write(new QueryMessage("JANUS:BEGIN_TRANSACTION"));
        _byteBuffer.WaitFor<CommandComplete>();
        _byteBuffer.WaitFor<ReadyForQuery>();
        
        return result;
    }

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotImplementedException();
    }

    #endregion

    private bool _running = true;
    private TcpClient _tcpClient;

    public bool Running { get { return _running; } }
    private void ReadDataFromStream(PgwByteBuffer buffer)
    {
        try
        {
            var sslNegotiationDone = false;
            var header = new byte[5];
            while (_running)
            {
                var messageType = (char)buffer.ReadByte();
                if (messageType == 'N' && sslNegotiationDone == false)
                {
                    sslNegotiationDone = true;
                    var sslN = new DataMessage((char)messageType, 0, new byte[0]);
                    inputQueue.TryAdd(sslN);
                    continue;
                }

                var messageLength = buffer.ReadInt32();
                var data = new byte[0];
                if (messageLength > 4)
                {
                    data = buffer.Read(messageLength - 4);
                }

                var dm = new DataMessage((char)messageType, messageLength, data);
                inputQueue.TryAdd(dm);
            }
        }
        catch (Exception ex)
        {

        }
    }
}

