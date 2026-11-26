using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RoomKit;

/// <summary>Makes a new ephemeral P-256 key for one encrypted message (the default is a fresh random key).</summary>
internal delegate ECDiffieHellman EphemeralKeySource();

/// <summary>The VAPID P-256 key pair: the 65-byte uncompressed public point and the 32-byte private scalar.</summary>
internal sealed record PushKeyPair(byte[] PublicKey, byte[] PrivateKey);

/// <summary>Web Push crypto: RFC 8291 <c>aes128gcm</c> payload encryption (one record) and the RFC 8292 VAPID ES256
/// JWT. The clock, the salt and the ephemeral key come from the constructor, so tests can fix them.</summary>
internal sealed class PushCrypto
{
    public const int PublicKeyLength = 65;

    public const int AuthLength = 16;

    public const int SaltLength = 16;

    public const int RecordSize = 4096;

    /// <summary><c>mailto:&lt;short name in lower case&gt;@localhost</c>.</summary>
    public static string DefaultSubject => "mailto:" + RoomContext.Options.ShortName.ToLowerInvariant() + "@localhost";

    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    private static readonly BigInteger P = BigInteger.Parse(
        "0FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF",
        System.Globalization.NumberStyles.HexNumber);

    private static readonly BigInteger B = BigInteger.Parse(
        "05AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B",
        System.Globalization.NumberStyles.HexNumber);

    private readonly TimeProvider time;

    private readonly EphemeralKeySource ephemeralKey;

    private readonly RandomFill random;

    private readonly string subject;

    public PushCrypto(TimeProvider time, EphemeralKeySource ephemeralKey, RandomFill random, string? subject)
    {
        this.time = time;
        this.ephemeralKey = ephemeralKey;
        this.random = random;
        this.subject = string.IsNullOrWhiteSpace(subject) ? DefaultSubject : subject;
    }

    /// <summary>A fresh random P-256 key.</summary>
    public static ECDiffieHellman SystemEphemeralKey()
    {
        return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>A new random VAPID key pair.</summary>
    public static PushKeyPair GenerateKeyPair()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = key.ExportParameters(includePrivateParameters: true);
        return new PushKeyPair(PublicPoint(parameters), parameters.D!);
    }

    /// <summary>True when the bytes are an uncompressed P-256 point (<c>04 || X || Y</c>) that lies on the
    /// curve.</summary>
    public static bool IsValidPublicKey(byte[]? point)
    {
        if (point is null || point.Length != PublicKeyLength || point[0] != 0x04)
        {
            return false;
        }

        BigInteger x = new(point.AsSpan(1, 32), isUnsigned: true, isBigEndian: true);
        BigInteger y = new(point.AsSpan(33, 32), isUnsigned: true, isBigEndian: true);
        if (x >= P || y >= P)
        {
            return false;
        }

        BigInteger left = BigInteger.ModPow(y, 2, P);
        BigInteger right = ((BigInteger.ModPow(x, 3, P) - (3 * x) + B) % P + P) % P;
        return left == right;
    }

