// Program.cs intentionally has NO top-level using directives: a Roslyn/
// MSBuildWorkspace quirk means GetEntryPoint returns null for unbuilt Exe
// fixtures whose Main-containing file has a top-level using. Keeping this file
// using-free ensures entry-point detection works and the whole fixture is not
// pruned as unreferenced.
public static class Program
{
    public static void Main()
    {
        // Reference __AppObjects (and transitively VB.Global) so neither is
        // pruned; the only thing under test is that `global using VB;` survives.
        _ = __AppObjects.Global;
    }
}
