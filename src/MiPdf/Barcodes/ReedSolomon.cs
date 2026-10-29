namespace MiPdf.Barcodes;

/// <summary>Arithmetic in GF(256) and Reed-Solomon error correction codewords (used by QR Code and Data Matrix).</summary>
internal sealed class GaloisField
{
    public static readonly GaloisField Qr = new(0x11D);
    public static readonly GaloisField DataMatrix = new(0x12D);

    private readonly byte[] _exp = new byte[512];
    private readonly int[] _log = new int[256];

    private GaloisField(int primitive)
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            _exp[i] = (byte)x;
            _log[x] = i;
            x <<= 1;
            if (x >= 256) x ^= primitive;
        }
        for (int i = 255; i < 512; i++) _exp[i] = _exp[i - 255];
    }

    public byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : _exp[_log[a] + _log[b]];

    public byte Power(int n) => _exp[n % 255];

    /// <summary>Generator polynomial (x - a^first)(x - a^(first+1))... of the given degree, highest coefficient first.</summary>
    public byte[] Generator(int degree, int firstRoot)
    {
        var g = new byte[] { 1 };
        for (int i = 0; i < degree; i++)
        {
            byte root = Power(firstRoot + i);
            var next = new byte[g.Length + 1];
            for (int j = 0; j < next.Length; j++)
            {
                byte value = j < g.Length ? g[j] : (byte)0;
                if (j > 0) value ^= Multiply(g[j - 1], root);
                next[j] = value;
            }
            g = next;
        }
        return g;
    }

    /// <summary>Computes the error correction codewords (remainder of data * x^n divided by the generator).</summary>
    public byte[] Remainder(ReadOnlySpan<byte> data, byte[] generator)
    {
        int n = generator.Length - 1;
        var ecc = new byte[n];
        foreach (byte d in data)
        {
            byte factor = (byte)(d ^ ecc[0]);
            Array.Copy(ecc, 1, ecc, 0, n - 1);
            ecc[n - 1] = 0;
            if (factor == 0) continue;
            for (int i = 0; i < n; i++) ecc[i] ^= Multiply(generator[i + 1], factor);
        }
        return ecc;
    }
}
