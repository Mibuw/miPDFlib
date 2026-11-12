namespace MiPdf.Fonts.Programs;

/// <summary>The 258 standard Macintosh glyph names (TrueType post table format 2).</summary>
internal static class MacGlyphNames
{
    private static readonly string[] Names = (
        ".notdef .null nonmarkingreturn space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft " +
        "parenright asterisk plus comma hyphen period slash zero one two three four five six seven eight nine colon " +
        "semicolon less equal greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft " +
        "backslash bracketright asciicircum underscore grave a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft " +
        "bar braceright asciitilde Adieresis Aring Ccedilla Eacute Ntilde Odieresis Udieresis aacute agrave acircumflex " +
        "adieresis atilde aring ccedilla eacute egrave ecircumflex edieresis iacute igrave icircumflex idieresis ntilde " +
        "oacute ograve ocircumflex odieresis otilde uacute ugrave ucircumflex udieresis dagger degree cent sterling section " +
        "bullet paragraph germandbls registered copyright trademark acute dieresis notequal AE Oslash infinity plusminus " +
        "lessequal greaterequal yen mu partialdiff summation product pi integral ordfeminine ordmasculine Omega ae oslash " +
        "questiondown exclamdown logicalnot radical florin approxequal Delta guillemotleft guillemotright ellipsis " +
        "nonbreakingspace Agrave Atilde Otilde OE oe endash emdash quotedblleft quotedblright quoteleft quoteright divide " +
        "lozenge ydieresis Ydieresis fraction currency guilsinglleft guilsinglright fi fl daggerdbl periodcentered " +
        "quotesinglbase quotedblbase perthousand Acircumflex Ecircumflex Aacute Edieresis Egrave Iacute Icircumflex " +
        "Idieresis Igrave Oacute Ocircumflex apple Ograve Uacute Ucircumflex Ugrave dotlessi circumflex tilde macron breve " +
        "dotaccent ring cedilla hungarumlaut ogonek caron Lslash lslash Scaron scaron Zcaron zcaron brokenbar Eth eth " +
        "Yacute yacute Thorn thorn minus multiply onesuperior twosuperior threesuperior onehalf onequarter threequarters " +
        "franc Gbreve gbreve Idotaccent Scedilla scedilla Cacute cacute Ccaron ccaron dcroat").Split(' ');

    public static string? Get(int index) => index >= 0 && index < Names.Length ? Names[index] : null;
}

