using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf.Security;

/// <summary>
/// Standard security handler (ISO 32000-2, 7.6.4): password-based RC4/AES encryption, revisions 2–6.
/// </summary>
internal sealed class StandardSecurityHandler : IObjectDecryptor, IObjectEncryptor
{
    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private enum Cipher
    {
        None,
        Rc4,
        Aes128,
        Aes256,
    }

    private readonly byte[] _key;
    private readonly Cipher _streamCipher;
    private readonly Cipher _stringCipher;
    private readonly bool _encryptMetadata;

    private StandardSecurityHandler(byte[] key, Cipher streamCipher, Cipher stringCipher, bool encryptMetadata, int revision, int permissions, bool ownerAccess)
    {
        _key = key;
        _streamCipher = streamCipher;
        _stringCipher = stringCipher;
        _encryptMetadata = encryptMetadata;
        Revision = revision;
        Permissions = permissions;
        IsOwnerAccess = ownerAccess;
    }

    public int Revision { get; }

    /// <summary>The raw /P value.</summary>
    public int Permissions { get; }

    /// <summary>True if the owner password was supplied (or both passwords are identical/empty user access with owner rights).</summary>
    public bool IsOwnerAccess { get; }

    // ------------------------------------------------------------------ opening

    public static StandardSecurityHandler Open(PdfDictionary encrypt, PdfArray? id, string? password)
    {
        var filter = encrypt.GetName(PdfName.Filter)?.Value;
        if (filter != "Standard")
            throw new PdfException($"Unsupported security handler '{filter}' (only password-based encryption is supported).");
        int v = encrypt.GetInt(PdfName.V);
        int r = encrypt.GetInt(PdfName.Get("R"));
        int p = encrypt.GetInt(PdfName.P);
        bool encryptMetadata = encrypt.GetBool(PdfName.EncryptMetadata, true);
        byte[] o = encrypt.GetString(PdfName.Get("O"))?.Bytes ?? throw new PdfException("Encryption dictionary without /O.");
        byte[] u = encrypt.GetString(PdfName.Get("U"))?.Bytes ?? throw new PdfException("Encryption dictionary without /U.");
        byte[] id0 = id?.GetString(0)?.Bytes ?? Array.Empty<byte>();
        var pwd = password ?? "";

        Cipher stmCipher, strCipher;
        if (v >= 4)
        {
            stmCipher = CipherFromFilter(encrypt, encrypt.GetName(PdfName.StmF)?.Value ?? "Identity");
            strCipher = CipherFromFilter(encrypt, encrypt.GetName(PdfName.StrF)?.Value ?? "Identity");
        }
        else stmCipher = strCipher = Cipher.Rc4;

        if (r >= 5)
        {
            byte[] oe = encrypt.GetString(PdfName.Get("OE"))?.Bytes ?? throw new PdfException("Missing /OE.");
            byte[] ue = encrypt.GetString(PdfName.Get("UE"))?.Bytes ?? throw new PdfException("Missing /UE.");
            byte[] pw = Utf8Password(pwd);
            byte[] uData = u.AsSpan(0, Math.Min(48, u.Length)).ToArray();
            if (o.Length >= 48 && Hash(r, pw, o.AsSpan(32, 8), uData).AsSpan().SequenceEqual(o.AsSpan(0, 32)))
            {
                var key = AesNoPadding(Hash(r, pw, o.AsSpan(40, 8), uData), oe, encrypt: false);
                return new StandardSecurityHandler(key, stmCipher, strCipher, encryptMetadata, r, p, ownerAccess: true);
            }
            if (u.Length >= 48 && Hash(r, pw, u.AsSpan(32, 8), ReadOnlySpan<byte>.Empty).AsSpan().SequenceEqual(u.AsSpan(0, 32)))
            {
                var key = AesNoPadding(Hash(r, pw, u.AsSpan(40, 8), ReadOnlySpan<byte>.Empty), ue, encrypt: false);
                return new StandardSecurityHandler(key, stmCipher, strCipher, encryptMetadata, r, p, ownerAccess: false);
            }
            throw new PdfPasswordException(password == null ? "The document is password protected." : "Invalid password.");
        }

        int length = r == 2 ? 5 : Math.Clamp(encrypt.GetInt(PdfName.Length, 40) / 8, 5, 16);
        if (v >= 4 && stmCipher == Cipher.Aes128) length = 16;
        var passwordBytes = Latin1Password(pwd);
        // owner password: recover the user password from /O
        var ownerKey = OwnerRc4Key(passwordBytes, r, length);
        byte[] userFromOwner = o.AsSpan(0, Math.Min(32, o.Length)).ToArray();
        if (r == 2) userFromOwner = Rc4.Apply(ownerKey, userFromOwner);
        else
            for (int i = 19; i >= 0; i--) userFromOwner = Rc4.Apply(XorKey(ownerKey, i), userFromOwner);
        var keyFromOwner = ComputeKey(userFromOwner, o, p, id0, r, length, encryptMetadata);
        if (CheckUserKey(keyFromOwner, u, id0, r))
            return new StandardSecurityHandler(keyFromOwner, stmCipher, strCipher, encryptMetadata, r, p, ownerAccess: true);
        var keyFromUser = ComputeKey(passwordBytes, o, p, id0, r, length, encryptMetadata);
        if (CheckUserKey(keyFromUser, u, id0, r))
            return new StandardSecurityHandler(keyFromUser, stmCipher, strCipher, encryptMetadata, r, p, ownerAccess: false);
        throw new PdfPasswordException(password == null ? "The document is password protected." : "Invalid password.");
    }

