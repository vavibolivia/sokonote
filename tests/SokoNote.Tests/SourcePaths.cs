using System.IO;
using System.Runtime.CompilerServices;

namespace SokoNote.Tests;

/// <summary>Locates the source tree from this file's compile-time path, independent of where the tests were built to.</summary>
internal static class SourcePaths
{
    public static string AppDir => Path.GetFullPath(Path.Combine(ThisDir(), "..", "..", "src", "SokoNote"));

    private static string ThisDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
