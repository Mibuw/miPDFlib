using MiPdf.Graphics;
using MiPdf.PostScript.Interpreter;

namespace MiPdf.PostScript.Graphics;

/// <summary>A clipping path in device space, linked to the clips it intersects (shared between graphics states).</summary>
internal sealed class PsClip
{
    public readonly PsSegment[] Path;
    public readonly bool EvenOdd;
    public readonly PsClip? Outer;

    public PsClip(PsSegment[] path, bool evenOdd, PsClip? outer)
    {
        Path = path;
        EvenOdd = evenOdd;
        Outer = outer;
    }

    public int Depth => Outer == null ? 1 : Outer.Depth + 1;
}

/// <summary>The graphics state (PLRM 4.3): device-independent parameters and the device-dependent ones kept as given.</summary>
internal sealed class PsGState
{
    public Matrix Ctm = Matrix.Identity;
    public PsPath Path = new();
    public PsClip? Clip;
    public PsColorSpace ColorSpace = PsColorSpace.Gray;
    public double[] Color = { 0 };
    public PsObject Pattern = PsObject.Null;
    public double LineWidth = 1;
    public int LineCap, LineJoin;
    public double MiterLimit = 10;
    public double[] Dash = Array.Empty<double>();
    public double DashOffset;
    public double Flatness = 1;
    public bool StrokeAdjust;
    public bool Overprint;
    public double Smoothness;
    public PsObject Font = PsObject.Null;

    /// <summary>The output device (part of the graphics state, so grestore returns from nulldevice).</summary>
    public PsDevice? Device;

    // device-dependent parameters: stored and returned, not used for PDF output
    public PsObject Transfer = PsObject.Null, BlackGeneration = PsObject.Null, UnderColorRemoval = PsObject.Null;
    public PsObject Halftone = PsObject.Null, ColorRendering = PsObject.Null, ColorTransfer = PsObject.Null;

    /// <summary>Clipping paths saved by <c>clipsave</c>.</summary>
    public List<PsClip?> ClipStack = new();

    public PsGState Clone()
    {
        var copy = (PsGState)MemberwiseClone();
        copy.Path = Path.Clone();
        copy.ClipStack = new List<PsClip?>(ClipStack);
        return copy;
    }
}

/// <summary>A gstate object (LanguageLevel 2): a saved copy of a graphics state.</summary>
internal sealed class PsGStateObject
{
    public PsGState State;

    public PsGStateObject(PsGState state) => State = state;
}
