﻿using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Discord
{
	internal abstract partial class DiscordWebSocket : IDisposable
	{
		private const int ReceiveChunkSize = 4096;
		private const int SendChunkSize = 4096;
		private const int HR_TIMEOUT = -2147012894;

		protected readonly DiscordClient _client;
		protected readonly int _sendInterval;
		protected readonly bool _isDebug;
		private readonly ConcurrentQueue<byte[]> _sendQueue;

		protected CancellationTokenSource _disconnectToken;
		protected string _host;
		protected int _timeout, _heartbeatInterval;
		protected Exception _disconnectReason;
		private ClientWebSocket _webSocket;
		private DateTime _lastHeartbeat;
		private Task _task;
		private bool _isConnected, _wasDisconnectUnexpected;

		public DiscordWebSocket(DiscordClient client, int timeout, int interval, bool isDebug)
		{
			_client = client;
            _timeout = timeout;
			_sendInterval = interval;
			_isDebug = isDebug;

			_sendQueue = new ConcurrentQueue<byte[]>();
		}

		public async Task ConnectAsync(string url)
		{
			await DisconnectAsync();

			_disconnectToken = new CancellationTokenSource();
			var cancelToken = _disconnectToken.Token;

			_webSocket = new ClientWebSocket();
			_webSocket.Options.KeepAliveInterval = TimeSpan.Zero;
			await _webSocket.ConnectAsync(new Uri(url), cancelToken);
			_host = url;

			if (_isDebug)
				RaiseOnDebugMessage(DebugMessageType.Connection, $"Connected.");

			OnConnect();

			_lastHeartbeat = DateTime.UtcNow;
			_task = Task.Factory.ContinueWhenAll(CreateTasks(), x =>
			{
				if (_isDebug)
					RaiseOnDebugMessage(DebugMessageType.Connection, $"Disconnected.");

				//Do not clean up until all tasks have ended
				OnDisconnect();

				bool wasUnexpected = _wasDisconnectUnexpected;
                _disconnectToken.Dispose();
				_disconnectToken = null;
				_wasDisconnectUnexpected = false;

				//Clear send queue
				_heartbeatInterval = 0;
				_lastHeartbeat = DateTime.MinValue;
				_webSocket.Dispose();
				_webSocket = null;
				byte[] ignored;
				while (_sendQueue.TryDequeue(out ignored)) { }

				_disconnectReason = null;
				_task = null;
				if (_isConnected)
				{
					_isConnected = false;
					RaiseDisconnected(wasUnexpected);
				}
			});
		}
		public Task ReconnectAsync()
			=> ConnectAsync(_host);
		public async Task DisconnectAsync()
		{
            if (_task != null)
			{
				try { DisconnectInternal(new Exception("Disconnect requested by user."), false); } catch (NullReferenceException) { }
				try { await _task; } catch (NullReferenceException) { }
			}
		}

		protected void DisconnectInternal()
		{
			_disconnectToken.Cancel();
		}
		protected void DisconnectInternal(Exception ex, bool isUnexpected = true)
		{
			if (_disconnectReason == null)
			{
				_wasDisconnectUnexpected = isUnexpected;
				_disconnectReason = ex;
				_disconnectToken.Cancel();
			}
		}

		protected virtual void OnConnect() { }
		protected virtual void OnDisconnect() { }

		protected void SetConnected()
		{
			_isConnected = true;
			RaiseConnected();
		}

		protected virtual Task[] CreateTasks()
		{
			return new Task[]
			{
				ReceiveAsync(),
				SendAsync()
			};
		}

		private async Task ReceiveAsync()
		{
			var cancelSource = _disconnectToken;
			var cancelToken = cancelSource.Token;
			await Task.Yield();

			var buffer = new byte[ReceiveChunkSize];
			var builder = new StringBuilder();

			try
			{
				while (_webSocket.State == WebSocketState.Open && !cancelToken.IsCancellationRequested)
				{
					WebSocketReceiveResult result = null;
					do
					{
						if (_webSocket.State != WebSocketState.Open || cancelToken.IsCancellationRequested)
							return;

                        try
						{
							result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancelToken);
						}
						catch (Win32Exception ex) 
						when (ex.HResult == HR_TIMEOUT)
						{
							string msg = $"Connection timed out.";
							RaiseOnDebugMessage(DebugMessageType.Connection, msg);
							DisconnectInternal(new Exception(msg));
							return;
						}

						if (result.MessageType == WebSocketMessageType.Close)
						{
							string msg = $"Got Close Message ({result.CloseStatus?.ToString() ?? "Unexpected"}, {result.CloseStatusDescription ?? "No Reason"})";
							RaiseOnDebugMessage(DebugMessageType.Connection, msg);
							DisconnectInternal(new Exception(msg));
							await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
							return;
						}
						else
							builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

					}
					while (result == null || !result.EndOfMessage);

#if DEBUG
					System.Diagnostics.Debug.WriteLine(">>> " + builder.ToString());
#endif
					await ProcessMessage(builder.ToString());

					builder.Clear();
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) { DisconnectInternal(ex); }
			finally { DisconnectInternal(); }
		}
		private async Task SendAsync()
		{
			var cancelSource = _disconnectToken;
			var cancelToken = cancelSource.Token;
			await Task.Yield();

			try
			{
				byte[] bytes;
				while (_webSocket.State == WebSocketState.Open && !cancelToken.IsCancellationRequested)
				{
					if (_heartbeatInterval > 0)
					{
						DateTime now = DateTime.UtcNow;
						if ((now - _lastHeartbeat).TotalMilliseconds > _heartbeatInterval)
						{
							await SendMessage(GetKeepAlive(), cancelToken);
							_lastHeartbeat = now;
						}
					}
					while (_sendQueue.TryDequeue(out bytes))
						await SendMessage(bytes, cancelToken);
					await Task.Delay(_sendInterval, cancelToken);
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) { DisconnectInternal(ex); }
			finally { DisconnectInternal(); }
		}

		protected abstract Task ProcessMessage(string json);
		protected abstract object GetKeepAlive();

        protected void QueueMessage(object message)
		{
#if DEBUG
			System.Diagnostics.Debug.WriteLine("<<< " + JsonConvert.SerializeObject(message));
#endif
			var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
			_sendQueue.Enqueue(bytes);
		}
		protected Task SendMessage(object message, CancellationToken cancelToken)
		{
#if DEBUG
			System.Diagnostics.Debug.WriteLine("<<< " + JsonConvert.SerializeObject(message));
#endif
			return SendMessage(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)), cancelToken);
		}
		protected async Task SendMessage(byte[] message, CancellationToken cancelToken)
		{
			var frameCount = (int)Math.Ceiling((double)message.Length / SendChunkSize);

			int offset = 0;
			for (var i = 0; i < frameCount; i++, offset += SendChunkSize)
			{
				bool isLast = i == (frameCount - 1);

				int count;
				if (isLast)
					count = message.Length - (i * SendChunkSize);
				else
					count = SendChunkSize;
				
				try
				{
					await _webSocket.SendAsync(new ArraySegment<byte>(message, offset, count), WebSocketMessageType.Text, isLast, cancelToken);
				}
				catch (Win32Exception ex)
				when (ex.HResult == HR_TIMEOUT)
				{
					return;
				}
			}
		}

#region IDisposable Support
		private bool _isDisposed = false;

		public void Dispose()
		{
			if (!_isDisposed)
			{
				DisconnectAsync().Wait();
				_isDisposed = true;
			}
		}
#endregion
	}
}
