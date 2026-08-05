namespace ExtensionBlockFixture;

public interface IComStub
{
    int Version { get; set; }
}

public static class _VB6Extensions
{
    extension(IComStub stub)
    {
        // _Version is referenced from Program.Main via the extension-method
        // syntax (stub._Version). The whole _VB6Extensions type must be kept
        // even though nothing names the type itself.
        public int _Version
        {
            get => throw new System.NotImplementedException();
            set => throw new System.NotImplementedException();
        }

        // UnusedExtensionMember is never referenced; it must be pruned
        // individually without taking the whole extension block (or the whole
        // static class) with it.
        public int _UnusedExtensionMember
        {
            get => throw new System.NotImplementedException();
            set => throw new System.NotImplementedException();
        }
    }

    // A regular unused member on the same static class. It must be pruned
    // without affecting the extension block above.
    public static void UnusedRegularMethod() { }
}
