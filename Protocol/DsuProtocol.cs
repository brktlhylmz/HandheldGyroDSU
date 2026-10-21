using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
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

    // XInput 1.4 constants for optional full gamepad button/stick forwarding inside DSU 0x100002 packets
    private const ushort XINPUT_GAMEPAD_DPAD_UP        = 0x0001;
    private const ushort XINPUT_GAMEPAD_DPAD_DOWN      = 0x0002;
    private const ushort XINPUT_GAMEPAD_DPAD_LEFT      = 0x0004;
    private const ushort XINPUT_GAMEPAD_DPAD_RIGHT     = 0x0008;
    private const ushort XINPUT_GAMEPAD_START          = 0x0010;
    private const ushort XINPUT_GAMEPAD_BACK           = 0x0020;
    private const ushort XINPUT_GAMEPAD_LEFT_THUMB     = 0x0040;
    private const ushort XINPUT_GAMEPAD_RIGHT_THUMB    = 0x0080;
    private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER  = 0x0100;
    private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
    private const ushort XINPUT_GAMEPAD_A              = 0x1000;
    private const ushort XINPUT_GAMEPAD_B              = 0x2000;
    private const ushort XINPUT_GAMEPAD_X              = 0x4000;
    private const ushort XINPUT_GAMEPAD_Y              = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);

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
    /// Reports the specified slot as connected if it matches the active slot or is marked connected.
    /// </summary>
    public static byte[] BuildControllerInfoResponse(
        uint serverId,
        byte slot,
        bool? isSlotConnected = null,
        byte macSuffixOffset = 0)
    {
        byte[] packet = new byte[32];
        Span<byte> span = packet;

        bool connected = isSlotConnected ?? (slot == 0);
        byte macSuffix = (byte)Math.Clamp(slot + 1 + macSuffixOffset, 1, 254);

        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), MsgTypeControllerInfoRequest);
        WriteSharedControllerInfo(span.Slice(20, 11), slot, isConnected: connected, macSuffix);
        span[31] = 0;

        FinalizeServerHeader(span, serverId);
        return packet;
    }

    /// <summary>
    /// Builds a 0x100002 (Controller Data) motion broadcast packet (100 bytes).
    /// When forwardGamepadInputs is enabled, also embeds live XInput buttons, triggers, D-Pad, and analog sticks.
    /// </summary>
    public static void BuildMotionDataPacket(
        Span<byte> packet,
        uint serverId,
        uint packetNumber,
        in MotionData motion,
        byte slot = 0,
        byte macSuffixOffset = 0,
        bool forwardGamepadInputs = false)
    {
        packet.Clear();

        byte macSuffix = (byte)Math.Clamp(slot + 1 + macSuffixOffset, 1, 254);

        // [16..19] Message Type (0x100002)
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(16, 4), MsgTypeControllerDataRequest);

        // [20..30] Shared Controller Info (11 bytes)
        WriteSharedControllerInfo(packet.Slice(20, 11), slot, isConnected: true, macSuffix);

        // [31] Is Connected (1 = Connected)
        packet[31] = 1;

        // [32..35] Packet Number (uint32 LE)
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(32, 4), packetNumber);

        // [40..43] Left and Right Analog Sticks default to Center (128)
        packet[40] = 128;
        packet[41] = 128;
        packet[42] = 128;
        packet[43] = 128;

        if (forwardGamepadInputs)
        {
            TryWriteXInputGamepadState(packet, slot);
        }

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

    private static void TryWriteXInputGamepadState(Span<byte> packet, byte slot)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            uint userIndex = Math.Min((uint)slot, 3u);
            XInputState state = default;
            bool foundState = false;

            if (XInputGetState(userIndex, out XInputState primaryState) == 0)
            {
                state = primaryState;
                foundState = true;
            }

            // If slot 0 is requested (or disconnected/idle because of a virtual pad at index 0),
            // scan XInput indices 0..3 for the first controller with active input or connection.
            if (userIndex == 0 && (!foundState || IsGamepadIdle(in state.Gamepad)))
            {
                for (uint i = 0; i < 4; i++)
                {
                    if (XInputGetState(i, out XInputState candidate) == 0)
                    {
                        if (!foundState)
                        {
                            state = candidate;
                            foundState = true;
                        }

                        if (!IsGamepadIdle(in candidate.Gamepad))
                        {
                            state = candidate;
                            foundState = true;
                            break;
                        }
                    }
                }
            }

            if (!foundState)
                return;

            ushort btns = state.Gamepad.wButtons;
            byte l2 = state.Gamepad.bLeftTrigger;
            byte r2 = state.Gamepad.bRightTrigger;

            byte states1 = 0;
            if ((btns & XINPUT_GAMEPAD_BACK) != 0)        states1 |= 0x01; // Share
            if ((btns & XINPUT_GAMEPAD_LEFT_THUMB) != 0)  states1 |= 0x02; // L3
            if ((btns & XINPUT_GAMEPAD_RIGHT_THUMB) != 0) states1 |= 0x04; // R3
            if ((btns & XINPUT_GAMEPAD_START) != 0)       states1 |= 0x08; // Options
            if ((btns & XINPUT_GAMEPAD_DPAD_UP) != 0)     states1 |= 0x10; // D-Pad Up
            if ((btns & XINPUT_GAMEPAD_DPAD_RIGHT) != 0)  states1 |= 0x20; // D-Pad Right
            if ((btns & XINPUT_GAMEPAD_DPAD_DOWN) != 0)   states1 |= 0x40; // D-Pad Down
            if ((btns & XINPUT_GAMEPAD_DPAD_LEFT) != 0)   states1 |= 0x80; // D-Pad Left

            byte states2 = 0;
            if (l2 > 30)                                      states2 |= 0x01; // L2 Digital
            if (r2 > 30)                                      states2 |= 0x02; // R2 Digital
            if ((btns & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0)   states2 |= 0x04; // L1
            if ((btns & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0)  states2 |= 0x08; // R1
            if ((btns & XINPUT_GAMEPAD_Y) != 0)               states2 |= 0x10; // Triangle / Y
            if ((btns & XINPUT_GAMEPAD_B) != 0)               states2 |= 0x20; // Circle / B
            if ((btns & XINPUT_GAMEPAD_A) != 0)               states2 |= 0x40; // Cross / A
            if ((btns & XINPUT_GAMEPAD_X) != 0)               states2 |= 0x80; // Square / X

            packet[36] = states1;
            packet[37] = states2;

            // [40..43] Left & Right Analog Sticks (0..255, 128 center)
            packet[40] = (byte)Math.Clamp((state.Gamepad.sThumbLX + 32768) >> 8, 0, 255);
            packet[41] = (byte)Math.Clamp((state.Gamepad.sThumbLY + 32768) >> 8, 0, 255);
            packet[42] = (byte)Math.Clamp((state.Gamepad.sThumbRX + 32768) >> 8, 0, 255);
            packet[43] = (byte)Math.Clamp((state.Gamepad.sThumbRY + 32768) >> 8, 0, 255);

            // [44..47] Analog D-Pad (Left, Down, Right, Up)
            packet[44] = (btns & XINPUT_GAMEPAD_DPAD_LEFT) != 0 ? (byte)255 : (byte)0;
            packet[45] = (btns & XINPUT_GAMEPAD_DPAD_DOWN) != 0 ? (byte)255 : (byte)0;
            packet[46] = (btns & XINPUT_GAMEPAD_DPAD_RIGHT) != 0 ? (byte)255 : (byte)0;
            packet[47] = (btns & XINPUT_GAMEPAD_DPAD_UP) != 0 ? (byte)255 : (byte)0;

            // [48..51] Analog Face Buttons (Square/X, Cross/A, Circle/B, Triangle/Y)
            packet[48] = (btns & XINPUT_GAMEPAD_X) != 0 ? (byte)255 : (byte)0;
            packet[49] = (btns & XINPUT_GAMEPAD_A) != 0 ? (byte)255 : (byte)0;
            packet[50] = (btns & XINPUT_GAMEPAD_B) != 0 ? (byte)255 : (byte)0;
            packet[51] = (btns & XINPUT_GAMEPAD_Y) != 0 ? (byte)255 : (byte)0;

            // [52..53] Analog Shoulders (R1, L1)
            packet[52] = (btns & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0 ? (byte)255 : (byte)0;
            packet[53] = (btns & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0 ? (byte)255 : (byte)0;

            // [54..55] Analog Triggers (R2, L2)
            packet[54] = r2;
            packet[55] = l2;
        }
        catch
        {
            // Ignore XInput DLL unavailability
        }
    }

    private static bool IsGamepadIdle(in XInputGamepad pad)
    {
        const int stickDeadzone = 4000;
        return pad.wButtons == 0 &&
               pad.bLeftTrigger <= 10 &&
               pad.bRightTrigger <= 10 &&
               Math.Abs((int)pad.sThumbLX) < stickDeadzone &&
               Math.Abs((int)pad.sThumbLY) < stickDeadzone &&
               Math.Abs((int)pad.sThumbRX) < stickDeadzone &&
               Math.Abs((int)pad.sThumbRY) < stickDeadzone;
    }

    private static void WriteSharedControllerInfo(Span<byte> infoSpan, byte slot, bool isConnected, byte macSuffix)
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
        infoSpan[9] = macSuffix; // Unique virtual MAC per slot/cluster
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
