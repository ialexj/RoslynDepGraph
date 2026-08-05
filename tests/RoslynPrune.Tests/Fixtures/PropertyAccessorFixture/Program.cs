namespace PropertyAccessorFixture;

public static class Program
{
    public static void Main()
    {
        var service = new Service();
        service.UsedProperty = 42;
        _ = service.UsedProperty;
    }
}
