#if STORE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RemoteController
{
    internal static class StoreRelease
    {
        private static readonly Guid NetworkListManager = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

        // The gaps preserve the SDK's COM vtable positions for methods this app does not call.
        [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkListManager
        {
            void _VtblGap1_2();
            IEnumNetworkConnections GetNetworkConnections();
        }

        [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IEnumNetworkConnections
        {
            void _VtblGap1_1();
            [PreserveSig]
            int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection, ref uint fetched);
        }

        [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkConnection
        {
            INetwork GetNetwork();
            void _VtblGap1_1();
            [return: MarshalAs(UnmanagedType.VariantBool)]
            bool GetIsConnected();
            void _VtblGap2_2();
            Guid GetAdapterId();
        }

        [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetwork
        {
            void _VtblGap1_11();
            int GetCategory();
        }

        public static IEnumerable<NetworkInterface> PrivateAdapters(IEnumerable<NetworkInterface> adapters)
        {
            var privateIds = new HashSet<Guid>();
            var otherIds = new HashSet<Guid>();
            try
            {
                var manager = (INetworkListManager)Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManager));
                var connections = manager.GetNetworkConnections();
                while (true)
                {
                    INetworkConnection connection;
                    uint fetched = 0;
                    int result = connections.Next(1, out connection, ref fetched);
                    if (result < 0) Marshal.ThrowExceptionForHR(result);
                    if (fetched == 0) break;
                    if (!connection.GetIsConnected()) continue;
                    Guid adapter = connection.GetAdapterId();
                    if (connection.GetNetwork().GetCategory() == 1) privateIds.Add(adapter);
                    else otherIds.Add(adapter);
                }
            }
            catch (COMException) { return Enumerable.Empty<NetworkInterface>(); }
            catch (InvalidCastException) { return Enumerable.Empty<NetworkInterface>(); }
            catch (ArgumentException) { return Enumerable.Empty<NetworkInterface>(); }
            privateIds.ExceptWith(otherIds);
            return adapters.Where(adapter =>
            {
                Guid id;
                return adapter.OperationalStatus == OperationalStatus.Up && Guid.TryParse(adapter.Id, out id) && privateIds.Contains(id);
            });
        }

        public static bool AllowConnection(TcpClient client)
        {
            var local = (IPEndPoint)client.Client.LocalEndPoint;
            var peer = (IPEndPoint)client.Client.RemoteEndPoint;
            if (IPAddress.IsLoopback(local.Address) && IPAddress.IsLoopback(peer.Address)) return true;
            if (peer.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(peer.Address)) return false;
            try
            {
                return PrivateAdapters(NetworkInterface.GetAllNetworkInterfaces())
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Any(address => address.Address.Equals(local.Address) && SameSubnet(local.Address, peer.Address, address.IPv4Mask));
            }
            catch (NetworkInformationException) { return false; }
        }

        internal static bool SameSubnet(IPAddress local, IPAddress peer, IPAddress mask)
        {
            if (mask == null || mask.Equals(IPAddress.Any) || mask.Equals(IPAddress.Broadcast)) return false;
            var left = local.GetAddressBytes();
            var right = peer.GetAddressBytes();
            var bits = mask.GetAddressBytes();
            if (left.Length != 4 || right.Length != 4 || bits.Length != 4) return false;
            for (int index = 0; index < bits.Length; index++)
                if ((left[index] & bits[index]) != (right[index] & bits[index])) return false;
            return true;
        }

        public static void OpenStartupSettings() { Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }

        public static void OpenPrivacyPolicy()
        {
            using (var stream = typeof(StoreRelease).Assembly.GetManifestResourceStream("Release.privacy-url"))
            using (var reader = new StreamReader(stream))
                Process.Start(new ProcessStartInfo(reader.ReadToEnd().Trim()) { UseShellExecute = true });
        }
    }
}
#endif
