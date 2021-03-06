﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Text;
using System.Threading;
using ServerSuperIO.Common;
using ServerSuperIO.Device;
using ServerSuperIO.Protocol;
using ServerSuperIO.Server;

namespace ServerSuperIO.Communicate.NET
{
    public class TcpSocketSession : SocketSession
    {
        /// <summary>
        /// 无数状态下记数器
        /// </summary>
        //private int _NoneDataCounter = 0;

        /// <summary>
        /// 设置多长时间后检测网络状态
        /// </summary>
        private byte[] _KeepAliveOptionValues;

        /// <summary>
        /// 设置检测网络状态间隔时间
        /// </summary>
        private byte[] _KeepAliveOptionOutValues;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="remoteEndPoint"></param>
        /// <param name="proxy"></param>
        public TcpSocketSession(Socket socket, IPEndPoint remoteEndPoint, ISocketAsyncEventArgsProxy proxy)
            : base(socket, remoteEndPoint, proxy)
        {
        }

        public override void Initialize()
        {
            if (Client != null)
            {
                //-------------------初始化心跳检测---------------------//
                uint dummy = 0;
                _KeepAliveOptionValues = new byte[Marshal.SizeOf(dummy) * 3];
                _KeepAliveOptionOutValues = new byte[_KeepAliveOptionValues.Length];
                BitConverter.GetBytes((uint)1).CopyTo(_KeepAliveOptionValues, 0);
                BitConverter.GetBytes((uint)(2000)).CopyTo(_KeepAliveOptionValues, Marshal.SizeOf(dummy));

                uint keepAlive = this.Server.ServerConfig.KeepAlive;

                BitConverter.GetBytes((uint)(keepAlive)).CopyTo(_KeepAliveOptionValues, Marshal.SizeOf(dummy) * 2);

                Client.IOControl(IOControlCode.KeepAliveValues, _KeepAliveOptionValues, _KeepAliveOptionOutValues);

                Client.NoDelay = true;
                Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
                //----------------------------------------------------//

                Client.ReceiveTimeout = Server.ServerConfig.NetReceiveTimeout;
                Client.SendTimeout = Server.ServerConfig.NetSendTimeout;
                Client.ReceiveBufferSize = Server.ServerConfig.NetReceiveBufferSize;
                Client.SendBufferSize = Server.ServerConfig.NetSendBufferSize;
            }

            if (SocketAsyncProxy != null)
            {
                SocketAsyncProxy.Initialize(this);
                SocketAsyncProxy.SocketReceiveEventArgsEx.Completed += SocketEventArgs_Completed;
                SocketAsyncProxy.SocketSendEventArgs.Completed += SocketEventArgs_Completed;
            }
        }

        ///// <summary>
        ///// 读操作
        ///// </summary>
        ///// <returns></returns>
        //public override byte[] Read()
        //{
        //    IList<byte[]> listBytes = ReadReceiveFilter(null);
        //    if (listBytes != null)
        //    {
        //        return listBytes[0];
        //    }
        //    else
        //    {
        //        return null;
        //    }
        //}

        ///// <summary>
        ///// 读IO，带过滤器
        ///// </summary>
        ///// <param name="receiveFilter"></param>
        ///// <returns></returns>
        //public override IList<byte[]> Read(IReceiveFilter receiveFilter)
        //{
        //    if (receiveFilter == null)
        //    {
        //        throw new NullReferenceException("receiveFilter为空");
        //    }

        //    return ReadReceiveFilter(receiveFilter);
        //}

