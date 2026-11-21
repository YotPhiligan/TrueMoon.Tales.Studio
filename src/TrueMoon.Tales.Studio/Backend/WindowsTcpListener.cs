using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TrueMoon.Tales.Studio.Backend;

[SupportedOSPlatform("windows")]
internal static class WindowsTcpListener
{
    private const uint AddressFamilyIpv4 = 2;
    private const uint OwnerPidListener = 3;
    private const uint InsufficientBuffer = 122;
    private const int Loopback = 0x0100007f;
    private const int RowBytes = 6 * sizeof(uint);

    internal static bool IsOwned(int port, int processId)
    {
        uint size = 0;
        var error = GetExtendedTcpTable(0, ref size, 0, AddressFamilyIpv4, OwnerPidListener, 0);
        var networkPort = (ushort)IPAddress.HostToNetworkOrder((short)port);
        for (var attempt = 0; attempt < 3 && error == InsufficientBuffer; attempt++)
        {
            var capacity = checked((int)size);
            var table = Marshal.AllocHGlobal(capacity);
            try
            {
                error = GetExtendedTcpTable(table, ref size, 0, AddressFamilyIpv4, OwnerPidListener, 0);
                if (error != 0) continue;
                var rowCount = Marshal.ReadInt32(table);
                if (rowCount < 0 || rowCount > (capacity - sizeof(uint)) / RowBytes)
                    throw new IOException("Invalid Windows TCP listener table.");
                for (var index = 0; index < rowCount; index++)
                {
                    var row = table + sizeof(uint) + index * RowBytes;
                    if (Marshal.ReadInt32(row, sizeof(uint)) == Loopback &&
                        (ushort)Marshal.ReadInt32(row, 2 * sizeof(uint)) == networkPort &&
                        Marshal.ReadInt32(row, 5 * sizeof(uint)) == processId)
                        return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new Win32Exception((int)error);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref uint size, int order, uint family, uint tableClass, uint reserved);
}
