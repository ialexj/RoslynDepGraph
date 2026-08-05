namespace SelfReferenceFixture;

public static class Program
{
    public static void Main()
    {
        var used = new UsedType();
        used.DoWork();
    }
}
