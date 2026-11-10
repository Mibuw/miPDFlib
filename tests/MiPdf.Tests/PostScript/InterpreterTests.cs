using System.Text;
using MiPdf.PostScript.Interpreter;

namespace MiPdf.Tests.PostScript;

/// <summary>
/// The PostScript language core. Resources/postscript/language.ps exercises the operators, the scanner, filters,
/// errors and save/restore; language.expected.txt is the output of Ghostscript 10.07 for it (gs -dNODISPLAY -dSAFER).
/// </summary>
public class InterpreterTests
{
    private static string Resource(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", "postscript", name);

    private static PsJobResult Run(string program, PsLimits? limits = null) =>
        new PsInterpreter(limits).Run(Encoding.Latin1.GetBytes(program));

    [Fact]
    public void LanguageCoreMatchesGhostscript()
    {
        var result = new PsInterpreter().Run(File.ReadAllBytes(Resource("language.ps")));
        Assert.Null(result.ErrorName);
        string expected = File.ReadAllText(Resource("language.expected.txt"), Encoding.Latin1).Replace("\r\n", "\n");
        var expectedLines = expected.Split('\n');
        var actualLines = result.Output.Split('\n');
        for (int i = 0; i < Math.Min(expectedLines.Length, actualLines.Length); i++)
            Assert.True(expectedLines[i] == actualLines[i], $"line {i + 1}: expected '{expectedLines[i]}', got '{actualLines[i]}'");
        Assert.Equal(expectedLines.Length, actualLines.Length);
    }

    [Theory]
    [InlineData("3 4 add 2 mul =", "14\n")]
    [InlineData("1 3 div = 1 3 div == 2 sqrt ==", "0.333333\n0.333333343\n1.41421354\n")]
    [InlineData("9223372036854775807 1 add == 16#FF = -7 2 idiv =", "9.22337204e+18\n255\n-3\n")]
    [InlineData("(a\\101\\nb) == <41 42> == <~87cURD]i,\"Ebo80~> =", "(aA\\nb)\n(AB)\nHello World!\n")]
    [InlineData("[1 /n (s) {x} 2.5] ==", "[1 /n (s) {x} 2.5]\n")]
    [InlineData("/f { dup 1 le { pop 1 } { dup 1 sub f mul } ifelse } def 10 f =", "3628800\n")]
    [InlineData("0 1 1 10 { add } for = [ 0 0.5 1.5 { } for ] ==", "55\n[0.0 0.5 1.0 1.5]\n")]
    public void EvaluatesPrograms(string program, string output)
    {
        var result = Run(program);
        Assert.Null(result.ErrorName);
        Assert.Equal(output, result.Output);
    }

    [Fact]
    public void AnUncaughtErrorEndsTheJob()
    {
        var result = Run("(before) = 1 0 div (after) =");
        Assert.Equal("undefinedresult", result.ErrorName);
        Assert.Equal("div", result.OffendingCommand);
        Assert.Equal("before\n%%[ Error: undefinedresult; OffendingCommand: div ]%%\n", result.Output);

        result = Run("unknownname");
        Assert.Equal("undefined", result.ErrorName);
        Assert.Equal("unknownname", result.OffendingCommand);
    }

    [Fact]
    public void ErrorsCanBeCaughtAndHandled()
    {
        var result = Run("{ 1 2 nosuchname } stopped = count = $error /errorname get = " +
                         "errordict /undefined { pop (handled) = } put nosuchname (continued) =");
        Assert.Null(result.ErrorName);
        Assert.Equal("true\n2\nundefined\nhandled\ncontinued\n", result.Output);
    }

    [Fact]
    public void RestoreUndoesChangesOfArraysAndDictionariesButNotStrings()
    {
        var result = Run("/s (abc) def /a [1 2] def /x 1 def save s 0 65 put a 0 9 put /x 2 def /y 3 def restore " +
                         "s = a == x = /y where = { save [1] restore } stopped =");
        Assert.Equal("Abc\n[1 2]\n1\nfalse\ntrue\n", result.Output);
    }

    [Fact]
    public void TheSandboxHasNoFileSystem()
    {
        foreach (string program in new[] { "(/etc/passwd) (r) file", "(out.ps) (w) file", "(x.ps) run", "(x) deletefile", "(a) (b) renamefile" })
            Assert.Equal("invalidfileaccess", Run(program).ErrorName);
        Assert.Equal("ok\n", Run("(%stdout) (w) file (ok\\n) writestring (%stdin) (r) file read { pop } if").Output);
        Assert.Equal("false\n", Run("(/etc/passwd) status =").Output);
    }

    [Fact]
    public void LimitsEndRunawayPrograms()
    {
        Assert.Throws<PdfException>(() => Run("{ } loop", new PsLimits { MaxOperations = 1_000_000 }));
        Assert.Throws<PdfException>(() => Run("/r { r } def r", new PsLimits { MaxOperations = 1_000_000 }));
        Assert.Throws<PdfException>(() => Run("{ } loop", new PsLimits { Timeout = TimeSpan.FromMilliseconds(200) }));
        // memory that stays reachable (each step keeps the previous chain and a new array)
        Assert.Throws<PdfException>(() => Run("/a [] def { [ a 100000 array ] /a exch def } loop",
            new PsLimits { MaxMemory = 64 << 20, MaxOperations = 100_000_000 }));
        // stack overflows are PostScript errors, which a program can catch
        Assert.Equal("stackoverflow", Run("{ 1 } loop", new PsLimits { MaxOperandStack = 10_000 }).ErrorName);
        Assert.Equal("execstackoverflow", Run("/r { r 1 } def r", new PsLimits { MaxExecStack = 1_000 }).ErrorName);
        Assert.Equal("dictstackoverflow", Run("{ 1 dict begin } loop").ErrorName);
        Assert.Equal("limitcheck", Run("2000000000 string").ErrorName);
        Assert.Equal("true\n", Run("{ { 1 } loop } stopped = clear", new PsLimits { MaxOperandStack = 10_000 }).Output);
    }

    [Fact]
    public void CyclicStructuresAreSafe()
    {
        var result = Run("/a 1 array def a 0 a put a == /p { p } def /p load dup 0 /p load put bind (done) =");
        Assert.Null(result.ErrorName);
        Assert.EndsWith("done\n", result.Output);
    }

    [Fact]
    public void FiltersReadOnlyTheirData()
    {
        // a filter on the program file stops at its end of data; the program continues after it
        var result = Run("currentfile /ASCIIHexDecode filter 10 string readstring\n414243>\npop = (next) =\n" +
                         "currentfile 0 (%%EOD) /SubFileDecode filter 100 string readstring\nline 1\nline 2\n%%EOD\npop == (after) =");
        Assert.Null(result.ErrorName);
        Assert.Equal("ABC\nnext\n(line 1\\nline 2\\n)\nafter\n", result.Output);
    }

    [Fact]
    public void DamagedProgramsDoNotCrash()
    {
        // random mutations of the language test: every outcome is a result or a limit, never another exception
        var program = File.ReadAllBytes(Resource("language.ps"));
        var random = new Random(5);
        for (int run = 0; run < 300; run++)
        {
            var data = (byte[])program.Clone();
            for (int k = 0; k < 1 + random.Next(8); k++) data[random.Next(data.Length)] = (byte)random.Next(256);
            if (run % 4 == 0) data = data.AsSpan(0, random.Next(data.Length)).ToArray();
            try
            {
                new PsInterpreter(new PsLimits { MaxOperations = 2_000_000, MaxMemory = 256 << 20 }).Run(data);
            }
            catch (PdfException)
            {
            }
        }
    }
}
