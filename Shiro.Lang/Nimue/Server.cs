﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Guts;
using Shiro.Support;

namespace Shiro.Nimue
{
    internal static class Server
    {
        internal static bool Serving = false;

        private static InterpreterPool _pool = null;
        private static Interpreter _shiro;

        internal static ConnectionType ConType
        {
            get { return Connection.ConType; }
            set { Connection.ConType = value; }
        }

        internal static class Locks
        {
            public static readonly object ConnectionsLock = new object();
            public static readonly object ShiroLock = new object();
        }

        private static List<Connection> Connections = new List<Connection>();
        private static int Port = 4676;

        private static Token HandlerToken, ConnectHandlerToken;

        private static void Listener()
        {
            IPAddress localAdd = IPAddress.Parse("127.0.0.1");
            TcpListener listener = new TcpListener(localAdd, Port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Start();

            while (Serving)
            {
                while(!listener.Pending())
                    _shiro.DispatchPublications();

                TcpClient client = listener.AcceptTcpClient();

                var conn = new Connection(client);
                conn.ConnectionId = Guid.NewGuid();

                lock (Locks.ConnectionsLock)
                {
                    Connections.Add(conn);
                }

                if (ConnectHandlerToken != null)
                {
                    EvaluateTokenWithLets(conn, ConnectHandlerToken).Start();
                }
            }

            listener.Stop();
        }

        private static void UpdateConnections()
        {
            var newConns = new List<Connection>();
            List<Connection> conCopy;

            lock (Locks.ConnectionsLock)
                conCopy = Connections.ToArray().ToList();

            foreach (var con in conCopy)
            {
                if (!con.ShouldBeNuked)
                {
                    newConns.Add(con);
                    con.CheckForInput();
                    if (con.HasFullCommand)
                    {
                        #pragma warning disable CS4014
                        ProcessCommand(con);
                        #pragma warning restore CS4014
                    }
                }
                else
                {
                    con.CleanUp();
                }

                lock (Locks.ConnectionsLock)
                    Connections = newConns;
            }
        }

        private static async Task ProcessCommand(Connection con)
        {
            var result = await EvaluateTokenWithLets(con, HandlerToken);

            if (ConType == ConnectionType.HTTP)
            {
                HttpHelper.SendHttpResponse(con, result.ToString());
                con.SetForNuking();
            }
        }

        private static async Task<Token> EvaluateTokenWithLets(Connection con, Token handler)
        {
			Guid letId = Guid.NewGuid();
			try
			{
                var ipi = _pool.Begin()
                    .Let(Symbols.AutoVars.ConnectionId, new Token(con.ConnectionId.ToString()));

				if (ConType != ConnectionType.HTTP)
					ipi.Let(Symbols.AutoVars.TelnetInput, new Token(con.GetCommand()));
				else
				{
					var request = HttpHelper.ParseRequest(con.GetCommand());
					var token = HttpHelper.RequestToToken(request);
					ipi.Let(Symbols.AutoVars.HttpRequest, token);
				}

                var retVal = await ipi.Eval(handler);
				return retVal;
			}
			catch (ApplicationException aex)
			{
				Console.WriteLine($"Server error: {aex.Message}");
			}

			return Token.Nil;
        }

        internal static void SendTo(Guid conId, string msg)
        {
            Connection conn = null;
            lock (Locks.ConnectionsLock)
            {
                conn = Connections.FirstOrDefault(c => c.ConnectionId == conId);
            }

            SendTo(conn, msg);
        }
        internal static void SendTo(Connection conn, string msg)
        {
            if (conn != null)
                conn.Send(msg);
            else
                Interpreter.Error($"Attempt to send on non-existent socket, data '{msg}'");
        }
        internal static void SendToAll(string msg)
        {
            lock (Locks.ConnectionsLock)
            {
                foreach (var conn in Connections)
                {
                    try
                    {
                        conn.Send(msg);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        internal static void ListenForTelnetOrTcp(Interpreter shiro, Token commandHandler, int port = 4676, Token connectHandler = null, bool isTcp = false)
        {
            ConType = isTcp ? ConnectionType.TCP : ConnectionType.MUD;
            Port = port;
            HandlerToken = commandHandler;
            ConnectHandlerToken = connectHandler;

            _shiro = shiro;

            using (_pool = new InterpreterPool(shiro))
            {
                Serving = true;

                //Listen thread
                var ts = new ThreadStart(Listener);
                var thread = new Thread(ts);
                thread.Start();

                //Receive thread
                var ts2 = new ThreadStart(() =>
                {
                    while (Serving)
                    {
                    //Receive loop
                    UpdateConnections();
                        Thread.Sleep(50);
                    }

                    lock (Locks.ConnectionsLock)
                    {
                        foreach (var con in Connections)
                            con.CleanUp(false);
                        Connections.Clear();
                    }

                });
                var thread2 = new Thread(ts2);
                thread2.Start();

                Result = null;
                while (Serving)
                {
                    Thread.Sleep(50);
                }
            }
            _pool = null;
        }

        internal static void ListenForHttp(Interpreter shiro, Token commandHandler, int port = 8088)
        {
            ConType = ConnectionType.HTTP;
            Port = port;
            HandlerToken = commandHandler;
            ConnectHandlerToken = null;

            _shiro = shiro;

            using (_pool = new InterpreterPool(shiro))
            {
                Serving = true;

                //Listen thread
                var ts = new ThreadStart(Listener);
                var thread = new Thread(ts);
                thread.Start();

                //Receive thread
                var ts2 = new ThreadStart(() =>
                {
                    while (Serving)
                    {
                    //Receive loop
                    UpdateConnections();
                        Thread.Sleep(50);
                    }

                    lock (Locks.ConnectionsLock)
                    {
                        foreach (var con in Connections)
                            con.CleanUp(false);
                        Connections.Clear();
                    }

                });
                var thread2 = new Thread(ts2);
                thread2.Start();

                Result = null;
                while (Serving)
                {
                    Thread.Sleep(50);
                }
            }
            _pool = null;
        }

        internal static Token Result = null;
    }
}