        protected override IList<byte[]> ReceiveDataFilter(IReceiveFilter receiveFilter)
        {
            if (!this.IsDisposed)
            {
                System.Threading.Thread.Sleep(Server.ServerConfig.NetLoopInterval);
                if (this.Client.Connected)
                {
                    if (this.Client.Poll(10, SelectMode.SelectRead))
                    {
                        try
                        {
                            if (SocketAsyncProxy.SocketReceiveEventArgsEx.NextOffset >= SocketAsyncProxy.SocketReceiveEventArgsEx.InitOffset + SocketAsyncProxy.SocketReceiveEventArgsEx.Capacity)
                            {
                                SocketAsyncProxy.SocketReceiveEventArgsEx.Reset();
                            }

                            byte[] buffer = SocketAsyncProxy.SocketReceiveEventArgsEx.ReceiveBuffer;

                            #region
                            int num = this.Client.Receive(buffer, SocketAsyncProxy.SocketReceiveEventArgsEx.NextOffset, SocketAsyncProxy.SocketReceiveEventArgsEx.InitOffset + SocketAsyncProxy.SocketReceiveEventArgsEx.Capacity - SocketAsyncProxy.SocketReceiveEventArgsEx.NextOffset, SocketFlags.None);

                            if (num <= 0)
                            {
                                throw new SocketException((int)SocketError.HostDown);
                            }
                            else
                            {
                                //this._NoneDataCounter = 0;

                                LastActiveTime = DateTime.Now;

                                SocketAsyncProxy.SocketReceiveEventArgsEx.DataLength += num;
                                if (receiveFilter == null)
                                {
                                    IList<byte[]> listBytes = new List<byte[]>();
                                    listBytes.Add(SocketAsyncProxy.SocketReceiveEventArgsEx.Get());
                                    return listBytes;
                                }
                                else
                                {
                                    return SocketAsyncProxy.SocketReceiveEventArgsEx.Get(receiveFilter);
                                }
                            }

                            #endregion
                        }
                        catch (SocketException)
                        {
                            OnCloseSocket();
                            throw new SocketException((int)SocketError.HostDown);
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    OnCloseSocket();
                    throw new SocketException((int)SocketError.HostDown);
                }
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// 写操作
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public override int Write(byte[] data)
        {
            if (!this.IsDisposed)
            {
                if (this.Client.Connected)
                {
                    try
                    {
                        int successNum = 0;
                        int num = 0;
                        while (num < data.Length)
                        {
                            int remainLength = data.Length - num;
                            int sendLength = remainLength >= this.Client.SendBufferSize ? this.Client.SendBufferSize : remainLength;

                            SocketError error;
                            successNum += this.Client.Send(data, num, sendLength, SocketFlags.None, out error);

                            num += sendLength;

                            if (successNum <= 0 || error != SocketError.Success)
                            {
                                OnCloseSocket();
                                throw new SocketException((int)SocketError.HostDown);
                            }
                        }

                        return successNum;
                    }
                    catch (SocketException)
                    {
                        OnCloseSocket();
                        throw;
                    }
                }
                else
                {
                    OnCloseSocket();
                    throw new SocketException((int)SocketError.HostDown);
                }
            }
            else
            {
                return 0;
            }
        }

        public override void TryReceive()
        {
            if (Client != null)
            {
                try
                {
                    bool willRaiseEvent = this.Client.ReceiveAsync(this.SocketAsyncProxy.SocketReceiveEventArgsEx);
                    if (!willRaiseEvent)
                    {
                        ProcessReceive(this.SocketAsyncProxy.SocketReceiveEventArgsEx);
                    }
                }
                catch (Exception ex)
                {
                    this.Server.Logger.Error(true, ex.Message);
                }
            }
        }

        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            ISocketSession socketSession = (ISocketSession)e.UserToken;
            if (socketSession != null && socketSession.Client != null)
            {
                try
                {
                    if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
                    {
                        SocketAsyncEventArgsEx saeaEx = (SocketAsyncEventArgsEx)e;
                        if (saeaEx.NextOffset >= saeaEx.InitOffset + saeaEx.Capacity)
                        {
                            saeaEx.Reset();
                        }

                        saeaEx.DataLength += saeaEx.BytesTransferred;

                        IReceivePackage dataPackage = new ReceivePackage();
                        dataPackage.RemoteIP = this.RemoteIP;
                        dataPackage.RemotePort = this.RemotePort;

                        #region 过滤数据

                        if (this.Server.ServerConfig.StartReceiveDataFliter)
                        {
                            IRunDevice[] devList = this.Server.DeviceManager.GetDevices(CommunicateType.NET);
                            if (devList != null && devList.Length > 0)
                            {
                                if (this.Server.ServerConfig.ControlMode == ControlMode.Loop
                                    || this.Server.ServerConfig.ControlMode == ControlMode.Self
                                    || this.Server.ServerConfig.ControlMode == ControlMode.Parallel)
                                {
                                    #region
                                    byte[] data = new byte[saeaEx.DataLength];
                                    Buffer.BlockCopy(saeaEx.ReceiveBuffer, saeaEx.InitOffset, data, 0, data.Length);
                                    IRunDevice dev = null;
                                    try
                                    {
                                        dev = devList.FirstOrDefault(d => d.DeviceParameter.DeviceCode == d.Protocol.GetCode(data));
                                    }
                                    catch (Exception ex)
                                    {
                                        this.Server.Logger.Error(true, ex.Message);
                                    }

                                    if (dev != null)
                                    {
                                        ProcessFliterData(dataPackage, saeaEx, dev);
                                    }

                                    #endregion
                                }
                                else if (this.Server.ServerConfig.ControlMode == ControlMode.Singleton)
                                {
                                    ProcessFliterData(dataPackage, saeaEx, devList[0]);
                                }
                            }
                        }
                        else
                        {
                            ProcessFliterData(dataPackage, saeaEx, null);
                        }

                        #endregion

                        saeaEx.SetBuffer(saeaEx.ReceiveBuffer, saeaEx.NextOffset,saeaEx.InitOffset + saeaEx.Capacity - saeaEx.NextOffset);

                        OnSocketReceiveData(dataPackage);

                        bool willRaiseEvent = socketSession.Client.ReceiveAsync(this.SocketAsyncProxy.SocketReceiveEventArgsEx);
                        if (!willRaiseEvent)
                        {
                            ProcessReceive(this.SocketAsyncProxy.SocketReceiveEventArgsEx);
                        }
                    }
                    else
                    {
                        OnCloseSocket();
                    }
                }
                catch (SocketException ex)
                {
                    OnCloseSocket();
                    this.Server.Logger.Error(true, ex.Message);
                }
                catch (Exception ex)
                {
                    this.Server.Logger.Error(true, ex.Message);
                }
            }
        }

        private void ProcessFliterData(IReceivePackage dataPackage, SocketAsyncEventArgsEx saeaEx, IRunDevice dev)
        {
            if (dev == null || dev.Protocol == null || dev.Protocol.ReceiveFilter == null)
            {
                if (dataPackage.ListBytes == null)
                {
                    dataPackage.ListBytes = new List<byte[]>();
                }
                byte[] data = saeaEx.Get();
                if (data.Length > 0)
                {
                    LastActiveTime = DateTime.Now;
                }
                dataPackage.ListBytes.Add(data);
            }
            else
            {
                dataPackage.DeviceCode = dev.DeviceParameter.DeviceCode;
                IList<byte[]> listBytes = saeaEx.Get(dev.Protocol.ReceiveFilter);
                if (listBytes != null && listBytes.Count > 0)
                {
                    LastActiveTime = DateTime.Now;
                }
                dataPackage.ListBytes = listBytes;
            }
        }

        private void ProcessSend(SocketAsyncEventArgs e)
        {
            try
            {
                if (e.SocketError == SocketError.Success)
                {
                    byte[] data = (byte[])e.UserToken;

                    if (e.BytesTransferred < data.Length)
                    {
                        e.SetBuffer(data, e.BytesTransferred, data.Length - e.BytesTransferred);
                        bool willRaiseEvent = this.Client.SendAsync(e);
                        if (!willRaiseEvent)
                        {
                            ProcessSend(e);
                        }
                    }
                    else
                    {
                        e.UserToken = null;
                    }
                }
                else
                {
                    OnCloseSocket();
                }
            }
            catch (SocketException ex)
            {
                OnCloseSocket();
                this.Server.Logger.Error(true, ex.Message);
            }
            catch (Exception ex)
            {
                this.Server.Logger.Error(true, ex.Message);
            }
        }

        protected override void SendAsync(byte[] data)
        {
            if (Client != null)
            {
                try
                {
                    this.SocketAsyncProxy.SocketSendEventArgs.UserToken = data;
                    this.SocketAsyncProxy.SocketSendEventArgs.SetBuffer(data, 0, data.Length);
                    bool willRaiseEvent = this.Client.SendAsync(this.SocketAsyncProxy.SocketSendEventArgs);

                    if (!willRaiseEvent)
                    {
                        ProcessSend(this.SocketAsyncProxy.SocketSendEventArgs);
                    }
                }
                catch (Exception ex)
                {
                    this.Server.Logger.Error(true, ex.Message);
                }
            }
        }

        protected override void SendSync(byte[] data)
        {
            if (Client != null)
            {
                try
                {
                    this.Client.SendData(data);
                }
                catch (SocketException ex)
                {
                    OnCloseSocket();
                    this.Server.Logger.Error(true, ex.Message);
                }
                catch (Exception ex)
                {
                    this.Server.Logger.Error(true, ex.Message);
                }
            }
        }

        protected override void SocketEventArgs_Completed(object sender, SocketAsyncEventArgs e)
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                default:
                    this.Server.Logger.Info(false, "不支持接收和发送的操作");
                    break;
            }
        }
    }
}
