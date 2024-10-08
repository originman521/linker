﻿using linker.client.config;
using linker.config;
using linker.plugins.tuntap.messenger;
using linker.plugins.tuntap.proxy;
using linker.libs;
using MemoryPack;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using linker.plugins.client;
using linker.plugins.messenger;
using linker.plugins.tuntap.config;
using linker.tun;

namespace linker.plugins.tuntap
{
    public sealed class TuntapTransfer
    {
        private readonly MessengerSender messengerSender;
        private readonly ClientSignInState clientSignInState;
        private readonly FileConfig config;
        private readonly TuntapProxy tuntapProxy;
        private readonly RunningConfig runningConfig;
        private readonly LinkerTunDeviceAdapter linkerTunDeviceAdapter;

        private string deviceName = "linker";
        private uint operating = 0;
        private List<IPAddress> routeIps = new List<IPAddress>();

        private uint infosVersion = 0;
        public uint InfosVersion => infosVersion;

        private readonly ConcurrentDictionary<string, TuntapInfo> tuntapInfos = new ConcurrentDictionary<string, TuntapInfo>();
        public ConcurrentDictionary<string, TuntapInfo> Infos => tuntapInfos;

        public TuntapStatus Status => operating == 1 ? TuntapStatus.Operating : (TuntapStatus)(byte)linkerTunDeviceAdapter.Status;

        public TuntapTransfer(MessengerSender messengerSender, ClientSignInState clientSignInState, LinkerTunDeviceAdapter linkerTunDeviceAdapter, FileConfig config, TuntapProxy tuntapProxy, RunningConfig runningConfig)
        {
            this.messengerSender = messengerSender;
            this.clientSignInState = clientSignInState;
            this.linkerTunDeviceAdapter = linkerTunDeviceAdapter;
            this.config = config;
            this.tuntapProxy = tuntapProxy;
            this.runningConfig = runningConfig;
            linkerTunDeviceAdapter.Initialize(deviceName, tuntapProxy);
            linkerTunDeviceAdapter.Shutdown();
            linkerTunDeviceAdapter.Clear();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => linkerTunDeviceAdapter.Shutdown();
            Console.CancelKeyPress += (s, e) => linkerTunDeviceAdapter.Shutdown();
            clientSignInState.NetworkFirstEnabledHandle += Initialize;

        }
        private void Initialize()
        {
            Task.Run(() =>
            {
                NetworkHelper.GetRouteLevel(out routeIps);
                NotifyConfig();
                CheckTuntapStatusTask();
                if (runningConfig.Data.Tuntap.Running)
                {
                    Setup();
                }
            });
        }

        /// <summary>
        /// 运行网卡
        /// </summary>
        public void Setup()
        {
            if (Interlocked.CompareExchange(ref operating, 1, 0) == 1)
            {
                return;
            }
            Task.Run(() =>
            {
                NotifyConfig();
                try
                {
                    if (runningConfig.Data.Tuntap.IP.Equals(IPAddress.Any))
                    {
                        return;
                    }
                    linkerTunDeviceAdapter.Setup(runningConfig.Data.Tuntap.IP, 24, 1416);
                    if (string.IsNullOrWhiteSpace(linkerTunDeviceAdapter.Error))
                    {
                        linkerTunDeviceAdapter.SetNat();
                        runningConfig.Data.Tuntap.Running = true;
                        runningConfig.Data.Update();
                    }
                    else
                    {
                        LoggerHelper.Instance.Error(linkerTunDeviceAdapter.Error);
                    }
                }
                catch (Exception ex)
                {
                    LoggerHelper.Instance.Error(ex);
                }
                finally
                {
                    Interlocked.Exchange(ref operating, 0);
                    NotifyConfig();
                }
            });
        }
        /// <summary>
        /// 停止网卡
        /// </summary>
        public void Shutdown()
        {
            if (Interlocked.CompareExchange(ref operating, 1, 0) == 1)
            {
                return;
            }
            try
            {
                NotifyConfig();
                linkerTunDeviceAdapter.Shutdown();

                runningConfig.Data.Tuntap.Running = false;
                runningConfig.Data.Update();
            }
            catch (Exception ex)
            {
                if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                {
                    LoggerHelper.Instance.Error(ex);
                }
            }
            finally
            {
                Interlocked.Exchange(ref operating, 0);
                NotifyConfig();
            }
        }