/// <summary>The 391 predefined strings of CFF fonts (Compact Font Format, Appendix A) and the standard encoding.</summary>
internal static class CffStandardStrings
{
    private static readonly string[] Strings = (
        ".notdef space exclam quotedbl numbersign dollar percent ampersand quoteright parenleft parenright asterisk plus " +
        "comma hyphen period slash zero one two three four five six seven eight nine colon semicolon less equal greater " +
        "question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright asciicircum " +
        "underscore quoteleft a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft bar braceright asciitilde " +
        "exclamdown cent sterling fraction yen florin section currency quotesingle quotedblleft guillemotleft " +
        "guilsinglleft guilsinglright fi fl endash dagger daggerdbl periodcentered paragraph bullet quotesinglbase " +
        "quotedblbase quotedblright guillemotright ellipsis perthousand questiondown grave acute circumflex tilde macron " +
        "breve dotaccent dieresis ring cedilla hungarumlaut ogonek caron emdash AE ordfeminine Lslash Oslash OE " +
        "ordmasculine ae dotlessi lslash oslash oe germandbls onesuperior logicalnot mu trademark Eth onehalf plusminus " +
        "Thorn onequarter divide brokenbar degree thorn threequarters twosuperior registered minus eth multiply " +
        "threesuperior copyright Aacute Acircumflex Adieresis Agrave Aring Atilde Ccedilla Eacute Ecircumflex Edieresis " +
        "Egrave Iacute Icircumflex Idieresis Igrave Ntilde Oacute Ocircumflex Odieresis Ograve Otilde Scaron Uacute " +
        "Ucircumflex Udieresis Ugrave Yacute Ydieresis Zcaron aacute acircumflex adieresis agrave aring atilde ccedilla " +
        "eacute ecircumflex edieresis egrave iacute icircumflex idieresis igrave ntilde oacute ocircumflex odieresis " +
        "ograve otilde scaron uacute ucircumflex udieresis ugrave yacute ydieresis zcaron exclamsmall Hungarumlautsmall " +
        "dollaroldstyle dollarsuperior ampersandsmall Acutesmall parenleftsuperior parenrightsuperior twodotenleader " +
        "onedotenleader zerooldstyle oneoldstyle twooldstyle threeoldstyle fouroldstyle fiveoldstyle sixoldstyle " +
        "sevenoldstyle eightoldstyle nineoldstyle commasuperior threequartersemdash periodsuperior questionsmall asuperior " +
        "bsuperior centsuperior dsuperior esuperior isuperior lsuperior msuperior nsuperior osuperior rsuperior ssuperior " +
        "tsuperior ff ffi ffl parenleftinferior parenrightinferior Circumflexsmall hyphensuperior Gravesmall Asmall Bsmall " +
        "Csmall Dsmall Esmall Fsmall Gsmall Hsmall Ismall Jsmall Ksmall Lsmall Msmall Nsmall Osmall Psmall Qsmall Rsmall " +
        "Ssmall Tsmall Usmall Vsmall Wsmall Xsmall Ysmall Zsmall colonmonetary onefitted rupiah Tildesmall exclamdownsmall " +
        "centoldstyle Lslashsmall Scaronsmall Zcaronsmall Dieresissmall Brevesmall Caronsmall Dotaccentsmall Macronsmall " +
        "figuredash hypheninferior Ogoneksmall Ringsmall Cedillasmall questiondownsmall oneeighth threeeighths fiveeighths " +
        "seveneighths onethird twothirds zerosuperior foursuperior fivesuperior sixsuperior sevensuperior eightsuperior " +
        "ninesuperior zeroinferior oneinferior twoinferior threeinferior fourinferior fiveinferior sixinferior " +
        "seveninferior eightinferior nineinferior centinferior dollarinferior periodinferior commainferior Agravesmall " +
        "Aacutesmall Acircumflexsmall Atildesmall Adieresissmall Aringsmall AEsmall Ccedillasmall Egravesmall Eacutesmall " +
        "Ecircumflexsmall Edieresissmall Igravesmall Iacutesmall Icircumflexsmall Idieresissmall Ethsmall Ntildesmall " +
        "Ogravesmall Oacutesmall Ocircumflexsmall Otildesmall Odieresissmall OEsmall Oslashsmall Ugravesmall Uacutesmall " +
        "Ucircumflexsmall Udieresissmall Yacutesmall Thornsmall Ydieresissmall 001.000 001.001 001.002 001.003 Black Bold " +
        "Book Light Medium Regular Roman Semibold").Split(' ');

    public const int Count = 391;

    public static string? Get(int sid) => sid >= 0 && sid < Strings.Length ? Strings[sid] : null;

    // codes 161-251 of the standard encoding, in the order of the strings 96-149
    private static readonly int[] HighCodes =
    {
        161, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172, 173, 174, 175, 177, 178, 179, 180, 182, 183, 184, 185,
        186, 187, 188, 189, 191, 193, 194, 195, 196, 197, 198, 199, 200, 202, 203, 205, 206, 207, 208, 225, 227, 232, 233,
        234, 235, 241, 245, 248, 249, 250, 251,
    };

    /// <summary>Adobe StandardEncoding: the glyph name of a code, or null.</summary>
    public static string? StandardEncoding(int code)
    {
        if (code is >= 32 and <= 126) return Get(code - 31);
        int index = Array.IndexOf(HighCodes, code);
        return index >= 0 ? Get(96 + index) : null;
    }
}
