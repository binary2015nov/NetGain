﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace StackExchange.NetGain
{
    public class TcpServer : TcpHandler
    {
        private IMessageProcessor messageProcessor;
        public IMessageProcessor MessageProcessor { get { return messageProcessor; } set { messageProcessor = value; } }

        private Socket[] connectSockets;
        public TcpServer(int concurrentOperations = 0) : base(concurrentOperations)
        {
            
        }
        
        public override string ToString()
        {
            return "server";
        }

        protected override void Close()
        {
            Stop();
            base.Close();
        }
        public void Stop()
        {
            if (timer != null)
            {
                timer.Dispose();
                timer = null;
            }
            foreach(var conn in allConnections)
            {
                var socket = conn.Socket;
                if(socket != null)
                {
                    try { socket.Close(); }
                    catch (Exception ex) { Console.Error.WriteLine("{0}\t{1}", Connection.GetIdent(conn), ex.Message); }
                    try { ((IDisposable)socket).Dispose(); }
                    catch (Exception ex) { Console.Error.WriteLine("{0}\t{1}", Connection.GetIdent(conn), ex.Message); }
                }
            }
            if (connectSockets != null)
            {
                foreach (var connectSocket in connectSockets)
                {
                    if (connectSocket == null) continue;
                    EndPoint endpoint = null;
                    
                    try
                    {
                        endpoint = connectSocket.LocalEndPoint;
                        Console.WriteLine("{0}\tService stopping: {1}", Connection.GetConnectIdent(endpoint), endpoint);
                        connectSocket.Close();
                    }
                    catch (Exception ex) { Console.Error.WriteLine("{0}\t{1}", Connection.GetConnectIdent(endpoint), ex.Message); }
                    try { ((IDisposable)connectSocket).Dispose(); }
                    catch (Exception ex) { Console.Error.WriteLine("{0}\t{1}", Connection.GetConnectIdent(endpoint), ex.Message); }
                }
                connectSockets = null;
                var tmp = messageProcessor;
                if(tmp != null) tmp.EndProcessor(Context);
            }
            WriteLog();
        }
        ConnectionSet allConnections = new ConnectionSet();
        
        private System.Threading.Timer timer;
        public int Backlog { get; set; }
        
        private const int LogFrequency = 10000;

        public void Start(string configuration, params IPEndPoint[] endpoints)
        {
            if (endpoints == null || endpoints.Length == 0) throw new ArgumentNullException("endpoints");

            if (connectSockets != null) throw new InvalidOperationException("Already started");
            connectSockets = new Socket[endpoints.Length];
            var tmp = messageProcessor;
            if(tmp != null) tmp.StartProcessor(Context, configuration);
            for (int i = 0; i < endpoints.Length; i++)
            {
                Console.WriteLine("{0}\tService starting: {1}", Connection.GetConnectIdent(endpoints[i]), endpoints[i]);
                var connectSocket = new Socket(endpoints[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                connectSocket.Bind(endpoints[i]);
                connectSocket.Listen(Backlog);
                var args = Context.GetSocketArgs();
                args.UserToken = connectSocket; // the state on each connect attempt is the originating socket
                StartAccept(args);
                connectSockets[i] = connectSocket;
            }
            
            timer = new System.Threading.Timer(Heartbeat, null, LogFrequency, LogFrequency);
        }

        public override void OnAuthenticate(Connection connection, StringDictionary claims)
        {
            var tmp = messageProcessor;
            if(tmp != null) tmp.Authenticate(Context, connection, claims);
            base.OnAuthenticate(connection, claims);
        }
        public override void OnAfterAuthenticate(Connection connection)
        {
            var tmp = messageProcessor;
            if (tmp != null) tmp.AfterAuthenticate(Context, connection);
            base.OnAfterAuthenticate(connection);
        }
        protected override void OnAccepted(Connection connection)
        {
            var tmp = messageProcessor;
            if (tmp != null) tmp.OpenConnection(Context, connection);
            allConnections.Add(connection);
            
            StartReading(connection);
        }

        protected override void OnFlushed(Connection connection)
        {
            var tmp = messageProcessor;
            if (tmp != null) tmp.Flushed(Context, connection);
            base.OnFlushed(connection);
        }
        protected override int GetCurrentConnectionCount()
        {
            return allConnections.Count;
        }
        protected internal override void OnClosing(Connection connection)
        {
            if (allConnections.Remove(connection))
            {
                var tmp = messageProcessor;
                if (tmp != null) tmp.CloseConnection(Context, connection);
                // anything else we should do at connection shutdown
            }
            base.OnClosing(connection);
        }
        public override void OnReceived(Connection connection, object value)
        {
            var proc = messageProcessor;
            if(proc != null) proc.Received(Context, connection, value);
            base.OnReceived(connection, value);
        }

        private void Heartbeat(object sender)
        {
            var tmp = messageProcessor;
            if (tmp != null)
            {
                try
                {
                    tmp.Heartbeat(Context);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("{0}\tHeartbeat: {1}", Connection.GetHeartbeatIdent(), ex.Message);
                }
            }
            WriteLog();
        }

        private int broadcastCounter;
        public override string BuildLog()
        {
            var proc = messageProcessor;
            string procStatus = proc == null ? "" : proc.ToString();
            return base.BuildLog() + " bc:" + Thread.VolatileRead(ref broadcastCounter) + " " + procStatus;
        }
        void BroadcastProcessIterator(IEnumerator<Connection> iterator, Func<Connection, object> selector, NetContext ctx)
        {

            bool cont;
            do
            {
                Connection conn;
                lock (iterator)
                {
                    cont = iterator.MoveNext();
                    conn = cont ? iterator.Current : null;
                }
                try
                {
                    if (cont && conn != null && conn.IsAlive)
                    {
                        var message = selector(conn);
                        if (message != null)
                        {
                            conn.Send(ctx, message);
                            Interlocked.Increment(ref broadcastCounter);
                        }
                    }
                }
                catch
                { // if an individual connection errors... KILL IT! and then gulp down the exception
                    try { conn.Shutdown(ctx); }
                    catch { }
                }
            } while (cont);
        }

        public void Broadcast(Func<Connection, object> selector)
        {
            // manually dual-threaded; was using Parallel.ForEach, but that caused unconstrained memory growth
            var ctx = Context;
            using (var iter = allConnections.GetEnumerator())
            using (var workerDone = new AutoResetEvent(false))
            {
                ThreadPool.QueueUserWorkItem(x =>
                {
                    BroadcastProcessIterator(iter, selector, ctx);
                    workerDone.Set();
                });
                BroadcastProcessIterator(iter, selector, ctx);
                workerDone.WaitOne();
            }
        }


        public void Broadcast(object message, Func<Connection, bool> predicate = null)
        {
            if (message == null)
            {
                // nothing to send
            }
            else if (predicate == null)
            {  // no test
                Broadcast(conn => message);
            }
            else
            {
                Broadcast(conn => predicate(conn) ? message : null);
            }
        }

        public int ConnectionTimeoutSeconds { get; set; }

    }
}