    private static Cipher CipherFromFilter(PdfDictionary encrypt, string name)
    {
        if (name == "Identity") return Cipher.None;
        var cfm = encrypt.GetDictionary(PdfName.CF)?.GetDictionary(PdfName.Get(name))?.GetName(PdfName.CFM)?.Value;
        return cfm switch
        {
            "AESV2" => Cipher.Aes128,
            "AESV3" => Cipher.Aes256,
            "V2" => Cipher.Rc4,
            "None" => Cipher.None,
            _ => Cipher.Rc4,
        };
    }

    // ------------------------------------------------------------------ creating

    /// <summary>Creates a handler for new encryption and the corresponding /Encrypt dictionary.</summary>
    public static (StandardSecurityHandler Handler, PdfDictionary Dictionary) Create(PdfEncryptionOptions options, byte[] id0)
    {
        int p = unchecked((int)0xFFFFF0C0) | (int)options.Permissions;
        string owner = string.IsNullOrEmpty(options.OwnerPassword) ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) : options.OwnerPassword;
        var dict = new PdfDictionary();
        dict[PdfName.Filter] = PdfName.Get("Standard");
        dict.Set(PdfName.P, p);
        if (!options.EncryptMetadata) dict.Set(PdfName.EncryptMetadata, false);

        if (options.Algorithm == PdfEncryptionAlgorithm.Aes256)
        {
            const int r = 6;
            var key = RandomNumberGenerator.GetBytes(32);
            byte[] user = Utf8Password(options.UserPassword), ownerPw = Utf8Password(owner);
            var uvs = RandomNumberGenerator.GetBytes(8);
            var uks = RandomNumberGenerator.GetBytes(8);
            byte[] u = [.. Hash(r, user, uvs, ReadOnlySpan<byte>.Empty), .. uvs, .. uks];
            var ue = AesNoPadding(Hash(r, user, uks, ReadOnlySpan<byte>.Empty), key, encrypt: true);
            var ovs = RandomNumberGenerator.GetBytes(8);
            var oks = RandomNumberGenerator.GetBytes(8);
            byte[] o = [.. Hash(r, ownerPw, ovs, u), .. ovs, .. oks];
            var oe = AesNoPadding(Hash(r, ownerPw, oks, u), key, encrypt: true);
            var perms = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(perms, p);
            perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
            perms[8] = (byte)(options.EncryptMetadata ? 'T' : 'F');
            perms[9] = (byte)'a';
            perms[10] = (byte)'d';
            perms[11] = (byte)'b';
            RandomNumberGenerator.GetBytes(4).CopyTo(perms, 12);
            using var aes = Aes.Create();
            aes.Key = key;
            var permsEnc = aes.EncryptEcb(perms, PaddingMode.None);

            dict.Set(PdfName.V, 5);
            dict.Set(PdfName.Get("R"), r);
            dict.Set(PdfName.Length, 256);
            dict[PdfName.Get("O")] = new PdfString(o, true);
            dict[PdfName.Get("U")] = new PdfString(u, true);
            dict[PdfName.Get("OE")] = new PdfString(oe, true);
            dict[PdfName.Get("UE")] = new PdfString(ue, true);
            dict[PdfName.Perms] = new PdfString(permsEnc, true);
            AddCryptFilter(dict, "AESV3", 32);
            return (new StandardSecurityHandler(key, Cipher.Aes256, Cipher.Aes256, options.EncryptMetadata, r, p, true), dict);
        }

