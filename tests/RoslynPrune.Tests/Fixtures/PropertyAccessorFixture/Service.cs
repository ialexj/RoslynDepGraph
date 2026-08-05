namespace PropertyAccessorFixture;

public class Service
{
    // UsedProperty is read and written by Program; it must be kept.
    public int UsedProperty { get; set; }

    // UnusedProperty is never accessed; the whole property (not just the
    // get/set accessors) must be removed.
    public int UnusedProperty { get; set; }

    public int UnusedReadOnly { get; }

    public int UnusedWriteOnly
    {
        set { _ = value; }
    }
}
