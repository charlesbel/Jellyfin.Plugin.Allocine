using System;
using System.Buffers.Binary;

namespace Jellyfin.Plugin.Allocine
{
    internal static class GoogleCheckinCodec
    {
        private const string AnonymousCheckinRequest = "IhVgA2oRCAISCzYzLjAuMzIzNC4wGAFwA7ABAA==";

        public static byte[] CreateRequest()
        {
            return Convert.FromBase64String(AnonymousCheckinRequest);
        }

        public static GoogleCheckinCredentials ParseResponse(ReadOnlySpan<byte> response)
        {
            ulong androidId = 0;
            ulong securityToken = 0;
            bool hasAndroidId = false;
            bool hasSecurityToken = false;
            int offset = 0;

            while (offset < response.Length)
            {
                ulong key = ReadVarint(response, ref offset);
                int fieldNumber = checked((int)(key >> 3));
                int wireType = (int)(key & 7);

                if (fieldNumber == 7 && wireType == 1)
                {
                    androidId = ReadFixed64(response, ref offset);
                    hasAndroidId = true;
                }
                else if (fieldNumber == 8 && wireType == 1)
                {
                    securityToken = ReadFixed64(response, ref offset);
                    hasSecurityToken = true;
                }
                else
                {
                    SkipField(response, ref offset, wireType);
                }
            }

            if (!hasAndroidId || !hasSecurityToken)
            {
                throw new InvalidOperationException("Google Check-in response did not contain device credentials.");
            }

            return new GoogleCheckinCredentials(androidId, securityToken);
        }

        private static ulong ReadFixed64(ReadOnlySpan<byte> data, ref int offset)
        {
            EnsureAvailable(data, offset, sizeof(ulong));
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong)));
            offset += sizeof(ulong);
            return value;
        }

        private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                EnsureAvailable(data, offset, 1);
                byte current = data[offset++];
                if (shift == 63 && current > 1)
                {
                    throw new FormatException("Protocol Buffers varint exceeds 64 bits.");
                }

                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }

            throw new FormatException("Invalid Protocol Buffers varint.");
        }

        private static void SkipField(ReadOnlySpan<byte> data, ref int offset, int wireType)
        {
            switch (wireType)
            {
                case 0:
                    _ = ReadVarint(data, ref offset);
                    break;
                case 1:
                    EnsureAvailable(data, offset, sizeof(ulong));
                    offset += sizeof(ulong);
                    break;
                case 2:
                    ulong length = ReadVarint(data, ref offset);
                    if (length > int.MaxValue)
                    {
                        throw new FormatException("Protocol Buffers field is too large.");
                    }

                    EnsureAvailable(data, offset, (int)length);
                    offset += (int)length;
                    break;
                case 5:
                    EnsureAvailable(data, offset, sizeof(uint));
                    offset += sizeof(uint);
                    break;
                default:
                    throw new FormatException("Unsupported Protocol Buffers wire type.");
            }
        }

        private static void EnsureAvailable(ReadOnlySpan<byte> data, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset > data.Length - length)
            {
                throw new FormatException("Truncated Protocol Buffers payload.");
            }
        }
    }
}
