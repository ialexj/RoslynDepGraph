namespace ExtensionBlockFixture;

public static class Program
{
    public static void Main()
    {
        IComStub stub = null!;
        _ = stub._Version;
        stub._Version = 42;
    }
}
