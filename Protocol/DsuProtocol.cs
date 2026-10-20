using System;
using System.Buffers.Binary;
using HandheldDsuServer.Models;

namespace HandheldDsuServer.Protocol;

/// <summary>
/// Cemuhook DSU (DualShock UDP) protocol constants, packet parsers, and builders.
/// Fully compatible with SteamDeckGyroDSU, Cemu, Eden, Yuzu, Ryujinx, and Dolphin (DSU v1001).
/// </summary>
public static class DsuProtocol
{
    public const ushort ProtocolVersion = 1001;
    public const int HeaderSize = 16; // Magic(4) + Version(2) + Length(2) + CRC32(4) + ID(4)

    // Message Types
    public const uint MsgTypeVersionRequest        = 0x100000;
    public const uint MsgTypeControllerInfoRequest = 0x100001;
    public const uint MsgTypeControllerDataRequest = 0x100002;

    // Virtual MAC address for Slot 0
    private static readonly byte[] DefaultMacAddress = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x01];

    /// <summary>
    /// Parses an incoming DSUC packet from an emulator client (Cemu, Eden, Yuzu, Ryujinx, Dolphin).
    /// Uses flexible length handling because some clients (e.g., Cemu 0x100001 PortInfo requests)
    /// may include C++ struct padding that differs from header.length.
    /// </summary>
    public static bool TryParseClientPacket(
        ReadOnlySpan<byte> packet,
        out uint clientId,
        out uint messageType)
    {
        clientId = 0;
        messageType = 0;

        if (packet.Length < 20)
            return false;

        // Verify magic string: 'DSUC' (Client)
        if (packet[0] != (byte)'D' ||
            packet[1] != (byte)'S' ||
            packet[2] != (byte)'U' ||
            packet[3] != (byte)'C')
        {
            return false;
        }

        clientId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12, 4));
        messageType = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(16, 4));
        return true;
    }

    /// <summary>
    /// Builds a 0x100000 (Protocol Version) response packet (24 bytes).
    /// </summary>
    public static byte[] BuildVersionResponse(uint serverId)
    {
        byte[] packet = new byte[24];
        Span<byte> span = packet;

        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), MsgTypeVersionRequest);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), ProtocolVersion);

        FinalizeServerHeader(span, serverId);
        return packet;
    }

    /// <summary>
    /// Builds a 0x100001 (Controller Info) response packet (32 bytes).
    /// Slot 0 is reported as connected with Full Gyro support; other slots are reported as disconnected.
    /// </summary>
    public static byte[] BuildControllerInfoResponse(uint serverId, byte slot)
    {
        byte[] packet = new byte[32];
        Span<byte> span = packet;

        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), MsgTypeControllerInfoRequest);
        WriteSharedControllerInfo(span.Slice(20, 11), slot, isConnected: slot == 0);
        span[31] = 0;

        FinalizeServerHeader(span, serverId);
        return packet;
    }

    /// <summary>
    /// Builds a 0x100002 (Controller Data) motion broadcast packet (100 bytes).
    /// </summary>
    public static void BuildMotionDataPacket(
        Span<byte> packet,
        uint serverId,
        uint packetNumber,
        in MotionData motion)
    {
        packet.Clear();

        // [16..19] Message Type (0x100002)
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(16, 4), MsgTypeControllerDataRequest);

        // [20..30] Shared Controller Info (11 bytes) - Slot 0 active
        WriteSharedControllerInfo(packet.Slice(20, 11), slot: 0, isConnected: true);

        // [31] Is Connected (1 = Connected)
        packet[31] = 1;

        // [32..35] Packet Number (uint32 LE)
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(32, 4), packetNumber);

        // [40..43] Left and Right Analog Sticks (Center = 128)
        packet[40] = 128;
        packet[41] = 128;
        packet[42] = 128;
        packet[43] = 128;

        // [68..75] Motion Data Timestamp (uint64 LE, in microseconds)
        BinaryPrimitives.WriteUInt64LittleEndian(packet.Slice(68, 8), motion.TimestampUs);

        // [76..87] Accelerometer Data (AccX, AccY, AccZ - float LE in G)
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(76, 4), motion.AccX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(80, 4), motion.AccY);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(84, 4), motion.AccZ);

        // [88..99] Gyroscope Data (Pitch, Yaw, Roll - float LE in deg/s)
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(88, 4), motion.GyroPitch);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(92, 4), motion.GyroYaw);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(96, 4), motion.GyroRoll);

        // [0..15] Header and CRC32 calculation
        FinalizeServerHeader(packet, serverId);
    }

    private static void WriteSharedControllerInfo(Span<byte> infoSpan, byte slot, bool isConnected)
    {
        infoSpan[0] = slot;

        if (!isConnected)
        {
            infoSpan[1] = 0; // State: Not connected
            infoSpan[2] = 0; // Model: None
            infoSpan[3] = 0; // Connection: None
            infoSpan.Slice(4, 6).Clear();
            infoSpan[10] = 0; // Battery: None
            return;
        }

        infoSpan[1] = 2; // State: Connected (2)
        infoSpan[2] = 2; // Device Model: Full Gyro / DS4 (2)
        infoSpan[3] = 1; // Connection Type: USB / Internal (1)
        DefaultMacAddress.CopyTo(infoSpan.Slice(4, 6));
        infoSpan[10] = 0x05; // Battery Status: Full (0x05)
    }

    private static void FinalizeServerHeader(Span<byte> packet, uint serverId)
    {
        packet[0] = (byte)'D';
        packet[1] = (byte)'S';
        packet[2] = (byte)'U';
        packet[3] = (byte)'S';

        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(4, 2), ProtocolVersion);

        ushort lengthWithoutHeader = (ushort)(packet.Length - HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(6, 2), lengthWithoutHeader);

        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(12, 4), serverId);

        uint crc = Crc32.Compute(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8, 4), crc);
    }
}
