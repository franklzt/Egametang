﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Model
{
	public class KChannel: AChannel
	{
		private UdpClient socket;

		private Kcp kcp;

		private readonly TBuffer recvBuffer = new TBuffer(8 * 1024);
		private readonly Queue<byte[]> sendBuffer = new Queue<byte[]>();

		private readonly PacketParser parser;
		private bool isConnected;
		private readonly IPEndPoint remoteEndPoint;

		private TaskCompletionSource<byte[]> recvTcs;

		private uint lastRecvTime;

		private readonly byte[] cacheBytes = new byte[1400];

		public uint Conn;

		public uint RemoteConn;

		// accept
		public KChannel(uint conn, uint remoteConn, UdpClient socket, IPEndPoint remoteEndPoint, KService kService): base(kService, ChannelType.Accept)
		{
			this.Id = conn;
			this.Conn = conn;
			this.RemoteConn = remoteConn;
			this.remoteEndPoint = remoteEndPoint;
			this.socket = socket;
			this.parser = new PacketParser(this.recvBuffer);
			kcp = new Kcp(this.Conn, this.Output);
			kcp.SetMtu(512);
			kcp.NoDelay(1, 10, 2, 1);  //fast
			this.isConnected = true;
			this.lastRecvTime = kService.TimeNow;
		}

		// connect
		public KChannel(uint conn, UdpClient socket, IPEndPoint remoteEndPoint, KService kService): base(kService, ChannelType.Connect)
		{
			this.Id = conn;
			this.Conn = conn;
			this.socket = socket;
			this.parser = new PacketParser(this.recvBuffer);
			this.remoteEndPoint = remoteEndPoint;
			this.lastRecvTime = kService.TimeNow;
			this.Connect(kService.TimeNow);
		}

		public override void Dispose()
		{
			if (this.Id == 0)
			{
				return;
			}

			base.Dispose();

			this.socket = null;
		}

		private KService GetService()
		{
			return (KService)this.service;
		}

		public void HandleConnnect(uint responseConn)
		{
			if (this.isConnected)
			{
				return;
			}
			this.isConnected = true;
			this.RemoteConn = responseConn;
			this.kcp = new Kcp(responseConn, this.Output);
			kcp.SetMtu(512);
			kcp.NoDelay(1, 10, 2, 1);  //fast

			HandleSend();
		}

		public void HandleAccept(uint requestConn)
		{
			cacheBytes.WriteTo(0, KcpProtocalType.ACK);
			cacheBytes.WriteTo(4, requestConn);
			cacheBytes.WriteTo(8, this.Conn);
			this.socket.Send(cacheBytes, 12, remoteEndPoint);
		}

		/// <summary>
		/// 发送请求连接消息
		/// </summary>
		private void Connect(uint timeNow)
		{
			cacheBytes.WriteTo(0, KcpProtocalType.SYN);
			cacheBytes.WriteTo(4, this.Conn);
			this.socket.Send(cacheBytes, 8, remoteEndPoint);

			// 200毫秒后再次update发送connect请求
			this.GetService().AddToNextTimeUpdate(timeNow + 200, this.Id);
		}

		public void Update(uint timeNow)
		{
			// 如果还没连接上，发送连接请求
			if (!this.isConnected)
			{
				Connect(timeNow);
				return;
			}

			// 超时断开连接
			if (timeNow - this.lastRecvTime > 20 * 1000)
			{
				this.OnError(this, SocketError.Disconnecting);
				return;
			}

			this.kcp.Update(timeNow);

			uint nextUpdateTime = this.kcp.Check(timeNow);
			this.GetService().AddToNextTimeUpdate(nextUpdateTime, this.Id);
		}

		private void HandleSend()
		{
			while (true)
			{
				if (this.sendBuffer.Count <= 0)
				{
					break;
				}
				byte[] buffer = this.sendBuffer.Dequeue();
				this.KcpSend(buffer);
			}
		}

		public void HandleRecv(byte[] date, uint timeNow)
		{
			this.kcp.Input(date);
			// 加入update队列
			this.GetService().AddToUpdate(this.Id);

			lastRecvTime = timeNow;

			while (true)
			{
				int n = kcp.PeekSize();
				if (n == 0)
				{
					this.OnError(this, SocketError.NetworkReset);
					return;
				}
				int count = this.kcp.Recv(cacheBytes);
				if (count <= 0)
				{
					return;
				}
				
				// 收到的数据放入缓冲区
				this.recvBuffer.SendTo(this.cacheBytes, 0, count);

				if (this.recvTcs != null)
				{
					byte[] packet = this.parser.GetPacket();
					if (packet != null)
					{
						var tcs = this.recvTcs;
						this.recvTcs = null;
						tcs.SetResult(packet);
					}
				}
			}
		}
		
		public void Output(byte[] bytes, int count)
		{
			this.socket.Send(bytes, count, this.remoteEndPoint);
		}

		private void KcpSend(byte[] buffers)
		{
			this.kcp.Send(buffers);
			this.GetService().AddToUpdate(this.Id);
		}
		
		public override void Send(byte[] buffer)
		{
			if (isConnected)
			{
				this.KcpSend(buffer);
				return;
			}
			this.sendBuffer.Enqueue(buffer);
		}

		public override void Send(List<byte[]> buffers)
		{
			ushort size = (ushort)buffers.Select(b => b.Length).Sum();
			byte[] sizeBuffer = BitConverter.GetBytes(size);
			if (isConnected)
			{
				this.KcpSend(sizeBuffer);
			}
			else
			{
				this.sendBuffer.Enqueue(sizeBuffer);
			}

			foreach (byte[] buffer in buffers)
			{
				if (isConnected)
				{
					this.KcpSend(buffer);
					continue;
				}
				this.sendBuffer.Enqueue(buffer);
			}
		}

		public override Task<byte[]> Recv()
		{
			if (this.Id == 0)
			{
				throw new Exception("TChannel已经被Dispose, 不能接收消息");
			}

			byte[] packet = this.parser.GetPacket();
			if (packet != null)
			{
				return Task.FromResult(packet);
			}

			recvTcs = new TaskCompletionSource<byte[]>();
			return recvTcs.Task;
		}
	}
}
