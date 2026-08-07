// Minimal RFC 8032 Ed25519 implementation (public domain style) used by the
// MyPowerTools OTA pipeline for feed signing and verification.
//
// Compile at runtime from PowerShell:
//   Add-Type -Path <this-file>
//   [Mpt.Ed25519]::Sign(data, privateKeySeed)      -> 64-byte signature
//   [Mpt.Ed25519]::Verify(data, signature, pubKey) -> bool

using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Mpt
{
    public static class Ed25519
    {
        private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        private static readonly BigInteger L =
            BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        private static readonly BigInteger D;
        private static readonly BigInteger D2;
        private static readonly BigInteger SqrtM1;

        private struct Point
        {
            public BigInteger X, Y, Z, T;

            public Point(BigInteger x, BigInteger y, BigInteger z, BigInteger t)
            {
                X = x; Y = y; Z = z; T = t;
            }
        }

        private static readonly Point Identity = new Point(0, 1, 1, 0);
        private static readonly Point BasePoint;

        static Ed25519()
        {
            D = ((-121665 * ModInverse(121666)) % P + P) % P;
            D2 = (2 * D) % P;
            SqrtM1 = ModPow(new BigInteger(2), (P - 1) / 4, P);
            var baseY = (4 * ModInverse(5)) % P;
            var baseX = RecoverX(baseY, 0);
            BasePoint = new Point(baseX, baseY, 1, (baseX * baseY) % P);
        }

        private static BigInteger ModPow(BigInteger value, BigInteger exponent, BigInteger modulus)
        {
            var result = BigInteger.One;
            value %= modulus;
            if (value.Sign < 0) value += modulus;
            while (exponent > BigInteger.Zero)
            {
                if ((exponent & BigInteger.One) == BigInteger.One)
                {
                    result = (result * value) % modulus;
                }
                value = (value * value) % modulus;
                exponent >>= 1;
            }
            return result;
        }

        private static BigInteger ModInverse(BigInteger value)
        {
            return ModPow(value, P - 2, P);
        }

        private static BigInteger Mod(BigInteger value, BigInteger modulus)
        {
            var result = value % modulus;
            if (result.Sign < 0) result += modulus;
            return result;
        }

        private static byte[] Sha512(byte[] value)
        {
            using (var sha = SHA512.Create())
            {
                return sha.ComputeHash(value);
            }
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }

        private static byte[] Concat3(byte[] first, byte[] second, byte[] third)
        {
            var result = new byte[first.Length + second.Length + third.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            Buffer.BlockCopy(third, 0, result, first.Length + second.Length, third.Length);
            return result;
        }

        private static BigInteger FromLittleEndian(byte[] bytes)
        {
            var value = new BigInteger(bytes);
            if (value.Sign < 0)
            {
                value += BigInteger.One << (8 * bytes.Length);
            }
            return value;
        }

        private static byte[] ToLittleEndian(BigInteger value, int length)
        {
            if (value.Sign < 0) throw new ArgumentException("negative scalar");
            var bytes = value.ToByteArray();
            if (bytes.Length > length)
            {
                if (bytes.Length == length + 1 && bytes[bytes.Length - 1] == 0)
                {
                    Array.Resize(ref bytes, length);
                }
                else
                {
                    throw new ArgumentException("value does not fit");
                }
            }
            var result = new byte[length];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        private static BigInteger RecoverX(BigInteger y, int sign)
        {
            var y2 = (y * y) % P;
            var numerator = (y2 - 1 + P) % P;
            var denominator = (D * y2 + 1) % P;
            var x2 = (numerator * ModInverse(denominator)) % P;
            var x = ModPow(x2, (P + 3) / 8, P);
            if (((x * x) % P) != x2)
            {
                x = (x * SqrtM1) % P;
            }
            if (((x * x) % P) != x2)
            {
                throw new ArgumentException("not a valid point encoding");
            }
            if (((int)(x & BigInteger.One)) != sign)
            {
                x = P - x;
            }
            return x;
        }

        private static byte[] EncodePoint(Point point)
        {
            var zInv = ModInverse(Mod(point.Z, P));
            var x = (point.X * zInv) % P;
            var y = (point.Y * zInv) % P;
            var bytes = ToLittleEndian(y, 32);
            if (((int)(x & BigInteger.One)) == 1)
            {
                bytes[31] |= 0x80;
            }
            return bytes;
        }

        private static Point DecodePoint(byte[] encoded)
        {
            if (encoded.Length != 32) throw new ArgumentException("point length");
            var copy = (byte[])encoded.Clone();
            var sign = (copy[31] >> 7) & 1;
            copy[31] &= 0x7f;
            var y = FromLittleEndian(copy);
            if (y >= P) throw new ArgumentException("point y out of range");
            var x = RecoverX(y, sign);
            return new Point(x, y, 1, (x * y) % P);
        }

        private static Point PointDouble(Point p)
        {
            var a = (p.X * p.X) % P;
            var b = (p.Y * p.Y) % P;
            var c = (2 * p.Z * p.Z) % P;
            var d = (P - a) % P;
            var e = Mod((p.X + p.Y) * (p.X + p.Y) - a - b, P);
            var g = (d + b) % P;
            var f = (g - c + P) % P;
            var h = (d - b + P) % P;
            return new Point(
                (e * f) % P,
                (g * h) % P,
                (f * g) % P,
                (e * h) % P);
        }

        private static Point PointAdd(Point p1, Point p2)
        {
            var a = Mod((p1.Y - p1.X) * (p2.Y - p2.X), P);
            var b = Mod((p1.Y + p1.X) * (p2.Y + p2.X), P);
            var c = Mod(p1.T * D2 * p2.T, P);
            var d = Mod(2 * p1.Z * p2.Z, P);
            var e = (b - a + P) % P;
            var f = (d - c + P) % P;
            var g = (d + c) % P;
            var h = (b + a) % P;
            return new Point(
                (e * f) % P,
                (g * h) % P,
                (f * g) % P,
                (e * h) % P);
        }

        private static Point ScalarMult(BigInteger scalar, Point point)
        {
            var result = Identity;
            var addend = point;
            var current = scalar;
            while (current > BigInteger.Zero)
            {
                if ((current & BigInteger.One) == BigInteger.One)
                {
                    result = PointAdd(result, addend);
                }
                addend = PointDouble(addend);
                current >>= 1;
            }
            return result;
        }

        private static BigInteger HashToScalar(byte[] value)
        {
            return FromLittleEndian(Sha512(value)) % L;
        }

        private static BigInteger DecodeScalar(byte[] bytes)
        {
            if (bytes.Length != 32) throw new ArgumentException("scalar length");
            return FromLittleEndian(bytes);
        }

        private static byte[] Clamp(byte[] hash)
        {
            var a = new byte[32];
            Buffer.BlockCopy(hash, 0, a, 0, 32);
            a[0] &= 248;
            a[31] &= 127;
            a[31] |= 64;
            return a;
        }

        public static byte[] GeneratePrivateKey()
        {
            var seed = new byte[32];
            RandomNumberGenerator.Fill(seed);
            return seed;
        }

        public static byte[] PublicKeyFromPrivate(byte[] privateKey)
        {
            if (privateKey == null || privateKey.Length != 32)
                throw new ArgumentException("private key must be 32 bytes");
            var hash = Sha512(privateKey);
            var a = DecodeScalar(Clamp(hash));
            return EncodePoint(ScalarMult(a, BasePoint));
        }

        public static byte[] Sign(byte[] message, byte[] privateKey)
        {
            if (privateKey == null || privateKey.Length != 32)
                throw new ArgumentException("private key must be 32 bytes");
            var hash = Sha512(privateKey);
            var a = DecodeScalar(Clamp(hash));
            var prefix = new byte[32];
            Buffer.BlockCopy(hash, 32, prefix, 0, 32);
            var publicKey = PublicKeyFromPrivate(privateKey);

            var r = HashToScalar(Concat(prefix, message));
            var rPoint = EncodePoint(ScalarMult(r, BasePoint));
            var k = HashToScalar(Concat3(rPoint, publicKey, message));
            var s = Mod(r + k * a, L);

            var signature = new byte[64];
            Buffer.BlockCopy(rPoint, 0, signature, 0, 32);
            var sBytes = ToLittleEndian(s, 32);
            Buffer.BlockCopy(sBytes, 0, signature, 32, 32);
            return signature;
        }

        public static bool Verify(byte[] message, byte[] signature, byte[] publicKey)
        {
            if (signature == null || signature.Length != 64)
                return false;
            if (publicKey == null || publicKey.Length != 32)
                return false;

            Point a;
            Point r;
            try
            {
                a = DecodePoint(publicKey);
                var rBytes = new byte[32];
                Buffer.BlockCopy(signature, 0, rBytes, 0, 32);
                r = DecodePoint(rBytes);
            }
            catch (ArgumentException)
            {
                return false;
            }

            var sBytes = new byte[32];
            Buffer.BlockCopy(signature, 32, sBytes, 0, 32);
            var s = DecodeScalar(sBytes);
            if (s >= L) return false;

            var rEncoded = new byte[32];
            Buffer.BlockCopy(signature, 0, rEncoded, 0, 32);
            var k = HashToScalar(Concat3(rEncoded, publicKey, message));
            var sB = ScalarMult(s, BasePoint);
            var kA = ScalarMult(k, a);
            var rPlusKA = PointAdd(r, kA);

            // RFC 8032 cofactored group check: [8]sB == [8]R + [8]kA
            var left = PointDouble(PointDouble(PointDouble(sB)));
            var right = PointDouble(PointDouble(PointDouble(rPlusKA)));
            var leftBytes = EncodePoint(left);
            var rightBytes = EncodePoint(right);
            for (int i = 0; i < 32; i++)
            {
                if (leftBytes[i] != rightBytes[i]) return false;
            }
            return true;
        }

    }
}