        /// <summary>
        /// 刷新信息，把自己的网卡配置发给别人，顺便把别人的网卡信息带回来
        /// </summary>
        public void RefreshConfig()
        {
            NotifyConfig();
        }
        /// <summary>
        /// 更新本机网卡信息
        /// </summary>
        /// <param name="info"></param>
        public void UpdateConfig(TuntapInfo info)
        {
            Task.Run(() =>
            {
                runningConfig.Data.Tuntap.IP = info.IP;
                runningConfig.Data.Tuntap.LanIPs = info.LanIPs;
                runningConfig.Data.Tuntap.Masks = info.Masks;
                runningConfig.Data.Tuntap.Gateway = info.Gateway;
                runningConfig.Data.Update();
                if (Status == TuntapStatus.Running)
                {
                    Shutdown();
                    Setup();
                }
                else
                {
                    NotifyConfig();
                }
            });
        }
        /// <summary>
        /// 收到别的客户端的网卡信息
        /// </summary>
        /// <param name="info"></param>
        /// <returns></returns>
        public TuntapInfo OnConfig(TuntapInfo info)
        {
            Task.Run(() =>
            {
                DelRoute();
                tuntapInfos.AddOrUpdate(info.MachineId, info, (a, b) => info);
                Interlocked.Increment(ref infosVersion);
                AddRoute();
            });

            return GetLocalInfo();
        }
        /// <summary>
        /// 信息有变化，刷新信息，把自己的网卡配置发给别人，顺便把别人的网卡信息带回来
        /// </summary>
        private void NotifyConfig()
        {
            GetRemoteInfo().ContinueWith((result) =>
            {
                if (result.Result == null)
                {
                    NotifyConfig();
                }
                else
                {
                    DelRoute();
                    foreach (var item in result.Result)
                    {
                        tuntapInfos.AddOrUpdate(item.MachineId, item, (a, b) => item);
                    }
                    Interlocked.Increment(ref infosVersion);
                    AddRoute();
                }
            });
        }
        /// <summary>
        /// 获取自己的网卡信息
        /// </summary>
        /// <returns></returns>
        private TuntapInfo GetLocalInfo()
        {
            TuntapInfo info = new TuntapInfo
            {
                IP = runningConfig.Data.Tuntap.IP,
                LanIPs = runningConfig.Data.Tuntap.LanIPs,
                Masks = runningConfig.Data.Tuntap.Masks,
                MachineId = config.Data.Client.Id,
                Status = Status,
                Error = linkerTunDeviceAdapter.Error,
                System = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} {(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNLTTY_LINKER_IS_DOCKER")) == false ? "Docker" : "")}",
                Gateway = runningConfig.Data.Tuntap.Gateway,
            };
            if (runningConfig.Data.Tuntap.Masks.Length != runningConfig.Data.Tuntap.LanIPs.Length)
            {
                runningConfig.Data.Tuntap.Masks = runningConfig.Data.Tuntap.LanIPs.Select(c => 24).ToArray();
            }

            return info;
        }
        /// <summary>
        /// 获取别人的网卡信息
        /// </summary>
        /// <returns></returns>
        private async Task<List<TuntapInfo>> GetRemoteInfo()
        {
            TuntapInfo info = GetLocalInfo();
            tuntapInfos.AddOrUpdate(info.MachineId, info, (a, b) => info);
            MessageResponeInfo resp = await messengerSender.SendReply(new MessageRequestWrap
            {
                Connection = clientSignInState.Connection,
                MessengerId = (ushort)TuntapMessengerIds.ConfigForward,
                Payload = MemoryPackSerializer.Serialize(info),
                Timeout = 3000
            }).ConfigureAwait(false);
            if (resp.Code != MessageResponeCodes.OK)
            {
                return null;
            }

            List<TuntapInfo> infos = MemoryPackSerializer.Deserialize<List<TuntapInfo>>(resp.Data.Span);
            infos.Add(info);
            return infos;
        }

        /// <summary>
        /// 删除路由
        /// </summary>
        private void DelRoute()
        {
            List<TuntapVeaLanIPAddressList> ipsList = ParseIPs(tuntapInfos.Values.ToList());
            TuntapVeaLanIPAddress[] ips = ipsList.SelectMany(c => c.IPS).ToArray();

            var items = ipsList.SelectMany(c => c.IPS).Select(c => new LinkerTunDeviceRouteItem { Address = c.OriginIPAddress, PrefixLength = c.MaskLength }).ToArray();
            linkerTunDeviceAdapter.DelRoute(items, runningConfig.Data.Tuntap.Gateway);
        }
        /// <summary>
        /// 添加路由
        /// </summary>
        private void AddRoute()
        {
            List<TuntapVeaLanIPAddressList> ipsList = ParseIPs(tuntapInfos.Values.ToList());
            TuntapVeaLanIPAddress[] ips = ipsList.SelectMany(c => c.IPS).ToArray();

            var items = ipsList.SelectMany(c => c.IPS).Select(c => new LinkerTunDeviceRouteItem { Address = c.OriginIPAddress, PrefixLength = c.MaskLength }).ToArray();
            linkerTunDeviceAdapter.AddRoute(items, runningConfig.Data.Tuntap.IP, runningConfig.Data.Tuntap.Gateway);

            tuntapProxy.SetIPs(ipsList);
            foreach (var item in tuntapInfos.Values)
            {
                tuntapProxy.SetIP(item.MachineId, BinaryPrimitives.ReadUInt32BigEndian(item.IP.GetAddressBytes()));
            }
        }

        private List<TuntapVeaLanIPAddressList> ParseIPs(List<TuntapInfo> infos)
        {
            uint[] localIps = NetworkHelper.GetIPV4()
                .Concat(new IPAddress[] { runningConfig.Data.Tuntap.IP })
                .Concat(runningConfig.Data.Tuntap.LanIPs)
                .Concat(routeIps)
                .Select(c => BinaryPrimitives.ReadUInt32BigEndian(c.GetAddressBytes()))
                .ToArray();

            return infos
                //自己的ip不要
                .Where(c => c.IP.Equals(runningConfig.Data.Tuntap.IP) == false)
                .Select(c =>
                {
                    return new TuntapVeaLanIPAddressList
                    {
                        MachineId = c.MachineId,
                        IPS = ParseIPs(c.LanIPs, c.Masks)
                        //这边的局域网IP也不要，为了防止将本机局域网IP路由到别的地方
                        .Where(c => localIps.Select(d => d & c.MaskValue).Contains(c.NetWork) == false).ToList(),
                    };
                }).ToList();
        }
        private List<TuntapVeaLanIPAddress> ParseIPs(IPAddress[] lanIPs, int[] masks)
        {
            if (masks.Length != lanIPs.Length) masks = lanIPs.Select(c => 24).ToArray();
            return lanIPs.Where(c => c.Equals(IPAddress.Any) == false).Select((c, index) =>
            {
                return ParseIPAddress(c, (byte)masks[index]);

            }).ToList();
        }
        private TuntapVeaLanIPAddress ParseIPAddress(IPAddress ip, byte maskLength = 24)
        {
            uint ipInt = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
            //掩码十进制
            uint maskValue = NetworkHelper.MaskValue(maskLength);
            return new TuntapVeaLanIPAddress
            {
                IPAddress = ipInt,
                MaskLength = maskLength,
                MaskValue = maskValue,
                NetWork = ipInt & maskValue,
                Broadcast = ipInt | (~maskValue),
                OriginIPAddress = ip,
            };
        }

        private void CheckTuntapStatusTask()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(15000).ConfigureAwait(false);
                    try
                    {
                        if (runningConfig.Data.Tuntap.Running && OperatingSystem.IsWindows())
                        {
                            await CheckInterface().ConfigureAwait(false);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            });
        }
        private async Task CheckInterface()
        {
            NetworkInterface networkInterface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(c => c.Name == deviceName);

            if (networkInterface == null || networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                LoggerHelper.Instance.Error($"tuntap inerface {deviceName} is {networkInterface?.OperationalStatus ?? OperationalStatus.Unknown}, restarting");
                Shutdown();
                await Task.Delay(5000).ConfigureAwait(false);

                networkInterface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(c => c.Name == deviceName);
                if (networkInterface == null || networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    Setup();
                }
            }
        }
    }
}