    /// <summary>True when the key pair's private scalar belongs to its public point.</summary>
    public static bool IsValidKeyPair(PushKeyPair keys)
    {
        if (!IsValidPublicKey(keys.PublicKey) || keys.PrivateKey.Length != 32)
        {
            return false;
        }

        try
        {
            using ECDsa key = ImportSigningKey(keys);
            return PublicPoint(key.ExportParameters(false)).AsSpan().SequenceEqual(keys.PublicKey);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The uncompressed point of a key's public part.</summary>
    public static byte[] PublicPoint(ECParameters parameters)
    {
        byte[] point = new byte[PublicKeyLength];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);
        return point;
    }

    /// <summary>The <c>aes128gcm</c> body for one receiver, with a new ephemeral key and salt.</summary>
    public byte[] Encrypt(byte[] payload, byte[] receiverPublicKey, byte[] authSecret)
    {
        byte[] salt = new byte[SaltLength];
        random(salt);
        using ECDiffieHellman sender = ephemeralKey();
        return Encrypt(payload, receiverPublicKey, authSecret, sender, salt);
    }

    /// <summary>RFC 8291 section 3.4 and RFC 8188: header (salt, record size, key id = sender public key) and one
    /// encrypted record holding the payload and the last-record delimiter <c>0x02</c>.</summary>
    public static byte[] Encrypt(
        byte[] payload, byte[] receiverPublicKey, byte[] authSecret, ECDiffieHellman sender, byte[] salt)
    {
        if (!IsValidPublicKey(receiverPublicKey) || authSecret.Length != AuthLength || salt.Length != SaltLength)
        {
            throw new CryptographicException("invalid input");
        }

        if (payload.Length + 1 + 16 > RecordSize)
        {
            throw new CryptographicException("payload too large");
        }

        byte[] senderPublic = PublicPoint(sender.ExportParameters(false));
        using ECDiffieHellman receiver = ImportPublicKey(receiverPublicKey);
        byte[] shared = sender.DeriveRawSecretAgreement(receiver.PublicKey);
        byte[] keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), receiverPublicKey, senderPublic);
        byte[] ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo);
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        byte[] cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(ikm);
        CryptographicOperations.ZeroMemory(prk);

        byte[] plain = new byte[payload.Length + 1];
        payload.CopyTo(plain, 0);
        plain[^1] = 0x02;
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (AesGcm aes = new(cek, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        CryptographicOperations.ZeroMemory(cek);
        byte[] header = new byte[SaltLength + 4 + 1];
        salt.CopyTo(header, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), RecordSize);
        header[20] = (byte)senderPublic.Length;
        return Concat(header, senderPublic, cipher, tag);
    }

    /// <summary>The <c>Authorization</c> header value <c>vapid t=&lt;jwt&gt;, k=&lt;public key&gt;</c> for an
    /// endpoint.</summary>
    public string Authorization(Uri endpoint, PushKeyPair keys)
    {
        return "vapid t=" + Jwt(endpoint, keys) + ", k=" + Base64Url.Encode(keys.PublicKey);
    }

    /// <summary>The VAPID JWT: header <c>{"typ":"JWT","alg":"ES256"}</c>, claims <c>aud</c> (the endpoint's scheme
    /// and host), <c>exp</c> (now + 12 h) and <c>sub</c>, signed with ES256 (r || s).</summary>
    public string Jwt(Uri endpoint, PushKeyPair keys)
    {
        string header = Base64Url.Encode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        long exp = time.GetUtcNow().Add(TokenLifetime).ToUnixTimeSeconds();
        string audience = endpoint.Scheme + "://" + endpoint.IdnHost;
        using MemoryStream claims = new();
        using (System.Text.Json.Utf8JsonWriter writer = new(claims))
        {
            writer.WriteStartObject();
            writer.WriteString("aud", audience);
            writer.WriteNumber("exp", exp);
            writer.WriteString("sub", subject);
            writer.WriteEndObject();
        }

        string signingInput = header + "." + Base64Url.Encode(claims.ToArray());
        using ECDsa key = ImportSigningKey(keys);
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url.Encode(signature);
    }

    private static ECDsa ImportSigningKey(PushKeyPair keys)
    {
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = keys.PrivateKey,
            Q = new ECPoint { X = keys.PublicKey[1..33], Y = keys.PublicKey[33..] },
        });
    }

    private static ECDiffieHellman ImportPublicKey(byte[] point)
    {
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] all = new byte[parts.Sum(part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(all, offset);
            offset += part.Length;
        }

        return all;
    }
}

/// <summary>Base64url without padding (RFC 4648 section 5), strict on decode.</summary>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The bytes, or <c>null</c> when the text has characters outside base64url (padding is
    /// allowed).</summary>
    public static byte[]? Decode(string? text)
    {
        if (text is null || text.Length > 8192)
        {
            return null;
        }

        string trimmed = text.TrimEnd('=');
        foreach (char c in trimmed)
        {
            bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok)
            {
                return null;
            }
        }

        if (trimmed.Length % 4 == 1)
        {
            return null;
        }

        string padded = trimmed.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
