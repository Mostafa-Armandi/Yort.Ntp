﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.IO;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Networking.Sockets;

namespace Yort.Ntp
{
	public partial class NtpClient
	{
		private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

		async partial void SendTimeRequest()
		{
			var socket = new Windows.Networking.Sockets.DatagramSocket();
			AsyncUdpResult asyncResult = null;
			try
			{
				var buffer = new byte[48];
				buffer[0] = 0x1B;

				socket.MessageReceived += Socket_Completed_Receive;
				asyncResult = new AsyncUdpResult(socket);
			
				await socket.ConnectAsync(new Windows.Networking.HostName(_ServerAddress), "123").AsTask().ConfigureAwait(false);

				using (var udpWriter = new DataWriter(socket.OutputStream))
				{
					udpWriter.WriteBytes(buffer);
					await udpWriter.StoreAsync().AsTask().ConfigureAwait(false);

					udpWriter.WriteBytes(buffer);
					await udpWriter.StoreAsync().AsTask().ConfigureAwait(false);

					asyncResult.Wait(OneSecond);
				}
			}
			catch (Exception ex)
			{
				try
				{
					if (socket != null)
					{
						ExecuteWithSuppressedExceptions(() => socket.MessageReceived -= this.Socket_Completed_Receive);
						ExecuteWithSuppressedExceptions(() => socket.Dispose());
					}
				}
				finally
				{
					OnErrorOccurred(ExceptionToNtpNetworkException(ex));
				}
			}
			finally
			{
				asyncResult?.Dispose();
			}
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
		private void Socket_Completed_Receive(Windows.Networking.Sockets.DatagramSocket sender, Windows.Networking.Sockets.DatagramSocketMessageReceivedEventArgs args)
		{
			try
			{
				ExecuteWithSuppressedExceptions(() => sender.MessageReceived -= this.Socket_Completed_Receive);

				byte[] buffer = null;
				using (var reader = args.GetDataReader())
				{
					buffer = new byte[reader.UnconsumedBufferLength];
					reader.ReadBytes(buffer);
				}

				ConvertBufferToCurrentTime(buffer);
			}
			catch (Exception ex)
			{
				OnErrorOccurred(ExceptionToNtpNetworkException(ex));
			}
		}

		private static NtpNetworkException ExceptionToNtpNetworkException(Exception ex)
		{
			return new NtpNetworkException(ex.Message, (int)SocketError.GetStatus(ex.HResult), ex);
		}

		#region Private Classes

		private sealed class AsyncUdpResult : IDisposable
		{
			private DatagramSocket _Socket;
			private System.Threading.ManualResetEvent _DataArrivedSignal;

			internal AsyncUdpResult(DatagramSocket socket)
			{	
				_Socket = socket;
				_Socket.MessageReceived += _Socket_MessageReceived;
				_DataArrivedSignal = new System.Threading.ManualResetEvent(false);
			}

			private void _Socket_MessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
			{
				SafeSetSignal();
			}

			private void SafeSetSignal()
			{
				try
				{
					_DataArrivedSignal?.Set();
				}
				catch (ObjectDisposedException)
				{
				}
			}

			public void Wait(TimeSpan timeout)
			{
				try
				{
					if (!(_DataArrivedSignal?.WaitOne(timeout) ?? false))
					{
						var te = new TimeoutException("No response from NTP server.");
						throw new NtpNetworkException(te.Message, (int)SocketErrorStatus.OperationAborted, te);
					}
				}
				catch (ObjectDisposedException) { }
			}

			public void Dispose()
			{
				try
				{
					var signal = _DataArrivedSignal;
					_DataArrivedSignal = null;
					signal?.Dispose();
				}
				catch (ObjectDisposedException) { }
			}
		}

		#endregion

	}
}