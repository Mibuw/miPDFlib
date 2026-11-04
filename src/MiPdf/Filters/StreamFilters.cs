using MiPdf.Objects;

namespace MiPdf.Filters;

/// <summary>Applies the decoding filters of a stream (ISO 32000-2, 7.4).</summary>
internal static class StreamFilters
{
    /// <summary>Filters whose output is image data that is kept encoded.</summary>
    private static readonly HashSet<string> ImageFilters = new(StringComparer.Ordinal)
    {
        "DCTDecode", "DCT", "JPXDecode", "JBIG2Decode", "CCITTFaxDecode", "CCF",
    };

    /// <summary>
    /// Returns the filter names of the <c>/Filter</c> entry of a stream dictionary in the order they are applied
    /// for decoding (a single name or an array); empty if there is no filter.
    /// </summary>
    public static IReadOnlyList<PdfName> GetFilterNames(PdfDictionary dict)
    {
        return dict.Get(PdfName.Filter) switch
        {
            PdfName n => new[] { n },
            PdfArray a => a.Select(o => o.Resolve()).OfType<PdfName>().ToArray(),
            _ => Array.Empty<PdfName>(),
        };
    }

    private static PdfDictionary? GetDecodeParms(PdfDictionary dict, int index)
    {
        var parms = dict.Get(PdfName.DecodeParms) ?? dict.Get(PdfName.DP);
        return parms switch
        {
            PdfDictionary d => index == 0 ? d : null,
            PdfArray a => a.GetDictionary(index),
            _ => null,
        };
    }

    /// <summary>
    /// Decodes <paramref name="data"/> with the filters declared in <paramref name="dict"/>.
    /// Stops before the first image filter; its name is returned in <paramref name="remainingImageFilter"/>.
    /// </summary>
    public static byte[] Decode(byte[] data, PdfDictionary dict, out PdfName? remainingImageFilter)
    {
        remainingImageFilter = null;
        var filters = GetFilterNames(dict);
        if (filters.Count > PdfLimits.Current.MaxFiltersPerStream)
            throw new PdfException($"Too many stream filters ({filters.Count}, PdfLimits.MaxFiltersPerStream).");
        for (int i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            if (ImageFilters.Contains(f.Value))
            {
                remainingImageFilter = f;
                return data;
            }
            data = DecodeOne(data, f, GetDecodeParms(dict, i));
        }
        return data;
    }

    /// <summary>
    /// Decodes <paramref name="data"/> with a single non-image filter (full or abbreviated name), including the
    /// predictor for Flate and LZW. <c>/Crypt</c> returns the data unchanged, as decryption is done by the security handler.
    /// </summary>
    /// <param name="data">The encoded data.</param>
    /// <param name="filter">The filter name, e.g. <c>/FlateDecode</c> or <c>/Fl</c>.</param>
    /// <param name="parms">The decode parameters of this filter, or <c>null</c>.</param>
    /// <exception cref="PdfException">The filter is not supported (this includes the image filters).</exception>
    public static byte[] DecodeOne(byte[] data, PdfName filter, PdfDictionary? parms)
    {
        switch (filter.Value)
        {
            case "FlateDecode":
            case "Fl":
                return Predictor.Apply(FlateFilter.Decode(data), parms);
            case "LZWDecode":
            case "LZW":
                return Predictor.Apply(LzwFilter.Decode(data, parms?.GetInt(PdfName.EarlyChange, 1) ?? 1), parms);
            case "ASCII85Decode":
            case "A85":
                return Ascii85Filter.Decode(data);
            case "ASCIIHexDecode":
            case "AHx":
                return AsciiHexFilter.Decode(data);
            case "RunLengthDecode":
            case "RL":
                return RunLengthFilter.Decode(data);
            case "Crypt":
                return data; // decryption is performed by the security handler
            default:
                throw new PdfException($"Unsupported stream filter /{filter.Value}.");
        }
    }
}