        {
            bool aes = options.Algorithm == PdfEncryptionAlgorithm.Aes128;
            int r = aes ? 4 : 3;
            const int length = 16;
            var ownerBytes = Latin1Password(owner);
            var userBytes = Latin1Password(options.UserPassword);
            // Algorithm 3: /O
            var ownerKey = OwnerRc4Key(ownerBytes, r, length);
            var o = Rc4.Apply(ownerKey, Pad(userBytes));
            for (int i = 1; i <= 19; i++) o = Rc4.Apply(XorKey(ownerKey, i), o);
            var key = ComputeKey(userBytes, o, p, id0, r, length, options.EncryptMetadata);
            // Algorithm 5: /U
            var hash = MD5.HashData([.. Padding, .. id0]);
            var u = Rc4.Apply(key, hash);
            for (int i = 1; i <= 19; i++) u = Rc4.Apply(XorKey(key, i), u);
            u = [.. u, .. new byte[16]];

            dict.Set(PdfName.V, aes ? 4 : 2);
            dict.Set(PdfName.Get("R"), r);
            dict.Set(PdfName.Length, 128);
            dict[PdfName.Get("O")] = new PdfString(o, true);
            dict[PdfName.Get("U")] = new PdfString(u, true);
            if (aes) AddCryptFilter(dict, "AESV2", 16);
            var cipher = aes ? Cipher.Aes128 : Cipher.Rc4;
            return (new StandardSecurityHandler(key, cipher, cipher, options.EncryptMetadata, r, p, true), dict);
        }
    }

    private static void AddCryptFilter(PdfDictionary dict, string cfm, int length)
    {
        var stdCf = new PdfDictionary();
        stdCf[PdfName.CFM] = PdfName.Get(cfm);
        stdCf[PdfName.Get("AuthEvent")] = PdfName.Get("DocOpen");
        stdCf.Set(PdfName.Length, length);
        dict[PdfName.CF] = new PdfDictionary { [PdfName.StdCF] = stdCf };
        dict[PdfName.StmF] = PdfName.StdCF;
        dict[PdfName.StrF] = PdfName.StdCF;
    }

    // ------------------------------------------------------------------ key algorithms

    private static byte[] Pad(byte[] password)
    {
        var result = new byte[32];
        int n = Math.Min(32, password.Length);
        password.AsSpan(0, n).CopyTo(result);
        Padding.AsSpan(0, 32 - n).CopyTo(result.AsSpan(n));
        return result;
    }

    private static byte[] Latin1Password(string password) => Encoding.Latin1.GetBytes(password);

    private static byte[] Utf8Password(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        return bytes.Length > 127 ? bytes[..127] : bytes;
    }

    /// <summary>Algorithm 2: file encryption key for revisions 2–4.</summary>
    private static byte[] ComputeKey(byte[] password, byte[] o, int p, byte[] id0, int r, int length, bool encryptMetadata)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(Pad(password));
        md5.AppendData(o.AsSpan(0, Math.Min(32, o.Length)));
        Span<byte> pBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pBytes, p);
        md5.AppendData(pBytes);
        md5.AppendData(id0);
        if (r >= 4 && !encryptMetadata) md5.AppendData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        var hash = md5.GetHashAndReset();
        if (r >= 3)
            for (int i = 0; i < 50; i++) hash = MD5.HashData(hash.AsSpan(0, length));
        return hash.AsSpan(0, length).ToArray();
    }

    private static byte[] OwnerRc4Key(byte[] ownerPassword, int r, int length)
    {
        var hash = MD5.HashData(Pad(ownerPassword));
        if (r >= 3)
            for (int i = 0; i < 50; i++) hash = MD5.HashData(hash);
        return hash.AsSpan(0, r == 2 ? 5 : length).ToArray();
    }

    /// <summary>Algorithms 4 and 5: checks a file key against /U.</summary>
    private static bool CheckUserKey(byte[] key, byte[] u, byte[] id0, int r)
    {
        if (r == 2) return Rc4.Apply(key, Padding).AsSpan().SequenceEqual(u.AsSpan(0, Math.Min(32, u.Length)));
        var value = Rc4.Apply(key, MD5.HashData([.. Padding, .. id0]));
        for (int i = 1; i <= 19; i++) value = Rc4.Apply(XorKey(key, i), value);
        return u.Length >= 16 && value.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
    }

    private static byte[] XorKey(byte[] key, int value)
    {
        var result = new byte[key.Length];
        for (int i = 0; i < key.Length; i++) result[i] = (byte)(key[i] ^ value);
        return result;
    }

    /// <summary>Password hash for revision 5 (SHA-256) and 6 (Algorithm 2.B).</summary>
    internal static byte[] Hash(int revision, byte[] password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userKey)
    {
        byte[] k = SHA256.HashData([.. password, .. salt, .. userKey]);
        if (revision < 6) return k;
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        int round = 0;
        while (true)
        {
            var block = new byte[password.Length + k.Length + userKey.Length];
            password.CopyTo(block, 0);
            k.CopyTo(block, password.Length);
            userKey.CopyTo(block.AsSpan(password.Length + k.Length));
            var k1 = new byte[block.Length * 64];
            for (int i = 0; i < 64; i++) block.CopyTo(k1, i * block.Length);
            aes.Key = k.AsSpan(0, 16).ToArray();
            var e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            int mod = 0;
            for (int i = 0; i < 16; i++) mod += e[i];
            k = (mod % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            round++;
            if (round >= 64 && e[^1] <= round - 32) break;
        }
        return k.AsSpan(0, 32).ToArray();
    }

    private static byte[] AesNoPadding(byte[] key, byte[] data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = new byte[16];
        return encrypt ? aes.EncryptCbc(data, iv, PaddingMode.None) : aes.DecryptCbc(data.AsSpan(0, data.Length / 16 * 16), iv, PaddingMode.None);
    }

    // ------------------------------------------------------------------ object encryption

    private byte[] ObjectKey(int number, int generation, Cipher cipher)
    {
        if (cipher == Cipher.Aes256) return _key;
        var input = new byte[_key.Length + 5 + (cipher == Cipher.Aes128 ? 4 : 0)];
        _key.CopyTo(input, 0);
        input[_key.Length] = (byte)number;
        input[_key.Length + 1] = (byte)(number >> 8);
        input[_key.Length + 2] = (byte)(number >> 16);
        input[_key.Length + 3] = (byte)generation;
        input[_key.Length + 4] = (byte)(generation >> 8);
        if (cipher == Cipher.Aes128) "sAlT"u8.CopyTo(input.AsSpan(_key.Length + 5));
        return MD5.HashData(input).AsSpan(0, Math.Min(_key.Length + 5, 16)).ToArray();
    }

    private byte[] Decrypt(byte[] data, int number, int generation, Cipher cipher)
    {
        if (cipher == Cipher.None || data.Length == 0) return data;
        var key = ObjectKey(number, generation, cipher);
        if (cipher == Cipher.Rc4) return Rc4.Apply(key, data);
        if (data.Length < 16) return Array.Empty<byte>();
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = data.AsSpan(0, 16);
        var body = data.AsSpan(16, (data.Length - 16) / 16 * 16);
        try
        {
            return aes.DecryptCbc(body, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return aes.DecryptCbc(body, iv, PaddingMode.None); // invalid padding: keep the data
        }
    }

    private byte[] Encrypt(byte[] data, int number, int generation, Cipher cipher)
    {
        if (cipher == Cipher.None) return data;
        var key = ObjectKey(number, generation, cipher);
        if (cipher == Cipher.Rc4) return Rc4.Apply(key, data);
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = RandomNumberGenerator.GetBytes(16);
        return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
    }

    private Cipher StreamCipherFor(PdfStream stream)
    {
        var type = stream.GetName(PdfName.Type);
        if (type == PdfName.XRef) return Cipher.None;
        if (!_encryptMetadata && type == PdfName.Metadata) return Cipher.None;
        // a /Crypt filter selects a named crypt filter; "Identity" disables encryption
        var filters = stream.GetFilters();
        if (filters.Count > 0 && filters[0] == PdfName.Crypt)
        {
            var parms = stream.Get(PdfName.DecodeParms) switch
            {
                PdfDictionary d => d,
                PdfArray a => a.GetDictionary(0),
                _ => null,
            };
            if ((parms?.GetName(PdfName.Name)?.Value ?? "Identity") == "Identity") return Cipher.None;
        }
        return _streamCipher;
    }

    public byte[] DecryptString(byte[] data, int objectNumber, int generation) => Decrypt(data, objectNumber, generation, _stringCipher);

    public byte[] DecryptStream(byte[] data, int objectNumber, int generation, PdfStream stream) =>
        Decrypt(data, objectNumber, generation, StreamCipherFor(stream));

    public byte[] EncryptString(byte[] data, int objectNumber, int generation) => Encrypt(data, objectNumber, generation, _stringCipher);

    /// <summary>Stream encryption that honors metadata/crypt filter exceptions.</summary>
    public byte[] EncryptStream(byte[] data, int objectNumber, int generation, PdfStream stream) =>
        Encrypt(data, objectNumber, generation, StreamCipherFor(stream));
}

/// <summary>RC4 stream cipher (needed for legacy PDF encryption; not provided by .NET).</summary>
internal static class Rc4
{
    public static byte[] Apply(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var result = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            result[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return result;
    }
}
